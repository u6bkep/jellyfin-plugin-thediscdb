using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TheDiscDb.Parsers;
using Jellyfin.Plugin.TheDiscDb.TheDiscDb;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Video = MediaBrowser.Controller.Entities.Video;

namespace Jellyfin.Plugin.TheDiscDb.Resolvers;

/// <summary>
/// Resolves BDMV folders inside TV show Season directories into individual Episode items
/// by looking up the disc in TheDiscDb and mapping playlists to episodes.
///
/// Each episode gets its Path set to the m2ts file(s) from its specific playlist,
/// so Jellyfin treats them as regular video files — no custom playback pipeline needed.
/// </summary>
public class BdmvEpisodeResolver : IItemResolver, IMultiItemResolver
{
    private readonly ILogger<BdmvEpisodeResolver> _logger;
    private readonly TheDiscDbClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="BdmvEpisodeResolver"/> class.
    /// </summary>
    public BdmvEpisodeResolver(ILogger<BdmvEpisodeResolver> logger, TheDiscDbClient client)
    {
        _logger = logger;
        _client = client;
    }

    /// <inheritdoc />
    public ResolverPriority Priority => ResolverPriority.Plugin;

    /// <inheritdoc />
    public BaseItem? ResolvePath(ItemResolveArgs args)
    {
        // When a folder inside a Series contains a BDMV subfolder, the built-in
        // EpisodeResolver would grab it as a single Episode (since it runs before
        // SeasonResolver). We intercept here to return a Season instead, so that
        // when Jellyfin later scans the Season's children, our ResolveMultiple
        // can split the BDMV into individual episodes.
        if (args.IsDirectory && args.Parent is Series series)
        {
            var hasBdmv = args.FileSystemChildren.Any(c =>
                c.IsDirectory && c.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase));

            if (hasBdmv)
            {
                var seasonNumber = ParseSeasonNumber(args.Path);
                if (seasonNumber.HasValue)
                {
                    _logger.LogInformation(
                        "TheDiscDb: Intercepting BDMV folder {Path} as Season {Number} (preventing EpisodeResolver)",
                        args.Path,
                        seasonNumber.Value);

                    return new Season
                    {
                        Path = args.Path,
                        IndexNumber = seasonNumber.Value,
                        SeriesId = series.Id,
                        SeriesName = series.Name,
                        // Lock the Season so child episodes inherit IsLocked=true.
                        // This prevents FillMissingEpisodeNumbersFromPath and TMDB
                        // from overwriting our TheDiscDb-sourced metadata.
                        IsLocked = true
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts a season number from a folder name like "Season 3".
    /// </summary>
    private static int? ParseSeasonNumber(string path)
    {
        var folderName = Path.GetFileName(path);
        if (folderName is null)
        {
            return null;
        }

        // Match common patterns: "Season 3", "Season03", "S03", "S3"
        var match = System.Text.RegularExpressions.Regex.Match(
            folderName,
            @"(?:season|s)\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num))
        {
            return num;
        }

        return null;
    }

    /// <inheritdoc />
    public MultiItemResolverResult ResolveMultiple(
        Folder parent,
        List<FileSystemMetadata> files,
        CollectionType? collectionType,
        IDirectoryService directoryService)
    {
        // Parent must be a Season (or a Series, for flat layouts) in a TV show library.
        // collectionType can be null for nested folders, so also check the parent type.
        var season = parent as Season;
        var series = parent as Series ?? parent.GetParents().OfType<Series>().FirstOrDefault();
        if (season is null && series is null)
        {
            return new MultiItemResolverResult();
        }

        if (collectionType.HasValue && collectionType != CollectionType.tvshows)
        {
            return new MultiItemResolverResult();
        }

        // Find BDMV subfolder in the file list
        var bdmvFolder = files.FirstOrDefault(f =>
            f.IsDirectory && f.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase));

        if (bdmvFolder is null)
        {
            return new MultiItemResolverResult();
        }

        // The BDMV's parent directory is the "disc root"
        var discRoot = Path.GetDirectoryName(bdmvFolder.FullName) ?? parent.Path;

        _logger.LogInformation("TheDiscDb: Found BDMV folder at {Path}, computing ContentHash...", discRoot);

        var contentHash = ContentHashCalculator.ComputeHash(discRoot);
        if (contentHash is null)
        {
            _logger.LogWarning("TheDiscDb: Could not compute ContentHash for {Path}", discRoot);
            return new MultiItemResolverResult();
        }

        _logger.LogInformation("TheDiscDb: ContentHash = {Hash}", contentHash);

        // Query TheDiscDb (synchronous wrapper — resolver API is synchronous)
        var disc = _client.GetDiscByHashAsync(contentHash).GetAwaiter().GetResult();
        if (disc is null)
        {
            _logger.LogInformation("TheDiscDb: No match found for hash {Hash}, falling through to default resolver", contentHash);
            return new MultiItemResolverResult();
        }

        var episodes = TheDiscDbClient.GetEpisodes(disc);
        var extras = TheDiscDbClient.GetExtras(disc);

        if (episodes.Count == 0 && extras.Count == 0)
        {
            _logger.LogInformation("TheDiscDb: Disc {Hash} matched but contains no episodes or extras, falling through", contentHash);
            return new MultiItemResolverResult();
        }

        _logger.LogInformation("TheDiscDb: Found {EpCount} episodes and {ExCount} extras on disc {Name}", episodes.Count, extras.Count, disc.Name);

        var result = new MultiItemResolverResult();
        var streamDir = Path.Combine(discRoot, "BDMV", "STREAM");

        foreach (var title in episodes)
        {
            var episode = CreateEpisode(title, discRoot, streamDir, contentHash, season, series);
            if (episode is not null)
            {
                result.Items.Add(episode);
            }
        }

        foreach (var title in extras)
        {
            var extra = CreateExtra(title, discRoot, streamDir, contentHash, season);
            if (extra is not null)
            {
                result.Items.Add(extra);
            }
        }

        // Pass non-BDMV files through to normal resolvers (e.g., standalone MKV episodes in the same folder)
        result.ExtraFiles = files
            .Where(f => !f.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                     && !f.Name.Equals("CERTIFICATE", StringComparison.OrdinalIgnoreCase))
            .ToList();

        _logger.LogInformation("TheDiscDb: Resolved {EpCount} episodes and {ExCount} extras from BDMV at {Path}", episodes.Count, extras.Count, discRoot);
        return result;
    }

    private Episode? CreateEpisode(
        DiscTitle title,
        string discRoot,
        string streamDir,
        string contentHash,
        Season? season,
        Series? series)
    {
        if (title.Item is null || title.SourceFile is null)
        {
            return null;
        }

        // Resolve the m2ts files for this episode's playlist
        var m2tsFiles = ResolvePlaylistFiles(discRoot, title.SourceFile, title.SegmentMap, streamDir);
        if (m2tsFiles.Count == 0)
        {
            _logger.LogWarning("TheDiscDb: No m2ts files found for playlist {Playlist}", title.SourceFile);
            return null;
        }

        if (!int.TryParse(title.Item.Season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonNumber))
        {
            return null;
        }

        if (!int.TryParse(title.Item.Episode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episodeNumber))
        {
            return null;
        }

        var episode = new Episode
        {
            Path = m2tsFiles[0],
            VideoType = VideoType.VideoFile,
            ParentIndexNumber = seasonNumber,
            IndexNumber = episodeNumber,
            Name = title.Item.Title ?? $"Episode {episodeNumber}",
            ProviderIds = new Dictionary<string, string>
            {
                // Format: "hash:playlist:SxxExx:Title" — encodes everything needed
                // to restore metadata after the filename parser overwrites it
                ["TheDiscDb"] = $"{contentHash}:{Path.GetFileNameWithoutExtension(title.SourceFile)}:S{seasonNumber:D2}E{episodeNumber:D2}:{title.Item.Title}"
            }
        };

        // Link to parent Season/Series
        if (season is not null)
        {
            episode.SeasonId = season.Id;
            episode.SeasonName = season.Name;
        }

        if (series is not null)
        {
            episode.SeriesId = series.Id;
            episode.SeriesName = series.Name;
        }

        // If the playlist has multiple m2ts segments, use AdditionalParts
        if (m2tsFiles.Count > 1)
        {
            episode.AdditionalParts = m2tsFiles.Skip(1).ToArray();
        }

        return episode;
    }

    private Video? CreateExtra(
        DiscTitle title,
        string discRoot,
        string streamDir,
        string contentHash,
        Season? season)
    {
        if (title.Item is null || title.SourceFile is null)
        {
            return null;
        }

        var m2tsFiles = ResolvePlaylistFiles(discRoot, title.SourceFile, title.SegmentMap, streamDir);
        if (m2tsFiles.Count == 0)
        {
            _logger.LogDebug("TheDiscDb: No m2ts files for extra playlist {Playlist}", title.SourceFile);
            return null;
        }

        var extraType = MapExtraType(title.Item.Type, title.Item.Title);

        var video = new Video
        {
            Path = m2tsFiles[0],
            VideoType = VideoType.VideoFile,
            Name = title.Item.Title ?? "Extra",
            ExtraType = extraType,
            ProviderIds = new Dictionary<string, string>
            {
                ["TheDiscDb"] = $"{contentHash}:{Path.GetFileNameWithoutExtension(title.SourceFile)}"
            }
        };

        if (season is not null)
        {
            video.OwnerId = season.Id;
        }

        if (m2tsFiles.Count > 1)
        {
            video.AdditionalParts = m2tsFiles.Skip(1).ToArray();
        }

        return video;
    }

    /// <summary>
    /// Maps a TheDiscDb item type and title to a Jellyfin ExtraType.
    /// TheDiscDb uses: "Extra", "DeletedScene", "Trailer".
    /// We further classify "Extra" by title keywords.
    /// </summary>
    private static ExtraType MapExtraType(string? discDbType, string? title)
    {
        if (string.Equals(discDbType, "Trailer", StringComparison.OrdinalIgnoreCase))
        {
            return ExtraType.Trailer;
        }

        if (string.Equals(discDbType, "DeletedScene", StringComparison.OrdinalIgnoreCase))
        {
            return ExtraType.DeletedScene;
        }

        // Classify generic "Extra" by title keywords
        if (title is not null)
        {
            var t = title;
            if (t.Contains("Behind The", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Inside The", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Inside Season", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Making of", StringComparison.OrdinalIgnoreCase)
                || t.Contains("The Making", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Recording Booth", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Origins", StringComparison.OrdinalIgnoreCase))
            {
                return ExtraType.BehindTheScenes;
            }

            if (t.Contains("Interview", StringComparison.OrdinalIgnoreCase))
            {
                return ExtraType.Interview;
            }

            if (t.Contains("Deleted", StringComparison.OrdinalIgnoreCase))
            {
                return ExtraType.DeletedScene;
            }

            if (t.Contains("Trailer", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Promo", StringComparison.OrdinalIgnoreCase))
            {
                return ExtraType.Trailer;
            }

            if (t.Contains("Scene Breakdown", StringComparison.OrdinalIgnoreCase)
                || t.Contains("Anatomy of", StringComparison.OrdinalIgnoreCase))
            {
                return ExtraType.Scene;
            }

            if (t.Contains("Short", StringComparison.OrdinalIgnoreCase))
            {
                return ExtraType.Short;
            }
        }

        return ExtraType.Featurette;
    }

    /// <summary>
    /// Resolves the m2ts file paths for a given playlist.
    /// First tries parsing the MPLS file directly. Falls back to SegmentMap if available.
    /// </summary>
    private List<string> ResolvePlaylistFiles(string discRoot, string sourceFile, string? segmentMap, string streamDir)
    {
        // Try parsing the MPLS file
        var mplsPath = Path.Combine(discRoot, "BDMV", "PLAYLIST", sourceFile);
        if (File.Exists(mplsPath))
        {
            var clipNames = MplsParser.GetClipFiles(mplsPath);
            if (clipNames.Count > 0)
            {
                var resolved = new List<string>();
                foreach (var clip in clipNames)
                {
                    var fullPath = Path.Combine(streamDir, clip);
                    if (File.Exists(fullPath))
                    {
                        resolved.Add(fullPath);
                    }
                }

                if (resolved.Count > 0)
                {
                    return resolved;
                }
            }
        }

        // Fallback: use SegmentMap from TheDiscDb
        if (!string.IsNullOrEmpty(segmentMap))
        {
            var resolved = new List<string>();
            foreach (var segment in segmentMap.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = segment.Trim();
                var m2tsName = trimmed.PadLeft(5, '0') + ".m2ts";
                var fullPath = Path.Combine(streamDir, m2tsName);
                if (File.Exists(fullPath))
                {
                    resolved.Add(fullPath);
                }
            }

            if (resolved.Count > 0)
            {
                return resolved;
            }
        }

        return [];
    }
}
