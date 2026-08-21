using Jellyfin.Plugin.FinTV.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FinTV;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        _ = applicationHost;
        serviceCollection.AddHttpClient("fintv");
        serviceCollection.AddSingleton<FinTvServerClient>();
        serviceCollection.AddSingleton<LiveTvRegistrar>();
        serviceCollection.AddHostedService<LiveTvRegistrationHostedService>();
    }
}
