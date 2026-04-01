using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using Jellyfin.Plugin.TheDiscDb.Configuration;

namespace Jellyfin.Plugin.TheDiscDb;

/// <summary>
/// TheDiscDb plugin for Jellyfin.
/// Resolves BDMV Blu-ray folders into individual episodes using
/// playlist-to-episode mappings from thediscdb.com.
/// </summary>
public class TheDiscDbPlugin : BasePlugin<PluginConfiguration>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TheDiscDbPlugin"/> class.
    /// </summary>
    public TheDiscDbPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static TheDiscDbPlugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "TheDiscDb";

    /// <inheritdoc />
    public override string Description => "Resolves BDMV folders into individual episodes using TheDiscDb.";

    /// <inheritdoc />
    public override Guid Id => new("a1b2c3d4-5678-9abc-def0-123456789abc");
}
