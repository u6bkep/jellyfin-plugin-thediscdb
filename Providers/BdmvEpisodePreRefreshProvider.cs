using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
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
///
/// When a "Replace all metadata" refresh corrupts the season number (because
/// FillMissingEpisodeNumbersFromPath misparses .mpls filenames like "00201.mpls" as
/// S02E01), this provider:
///   1. Restores the correct season/episode from the TheDiscDb provider ID
///   2. Clears the Name/Overview (which came from the wrong TMDB season)
///   3. Queues a follow-up FullRefresh (without ReplaceAllMetadata) so TMDB
///      re-fetches using the corrected numbers
/// </summary>
public class BdmvEpisodePreRefreshProvider : ICustomMetadataProvider<Episode>
{
    private readonly ILogger<BdmvEpisodePreRefreshProvider> _logger;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="BdmvEpisodePreRefreshProvider"/> class.
    /// </summary>
    public BdmvEpisodePreRefreshProvider(
        ILogger<BdmvEpisodePreRefreshProvider> logger,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
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

        // Format: "hash:playlist:SxxExx:Title"
        var parts = discDbId.Split(':', 4);
        if (parts.Length < 3)
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var episodeTag = parts[2]; // e.g., "S03E01" or "S01E01-E02"
        if (!TryParseEpisodeTag(episodeTag, out var seasonNumber, out var episodeNumber, out var episodeNumberEnd))
        {
            return Task.FromResult(ItemUpdateType.None);
        }

        var changed = ItemUpdateType.None;
        var seasonWasWrong = item.ParentIndexNumber != seasonNumber;

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

        if (item.IndexNumberEnd != episodeNumberEnd)
        {
            _logger.LogInformation(
                "TheDiscDb: Restoring episode end number {Old} -> {New} for {Path}",
                item.IndexNumberEnd,
                episodeNumberEnd,
                item.Path);
            item.IndexNumberEnd = episodeNumberEnd;
            changed = ItemUpdateType.MetadataEdit;
        }

        if (seasonWasWrong)
        {
            _logger.LogInformation(
                "TheDiscDb: Restoring season number {Old} -> {New} for {Path}",
                item.ParentIndexNumber,
                seasonNumber,
                item.Path);
            item.ParentIndexNumber = seasonNumber;
            changed = ItemUpdateType.MetadataEdit;

            // The season was wrong, which means TMDB fetched metadata from the wrong
            // season. Clear name/overview so the queued re-refresh can populate them
            // correctly. The merge logic fills in empty fields even with shouldReplace=false.
            _logger.LogInformation(
                "TheDiscDb: Season was wrong — clearing Name/Overview and queuing re-refresh for {Path}",
                item.Path);
            item.Name = null;
            item.Overview = null;

            var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ForceSave = true
            };
            _providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.Normal);
        }

        // Restore name from TheDiscDb if it's still a bare playlist filename
        if (parts.Length >= 4 && !string.IsNullOrEmpty(parts[3]))
        {
            if (item.Name is null || System.Text.RegularExpressions.Regex.IsMatch(item.Name, @"^\d{5}$"))
            {
                item.Name = parts[3];
                changed = ItemUpdateType.MetadataEdit;
            }
        }

        return Task.FromResult(changed);
    }

    private static bool TryParseEpisodeTag(string tag, out int season, out int episode, out int? episodeEnd)
    {
        season = 0;
        episode = 0;
        episodeEnd = null;

        // Parse "S03E01" or "S01E01-E02"
        var match = System.Text.RegularExpressions.Regex.Match(
            tag,
            @"^S(\d+)E(\d+)(?:-E(\d+))?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out season)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out episode))
        {
            return false;
        }

        if (match.Groups[3].Success
            && int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var endVal))
        {
            episodeEnd = endVal;
        }

        return true;
    }
}
