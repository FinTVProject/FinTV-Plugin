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
        BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode,
        BaseItemKind.MusicVideo, BaseItemKind.Audio, BaseItemKind.Playlist,
        BaseItemKind.Video, BaseItemKind.CollectionFolder
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

    public string Description => "Pushes Jellyfin library names to FinTV Server, then the libraries selected there.";

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
        var libraries = ListPushableLibraries();
        await PostLibraryListAsync(libraries, cancellationToken);
        progress.Report(5);

        var filter = await GetLibraryFilterAsync(cancellationToken);
        var libraryIds = ResolveLibraryIds(filter, libraries);
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

            batch.Add(Map(item, libraries));
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
                progress.Report(5 + (index * 90d / total));
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
        var libraries = ListPushableLibraries();
        if (libraries.Count == 0)
        {
            return new LibraryPushResult(false, "No TV, movie, music, or music video libraries found.");
        }

        await PostLibraryListAsync(libraries, cancellationToken);
        _logger.LogInformation("Sent {Count} Jellyfin libraries to FinTV Server.", libraries.Count);
        return new LibraryPushResult(true, $"Sent {libraries.Count} libraries to FinTV Server.");
    }

    private Task PostLibraryListAsync(
        IReadOnlyList<(VirtualFolderInfo Folder, Guid Id)> libraries,
        CancellationToken cancellationToken)
    {
        return _client.PostJsonAsync(
            "/api/plugin/libraries",
            new
            {
                libraries = libraries.Select(entry => new
                {
                    id = entry.Id,
                    name = entry.Folder.Name,
                    collectionType = entry.Folder.CollectionType?.ToString()
                })
            },
            cancellationToken);
    }

    private object Map(BaseItem item, IReadOnlyList<(VirtualFolderInfo Folder, Guid Id)> libraries)
    {
        var chapters = _chapters.GetChapters(item.Id)
            .Select(c => new { startPositionTicks = c.StartPositionTicks, name = c.Name })
            .ToList();

        Guid? parentId = item.ParentId == Guid.Empty ? null : item.ParentId;
        Guid? seriesId = null;
        string? seriesName = null;
        Guid? seasonId = null;
        string? seasonName = null;
        int? indexNumber = item.IndexNumber;
        int? parentIndexNumber = item.ParentIndexNumber;
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode)
        {
            seriesId = episode.SeriesId == Guid.Empty ? null : episode.SeriesId;
            seriesName = episode.SeriesName;
            seasonId = episode.SeasonId == Guid.Empty ? null : episode.SeasonId;
            seasonName = episode.SeasonName;
        }
        else if (item is MediaBrowser.Controller.Entities.TV.Season season)
        {
            seriesId = season.SeriesId == Guid.Empty ? null : season.SeriesId;
            seriesName = season.SeriesName;
            seasonId = season.Id;
            seasonName = season.Name;
        }

        var library = ResolveItemLibrary(item, libraries);
        var collectionType = library?.CollectionType
            ?? (item is CollectionFolder folder ? folder.CollectionType?.ToString() : null);

        var people = _libraryManager.GetPeople(item)
            .Select(person => new
            {
                name = person.Name,
                role = person.Role,
                type = person.Type.ToString()
            })
            .Take(25)
            .ToList();
        var stars = people
            .Where(person =>
                string.Equals(person.type, "Actor", StringComparison.OrdinalIgnoreCase)
                || string.Equals(person.type, "GuestStar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(person.type, "Guest Star", StringComparison.OrdinalIgnoreCase))
            .Select(person => person.name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();

        string[] artists = [];
        string[] albumArtists = [];
        if (item is MediaBrowser.Controller.Entities.Audio.Audio audio)
        {
            artists = audio.Artists?.ToArray() ?? [];
            albumArtists = audio.AlbumArtists?.ToArray() ?? [];
        }
        else if (item is MusicVideo musicVideo)
        {
            artists = musicVideo.Artists?.ToArray() ?? [];
        }

        var collections = _libraryManager.GetCollectionFolders(item)
            .Select(collection => collection.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var providerIds = item.ProviderIds?
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new
        {
            id = item.Id,
            jellyfinId = item.Id,
            name = item.Name,
            sortName = item.SortName,
            overview = item.Overview,
            plot = item.Overview,
            kind = MapKind(item),
            path = item.Path,
            jellyfinPath = item.Path,
            parentId,
            seriesId,
            seriesName,
            seasonId,
            seasonName,
            productionYear = item.ProductionYear,
            premiereDate = item.PremiereDate,
            officialRating = item.OfficialRating,
            communityRating = item.CommunityRating,
            criticRating = item.CriticRating,
            customRating = item.CustomRating,
            runtimeTicks = item.RunTimeTicks,
            runtime = FormatRuntime(item.RunTimeTicks),
            indexNumber,
            parentIndexNumber,
            libraryId = library?.Id ?? item.GetTopParent()?.Id,
            libraryName = library?.Name ?? item.GetTopParent()?.Name,
            collectionType,
            mediaType = item.MediaType.ToString(),
            album = item.Album,
            primaryImagePath = item.HasImage(ImageType.Primary) ? item.GetImagePath(ImageType.Primary) : null,
            genres = item.Genres ?? Array.Empty<string>(),
            tags = item.Tags ?? Array.Empty<string>(),
            studios = item.Studios ?? Array.Empty<string>(),
            collectionNames = collections,
            artists,
            albumArtists,
            people,
            stars,
            providerIds,
            chapters
        };
    }

    private static (Guid Id, string Name, string? CollectionType)? ResolveItemLibrary(
        BaseItem item,
        IReadOnlyList<(VirtualFolderInfo Folder, Guid Id)> libraries)
    {
        var folder = FindCollectionFolder(item);
        if (folder is not null)
        {
            var byId = libraries.FirstOrDefault(entry => entry.Id == folder.Id);
            if (byId.Id != Guid.Empty)
            {
                return (byId.Id, byId.Folder.Name, byId.Folder.CollectionType?.ToString());
            }

            var byName = libraries.FirstOrDefault(entry =>
                entry.Folder.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase));
            if (byName.Id != Guid.Empty)
            {
                return (byName.Id, byName.Folder.Name, byName.Folder.CollectionType?.ToString());
            }

            return (folder.Id, folder.Name, folder.CollectionType?.ToString());
        }

        var top = item.GetTopParent();
        if (top is null)
        {
            return null;
        }

        var match = libraries.FirstOrDefault(entry => entry.Id == top.Id);
        if (match.Id != Guid.Empty)
        {
            return (match.Id, match.Folder.Name, match.Folder.CollectionType?.ToString());
        }

        return null;
    }

    private static CollectionFolder? FindCollectionFolder(BaseItem item)
    {
        BaseItem? current = item;
        for (var i = 0; i < 16 && current is not null; i++)
        {
            if (current is CollectionFolder folder)
            {
                return folder;
            }

            current = current.GetParent();
        }

        return item.GetTopParent() as CollectionFolder;
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
        if (item is MediaBrowser.Controller.Entities.TV.Season) return 8;
        return 7;
    }

    private static string? FormatRuntime(long? ticks)
    {
        if (ticks is not > 0)
        {
            return null;
        }

        var time = TimeSpan.FromTicks(ticks.Value);
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours}h {time.Minutes:00}m";
        }

        if (time.TotalMinutes >= 1)
        {
            return $"{(int)time.TotalMinutes}m {time.Seconds:00}s";
        }

        return $"{time.Seconds}s";
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

    private static HashSet<Guid> ResolveLibraryIds(
        LibrarySyncFilter? filter,
        IReadOnlyList<(VirtualFolderInfo Folder, Guid Id)> folders)
    {
        var ids = new HashSet<Guid>();
        ids.UnionWith(Pick(filter?.TvLibraryIds, folders, CollectionType.tvshows));
        ids.UnionWith(Pick(filter?.MovieLibraryIds, folders, CollectionType.movies));
        ids.UnionWith(Pick(filter?.MusicLibraryIds, folders, CollectionType.music));
        ids.UnionWith(Pick(filter?.MusicVideoLibraryIds, folders, CollectionType.musicvideos));
        ids.UnionWith(Pick(filter?.HomeVideoLibraryIds, folders, CollectionType.homevideos));
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
            || MatchesType(folder, CollectionType.musicvideos)
            || MatchesType(folder, CollectionType.homevideos);
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

        public List<Guid> HomeVideoLibraryIds { get; set; } = [];
    }
}

public sealed record LibraryPushResult(bool Ok, string Message);
