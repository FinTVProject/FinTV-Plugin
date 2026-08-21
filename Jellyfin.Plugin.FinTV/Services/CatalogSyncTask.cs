using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public sealed class CatalogSyncTask : IScheduledTask
{
    private static readonly BaseItemKind[] ItemKinds =
    [
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode,
        BaseItemKind.MusicVideo, BaseItemKind.Audio, BaseItemKind.Playlist,
        BaseItemKind.CollectionFolder
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IChapterManager _chapters;
    private readonly FinTvServerClient _client;
    private readonly ILogger<CatalogSyncTask> _logger;

    public CatalogSyncTask(
        ILibraryManager libraryManager,
        IChapterManager chapters,
        FinTvServerClient client,
        ILogger<CatalogSyncTask> logger)
    {
        _libraryManager = libraryManager;
        _chapters = chapters;
        _client = client;
        _logger = logger;
    }

    public string Name => "FinTV Catalog Sync";

    public string Key => "FinTVCatalogSync";

    public string Description => "Pushes the Jellyfin libraries selected on FinTV Server, not the entire Jellyfin library.";

    public string Category => "FinTV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var filter = await GetLibraryFilterAsync(cancellationToken);
        var libraryIds = ResolveLibraryIds(filter);
        if (libraryIds.Count == 0)
        {
            _logger.LogWarning("FinTV catalog sync found no matching libraries. Select TV/movie/music libraries on FinTV Server.");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("FinTV catalog sync using {Count} libraries selected by FinTV Server.", libraryIds.Count);

        var items = new List<BaseItem>();
        foreach (var libraryId in libraryIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = _libraryManager.GetItemById(libraryId);
            if (folder is CollectionFolder)
            {
                items.Add(folder);
            }

            var children = _libraryManager.GetItemsResult(new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false,
                IncludeItemTypes = ItemKinds,
                TopParentIds = [libraryId]
            });
            items.AddRange(children.Items);
        }

        const int batchSize = 100;
        var batch = new List<object>(batchSize);
        var total = Math.Max(1, items.Count);
        var index = 0;
        var sentAny = false;
        var seen = new HashSet<Guid>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(item.Id))
            {
                continue;
            }

            batch.Add(Map(item));
            index++;
            if (batch.Count >= batchSize)
            {
                await _client.PostJsonAsync(
                    "/api/plugin/catalog",
                    new { replaceAll = false, items = batch.ToList() },
                    cancellationToken);
                sentAny = true;
                batch.Clear();
            }

            if (index % 250 == 0)
            {
                progress.Report(index * 90d / total);
            }
        }

        if (batch.Count > 0 || !sentAny)
        {
            await _client.PostJsonAsync(
                "/api/plugin/catalog",
                new { replaceAll = false, items = batch.ToList() },
                cancellationToken);
        }

        progress.Report(100);
    }

    public async Task PushChaptersAsync(Guid itemId, IReadOnlyList<(TimeSpan Start, string Name)> chapters, CancellationToken cancellationToken)
    {
        var payload = chapters.Select(c => new { startPositionTicks = c.Start.Ticks, name = c.Name }).ToList();
        await _client.PatchJsonAsync($"/api/plugin/catalog/{itemId:N}/chapters", payload, cancellationToken);
    }

    public async Task<LibraryPushResult> PushLibrariesAsync(CancellationToken cancellationToken)
    {
        var items = ListPushableLibraries()
            .Select(entry => MapLibrary(entry.Folder, entry.Id))
            .ToList();
        if (items.Count == 0)
        {
            return new LibraryPushResult(false, "No TV, movie, or music libraries found.");
        }

        await _client.PostJsonAsync(
            "/api/plugin/catalog",
            new { replaceAll = false, items },
            cancellationToken);

        _logger.LogInformation("Sent {Count} Jellyfin libraries to FinTV Server.", items.Count);
        return new LibraryPushResult(true, $"Sent {items.Count} libraries to FinTV Server.");
    }

    private object Map(BaseItem item)
    {
        var chapters = _chapters.GetChapters(item.Id)
            .Select(c => new { startPositionTicks = c.StartPositionTicks, name = c.Name })
            .ToList();

        Guid? parentId = item.ParentId == Guid.Empty ? null : item.ParentId;
        Guid? seriesId = null;
        string? seriesName = null;
        int? indexNumber = item.IndexNumber;
        int? parentIndexNumber = item.ParentIndexNumber;
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            seriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId;
            seriesName = episode.SeriesName;
        }

        string? collectionType = null;
        if (item is CollectionFolder folder)
        {
            collectionType = folder.CollectionType?.ToString();
        }

        return new
        {
            id = item.Id,
            name = item.Name,
            sortName = item.SortName,
            overview = item.Overview,
            kind = MapKind(item),
            path = item.Path,
            parentId,
            seriesId,
            seriesName,
            productionYear = item.ProductionYear,
            premiereDate = item.PremiereDate,
            officialRating = item.OfficialRating,
            runtimeTicks = item.RunTimeTicks,
            indexNumber,
            parentIndexNumber,
            libraryId = item.GetTopParent()?.Id,
            libraryName = item.GetTopParent()?.Name,
            collectionType,
            primaryImagePath = item.HasImage(ImageType.Primary) ? item.GetImagePath(ImageType.Primary) : null,
            genres = item.Genres ?? Array.Empty<string>(),
            tags = item.Tags ?? Array.Empty<string>(),
            studios = item.Studios ?? Array.Empty<string>(),
            collectionNames = Array.Empty<string>(),
            chapters
        };
    }

    private static int MapKind(BaseItem item)
    {
        if (item is MediaBrowser.Controller.Entities.Movies.Movie) return 0;
        if (item is MediaBrowser.Controller.Entities.TV.Series) return 1;
        if (item is MediaBrowser.Controller.Entities.TV.Episode) return 2;
        if (item.GetBaseItemKind() == BaseItemKind.MusicVideo) return 3;
        if (item is MediaBrowser.Controller.Entities.Audio.Audio) return 4;
        if (item.GetBaseItemKind() == BaseItemKind.Playlist) return 5;
        if (item is CollectionFolder) return 6;
        return 7;
    }

    private async Task<LibrarySyncFilter?> GetLibraryFilterAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetJsonAsync<LibrarySyncFilter>("/api/plugin/library-sync", cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read FinTV library-sync settings; falling back to TV/movie/music libraries.");
            return null;
        }
    }

    private object MapLibrary(VirtualFolderInfo folder, Guid id)
    {
        var item = _libraryManager.GetItemById(id);
        var path = (folder.Locations ?? []).FirstOrDefault() ?? item?.Path;
        return new
        {
            id,
            name = folder.Name,
            sortName = item?.SortName ?? folder.Name,
            overview = item?.Overview,
            kind = 6,
            path,
            parentId = (Guid?)null,
            libraryId = id,
            libraryName = folder.Name,
            collectionType = folder.CollectionType?.ToString(),
            primaryImagePath = item is not null && item.HasImage(ImageType.Primary)
                ? item.GetImagePath(ImageType.Primary)
                : null,
            genres = Array.Empty<string>(),
            tags = Array.Empty<string>(),
            studios = Array.Empty<string>(),
            collectionNames = Array.Empty<string>(),
            chapters = Array.Empty<object>()
        };
    }

    private List<(VirtualFolderInfo Folder, Guid Id)> ListPushableLibraries()
    {
        return _libraryManager.GetVirtualFolders()
            .Select(folder => (
                Folder: folder,
                Id: Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty))
            .Where(entry =>
                entry.Id != Guid.Empty
                && !IsPluginLibrary(entry.Folder)
                && IsKnownLibraryType(entry.Folder))
            .ToList();
    }

    private HashSet<Guid> ResolveLibraryIds(LibrarySyncFilter? filter)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Select(folder => (
                Folder: folder,
                Id: Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty))
            .Where(entry => entry.Id != Guid.Empty)
            .ToList();

        var ids = new HashSet<Guid>();
        ids.UnionWith(Pick(filter?.TvLibraryIds, folders, CollectionType.tvshows));
        ids.UnionWith(Pick(filter?.MovieLibraryIds, folders, CollectionType.movies));
        ids.UnionWith(Pick(filter?.MusicLibraryIds, folders, CollectionType.music));
        ids.UnionWith(Pick(filter?.MusicVideoLibraryIds, folders, CollectionType.musicvideos));
        return ids;
    }

    private static HashSet<Guid> Pick(
        IReadOnlyList<Guid>? selected,
        IReadOnlyList<(VirtualFolderInfo Folder, Guid Id)> folders,
        CollectionType type)
    {
        var wanted = (selected ?? [])
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (wanted.Count > 0)
        {
            return wanted;
        }

        return folders
            .Where(entry => MatchesType(entry.Folder, type) && !IsPluginLibrary(entry.Folder))
            .Select(entry => entry.Id)
            .ToHashSet();
    }

    private static bool IsKnownLibraryType(VirtualFolderInfo folder)
    {
        return MatchesType(folder, CollectionType.tvshows)
            || MatchesType(folder, CollectionType.movies)
            || MatchesType(folder, CollectionType.music)
            || MatchesType(folder, CollectionType.musicvideos);
    }

    private static bool MatchesType(VirtualFolderInfo folder, CollectionType type)
    {
        var value = folder.CollectionType?.ToString();
        return string.Equals(value, type.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPluginLibrary(VirtualFolderInfo folder)
    {
        return (folder.Locations ?? []).Any(path =>
            path.Contains("/plugins/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("\\plugins\\", StringComparison.OrdinalIgnoreCase)
            || path.Contains("virtual-libraries", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class LibrarySyncFilter
    {
        public List<Guid> TvLibraryIds { get; set; } = [];

        public List<Guid> MovieLibraryIds { get; set; } = [];

        public List<Guid> MusicLibraryIds { get; set; } = [];

        public List<Guid> MusicVideoLibraryIds { get; set; } = [];
    }
}

public sealed record LibraryPushResult(bool Ok, string Message);
