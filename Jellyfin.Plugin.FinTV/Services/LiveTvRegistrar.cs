using System.Globalization;
using Jellyfin.Plugin.FinTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FinTV.Services;

public sealed class LiveTvRegistrar
{
    public const string TunerFriendlyName = "FinTV";
    public const string M3uType = "m3u";
    public const string XmlTvType = "xmltv";

    private readonly FinTvServerClient _client;
    private readonly ITunerHostManager _tunerHosts;
    private readonly IListingsManager _listings;
    private readonly IConfigurationManager _config;
    private readonly ILogger<LiveTvRegistrar> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LiveTvRegistrar(
        FinTvServerClient client,
        ITunerHostManager tunerHosts,
        IListingsManager listings,
        IConfigurationManager config,
        ILogger<LiveTvRegistrar> logger)
    {
        _client = client;
        _tunerHosts = tunerHosts;
        _listings = listings;
        _config = config;
        _logger = logger;
        Instance = this;
    }

    public static LiveTvRegistrar? Instance { get; private set; }

    public async Task<LiveTvRegistrationResult> RegisterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RegisterCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LiveTvRegistrationResult> RegisterCoreAsync(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance;
        var config = plugin?.Configuration;
        if (config is null)
        {
            return new LiveTvRegistrationResult(false, "FinTV plugin is not loaded.");
        }

        if (!config.AutoRegisterLiveTv)
        {
            return new LiveTvRegistrationResult(true, "Auto-register Live TV is disabled.");
        }

        if (string.IsNullOrWhiteSpace(config.ServerUrl))
        {
            return new LiveTvRegistrationResult(false, "Set a FinTV Server URL before registering Live TV.");
        }

        var urls = await _client.GetJsonAsync<LiveTvUrls>("/api/plugin/live-tv-urls", cancellationToken).ConfigureAwait(false);
        if (urls is null || (string.IsNullOrWhiteSpace(urls.M3u) && string.IsNullOrWhiteSpace(urls.Epg)))
        {
            return new LiveTvRegistrationResult(false, "FinTV Server did not return M3U/XMLTV URLs.");
        }

        var m3uUrl = EnsureApiKey(RewriteToServer(urls.M3u, config.ServerUrl), config.ApiKey);
        var epgUrl = EnsureApiKey(RewriteToServer(urls.Epg, config.ServerUrl), config.ApiKey);
        if (string.IsNullOrWhiteSpace(m3uUrl) || string.IsNullOrWhiteSpace(epgUrl))
        {
            return new LiveTvRegistrationResult(false, "FinTV Server returned an empty M3U or XMLTV URL.");
        }

        var liveTv = (LiveTvOptions)_config.GetConfiguration("livetv");
        var tuner = FindExistingTuner(liveTv, config) ?? new TunerHostInfo
        {
            FriendlyName = TunerFriendlyName,
            Type = M3uType
        };
        tuner.Type = M3uType;
        tuner.FriendlyName = TunerFriendlyName;
        tuner.Url = m3uUrl;

        try
        {
            tuner = await _tunerHosts.SaveTunerHost(tuner).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FinTV M3U tuner validation failed; saving the tuner URL without validation");
            tuner = PersistTuner(tuner);
        }

        var listings = FindExistingListings(liveTv, config) ?? new ListingsProviderInfo();
        listings.Type = XmlTvType;
        listings.Path = epgUrl;
        listings.EnableAllTuners = false;
        listings.EnabledTuners = [tuner.Id];

        try
        {
            listings = await _listings.SaveListingProvider(listings, validateLogin: false, validateListings: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FinTV XMLTV provider save failed; writing the guide URL into Live TV config");
            listings = PersistListings(listings);
        }

        RememberIds(plugin!, config, tuner.Id, listings.Id);

        _logger.LogInformation(
            "Registered FinTV Live TV: M3U {M3u} XMLTV {Epg}",
            RedactQuery(m3uUrl),
            RedactQuery(epgUrl));

        return new LiveTvRegistrationResult(true, "Registered FinTV M3U tuner and XMLTV guide in Dashboard → Live TV.");
    }

    private TunerHostInfo PersistTuner(TunerHostInfo info)
    {
        var liveTv = (LiveTvOptions)_config.GetConfiguration("livetv");
        var list = (liveTv.TunerHosts ?? []).ToList();
        var index = list.FindIndex(i => string.Equals(i.Id, info.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            list.Add(info);
        }
        else
        {
            list[index] = info;
        }

        liveTv.TunerHosts = list.ToArray();
        _config.SaveConfiguration("livetv", liveTv);
        return info;
    }

    private ListingsProviderInfo PersistListings(ListingsProviderInfo info)
    {
        var liveTv = (LiveTvOptions)_config.GetConfiguration("livetv");
        var list = (liveTv.ListingProviders ?? []).ToList();
        var index = list.FindIndex(i => string.Equals(i.Id, info.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            list.Add(info);
        }
        else
        {
            list[index] = info;
        }

        liveTv.ListingProviders = list.ToArray();
        _config.SaveConfiguration("livetv", liveTv);
        return info;
    }

    private static TunerHostInfo? FindExistingTuner(LiveTvOptions liveTv, PluginConfiguration config)
    {
        var hosts = liveTv.TunerHosts ?? [];
        if (!string.IsNullOrWhiteSpace(config.LiveTvTunerId))
        {
            var byId = hosts.FirstOrDefault(h =>
                string.Equals(h.Id, config.LiveTvTunerId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        return hosts.FirstOrDefault(h =>
            string.Equals(h.FriendlyName, TunerFriendlyName, StringComparison.OrdinalIgnoreCase)
            || (h.Url is not null && h.Url.Contains("/iptv/channels.m3u", StringComparison.OrdinalIgnoreCase)));
    }

    private static ListingsProviderInfo? FindExistingListings(LiveTvOptions liveTv, PluginConfiguration config)
    {
        var providers = liveTv.ListingProviders ?? [];
        if (!string.IsNullOrWhiteSpace(config.LiveTvListingsId))
        {
            var byId = providers.FirstOrDefault(p =>
                string.Equals(p.Id, config.LiveTvListingsId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        return providers.FirstOrDefault(p =>
            string.Equals(p.Type, XmlTvType, StringComparison.OrdinalIgnoreCase)
            && p.Path is not null
            && p.Path.Contains("/iptv/epg", StringComparison.OrdinalIgnoreCase));
    }

    private static void RememberIds(Plugin plugin, PluginConfiguration config, string tunerId, string listingsId)
    {
        if (string.Equals(config.LiveTvTunerId, tunerId, StringComparison.Ordinal)
            && string.Equals(config.LiveTvListingsId, listingsId, StringComparison.Ordinal))
        {
            return;
        }

        config.LiveTvTunerId = tunerId;
        config.LiveTvListingsId = listingsId;
        plugin.SaveConfiguration();
    }

    internal static string RewriteToServer(string sourceUrl, string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl;
        }

        if (!Uri.TryCreate(serverUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var server))
        {
            return sourceUrl;
        }

        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
        {
            return new Uri(server, sourceUrl.TrimStart('/')).ToString();
        }

        return new Uri(server, source.PathAndQuery).ToString();
    }

    internal static string EnsureApiKey(string url, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (uri.Query.Contains("apiKey=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var builder = new UriBuilder(uri);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing)
            ? "apiKey=" + Uri.EscapeDataString(apiKey)
            : existing + "&apiKey=" + Uri.EscapeDataString(apiKey);
        return builder.Uri.ToString();
    }

    internal static string RedactQuery(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Query))
        {
            return url;
        }

        return uri.GetLeftPart(UriPartial.Path);
    }

    private sealed class LiveTvUrls
    {
        public string M3u { get; set; } = string.Empty;

        public string Epg { get; set; } = string.Empty;
    }
}

public sealed record LiveTvRegistrationResult(bool Ok, string Message);
