using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TheDiscDb.Configuration;

/// <summary>
/// Configuration for TheDiscDb plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the path to a local clone of the TheDiscDb/data git repository.
    /// </summary>
    public string DataRepoPath { get; set; } = string.Empty;
}
