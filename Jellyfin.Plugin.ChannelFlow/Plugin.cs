using System.Globalization;
using Jellyfin.Plugin.ChannelFlow.Configuration;
using Jellyfin.Plugin.ChannelFlow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ChannelFlow;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasEmbeddedImage
{
    public const string PluginImageResourceName = "Jellyfin.Plugin.ChannelFlow.logo.png";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public string ImageResourceName => PluginImageResourceName;

    public override string Name => "ChannelFlow-Jellyfin";

    public override Guid Id => Guid.Parse("f4e8a2b1-3c5d-4e6f-9a8b-7c6d5e4f3a2b");

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        var registrar = LiveTvRegistrar.Instance;
        if (registrar is not null)
        {
            _ = registrar.RegisterAsync(CancellationToken.None);
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var resourcePrefix = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.", GetType().Namespace);
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EnableInMainMenu = true,
                EmbeddedResourcePath = resourcePrefix + "configPage.html"
            }
        ];
    }
}
