using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheDiscDb.Providers;

/// <summary>
/// Post-merge metadata provider that restores correct season/episode numbering
/// for BDMV episodes resolved by TheDiscDb.
///
/// Jellyfin's metadata pipeline overwrites our resolver's episode numbers by parsing
/// the m2ts filename and merging remote provider data with shouldReplace=true.
/// By running as a post-merge custom provider (NOT IPreRefreshProvider), we get the
/// final word on the episode numbering after all other providers have finished.
/// </summary>
public class BdmvEpisodePreRefreshProvider : ICustomMetadataProvider<Episode>
{
    private readonly ILogger<BdmvEpisodePreRefreshProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BdmvEpisodePreRefreshProvider"/> class.
    /// </summary>
    public BdmvEpisodePreRefreshProvider(ILogger<BdmvEpisodePreRefreshProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "TheDiscDb Episode Fix";

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Episode item, MetadataRefreshOptions options, CancellationToken cancellationToken)
    {
        if (!item.ProviderIds.TryGetValue("TheDiscDb", out var discDbId) || string.IsNullOrEmpty(discDbId))
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        _logger.LogInformation(
            "TheDiscDb PreRefresh: Processing {Name} (S{Season}E{Ep}) with ID={DiscDbId}",
            item.Name,
            item.ParentIndexNumber,
            item.IndexNumber,
            discDbId);

        // Format: "hash:playlist:SxxExx:Title"
        var parts = discDbId.Split(':', 4);
        if (parts.Length < 3)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var episodeTag = parts[2]; // e.g., "S03E01"
        if (!TryParseEpisodeTag(episodeTag, out var seasonNumber, out var episodeNumber))
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var changed = ItemUpdateType.None;

        if (item.IndexNumber != episodeNumber)
        {
            _logger.LogInformation(
                "TheDiscDb: Restoring episode number {Old} -> {New} for {Path}",
                item.IndexNumber,
                episodeNumber,
                item.Path);
            item.IndexNumber = episodeNumber;
            changed = ItemUpdateType.MetadataEdit;
        }

        if (item.ParentIndexNumber != seasonNumber)
        {
            _logger.LogInformation(
                "TheDiscDb: Restoring season number {Old} -> {New} for {Path}",
                item.ParentIndexNumber,
                seasonNumber,
                item.Path);
            item.ParentIndexNumber = seasonNumber;
            changed = ItemUpdateType.MetadataEdit;
        }

        // Restore name if it was set by TheDiscDb (part 4 of the provider ID)
        if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]))
        {
            var discDbName = parts[3];
            // Only restore if the current name looks wrong (matches the m2ts filename pattern)
            if (item.Name is null
                || item.Name.StartsWith("0", StringComparison.Ordinal)
                || !item.Name.Equals(discDbName, StringComparison.Ordinal))
            {
                // Don't override names that came from a remote provider (TMDB)
                // Only override if name is clearly wrong (filename-based)
                if (item.Name is null || System.Text.RegularExpressions.Regex.IsMatch(item.Name, @"^\d{5}$"))
                {
                    item.Name = discDbName;
                    changed = ItemUpdateType.MetadataEdit;
                }
            }
        }

        return Task.FromResult(changed);
    }

    private static bool TryParseEpisodeTag(string tag, out int season, out int episode)
    {
        season = 0;
        episode = 0;

        // Parse "S03E01"
        var match = System.Text.RegularExpressions.Regex.Match(
            tag,
            @"^S(\d+)E(\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return false;
        }

        return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out season)
            && int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out episode);
    }
}
