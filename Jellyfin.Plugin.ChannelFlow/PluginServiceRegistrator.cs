using Jellyfin.Plugin.ChannelFlow.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.ChannelFlow;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;
        serviceCollection.AddHttpClient("channelflow");
        serviceCollection.AddSingleton<ChannelFlowServerClient>();
        serviceCollection.AddSingleton<LiveTvRegistrar>();
        serviceCollection.AddHostedService<LiveTvRegistrationHostedService>();
    }
}
