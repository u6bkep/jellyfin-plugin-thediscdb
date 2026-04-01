using System.IO;
using Jellyfin.Plugin.TheDiscDb.TheDiscDb;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheDiscDb;

/// <summary>
/// Registers plugin services with the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp =>
        {
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger(nameof(TheDiscDbClient));
            var appPaths = sp.GetRequiredService<IApplicationPaths>();
            var cacheDir = Path.Combine(appPaths.CachePath, "thediscdb");
            return new TheDiscDbClient(logger, cacheDir);
        });

        // BdmvEpisodeResolver is discovered via assembly scanning (GetExportTypes<IItemResolver>)
        // and instantiated by ActivatorUtilities.CreateInstance, which resolves constructor
        // params from the DI container. We only need to register TheDiscDbClient here.
    }
}
