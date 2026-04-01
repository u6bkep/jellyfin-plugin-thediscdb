using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TheDiscDb.Configuration;

/// <summary>
/// Configuration for TheDiscDb plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TheDiscDb GraphQL API endpoint.
    /// </summary>
    public string ApiEndpoint { get; set; } = "https://thediscdb.com/graphql";

    /// <summary>
    /// Gets or sets the cache duration in hours for TheDiscDb lookups.
    /// </summary>
    public int CacheHours { get; set; } = 168; // 1 week
}
