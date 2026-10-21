using Jellyfin.Plugin.Mediarium.Api;
using Jellyfin.Plugin.Mediarium.Discover;
using Jellyfin.Plugin.Mediarium.Events;
using Jellyfin.Plugin.Mediarium.Injection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Mediarium;

/// <summary>
/// Registers the plugin's services with Jellyfin.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MediariumClient>();
        serviceCollection.AddSingleton<NoticeStore>();
        serviceCollection.AddSingleton<EventProcessor>();
        serviceCollection.AddSingleton<IChannel, DiscoverChannel>();
        serviceCollection.AddHostedService<EventPoller>();
        serviceCollection.AddHostedService<FavouriteWatcher>();
        serviceCollection.AddTransient<IStartupFilter, WebInjectionStartupFilter>();
    }
}
