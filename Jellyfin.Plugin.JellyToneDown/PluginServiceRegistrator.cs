using Jellyfin.Plugin.JellyToneDown.Middleware;
using Jellyfin.Plugin.JellyToneDown.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyToneDown;

/// <summary>
/// Registers the plugin's services with Jellyfin's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<GainCacheService>();
        serviceCollection.AddSingleton<ScriptInjectionService>();

        // Adds the theme-gain middleware to the front of the request pipeline.
        serviceCollection.AddSingleton<IStartupFilter, ThemeGainStartupFilter>();

        // Patches (or un-patches) the web client at startup and whenever the config changes.
        serviceCollection.AddHostedService<ScriptInjectionService>(
            static provider => provider.GetRequiredService<ScriptInjectionService>());
    }
}
