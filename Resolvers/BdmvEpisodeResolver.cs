using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;

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
/// Each episode gets its Path set to the .mpls playlist file, giving each episode
/// a unique identity and letting ffprobe/ffmpeg handle segment resolution natively.
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
                c.IsDirectory && (c.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)
                    || Directory.Exists(Path.Combine(c.FullName, "BDMV"))));

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

        // Find all disc roots: either a direct BDMV child, or subdirectories containing BDMV (multi-disc layout)
        var discRoots = new List<string>();
        var consumedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (files.Any(f => f.IsDirectory && f.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)))
        {
            discRoots.Add(parent.Path);
            consumedDirs.Add("BDMV");
            consumedDirs.Add("CERTIFICATE");
        }

        foreach (var dir in files.Where(f => f.IsDirectory
            && !f.Name.Equals("BDMV", StringComparison.OrdinalIgnoreCase)
            && !f.Name.Equals("CERTIFICATE", StringComparison.OrdinalIgnoreCase)))
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "BDMV")))
            {
                discRoots.Add(dir.FullName);
                consumedDirs.Add(dir.Name);
            }
        }

        if (discRoots.Count == 0)
        {
            return new MultiItemResolverResult();
        }

        var result = new MultiItemResolverResult();

        foreach (var discRoot in discRoots)
        {
            ResolveDisc(discRoot, season, series, result);
        }

        if (result.Items.Count == 0)
        {
            return new MultiItemResolverResult();
        }

        // Pass unconsumed files through to normal resolvers (e.g., standalone MKV episodes)
        result.ExtraFiles = files
            .Where(f => !consumedDirs.Contains(f.Name))
            .ToList();

        return result;
    }

    private void ResolveDisc(string discRoot, Season? season, Series? series, MultiItemResolverResult result)
    {
        _logger.LogInformation("TheDiscDb: Found BDMV folder at {Path}, computing ContentHash...", discRoot);

        var contentHash = ContentHashCalculator.ComputeHash(discRoot);
        if (contentHash is null)
        {
            _logger.LogWarning("TheDiscDb: Could not compute ContentHash for {Path}", discRoot);
            return;
        }

        _logger.LogInformation("TheDiscDb: ContentHash = {Hash}", contentHash);

        var disc = _client.GetDiscByHash(contentHash);
        if (disc is null)
        {
            _logger.LogInformation("TheDiscDb: No match found for hash {Hash}, falling through to default resolver", contentHash);
            return;
        }

        var episodes = TheDiscDbClient.GetEpisodes(disc);
        var extras = TheDiscDbClient.GetExtras(disc);

        if (episodes.Count == 0 && extras.Count == 0)
        {
            _logger.LogInformation("TheDiscDb: Disc {Hash} matched but contains no episodes or extras, falling through", contentHash);
            return;
        }

        _logger.LogInformation("TheDiscDb: Found {EpCount} episodes and {ExCount} extras on disc {Name}", episodes.Count, extras.Count, disc.Name);

        var playlistDir = Path.Combine(discRoot, "BDMV", "PLAYLIST");

        foreach (var title in episodes)
        {
            var episode = CreateEpisode(title, playlistDir, contentHash, season, series);
            if (episode is not null)
            {
                result.Items.Add(episode);
            }
        }

        foreach (var title in extras)
        {
            var extra = CreateExtra(title, discRoot, playlistDir, contentHash, season);
            if (extra is not null)
            {
                result.Items.Add(extra);
            }
        }
    }

    private Episode? CreateEpisode(
        DiscTitle title,
        string playlistDir,
        string contentHash,
        Season? season,
        Series? series)
    {
        if (title.Item is null || title.SourceFile is null)
        {
            return null;
        }

        var mplsPath = Path.Combine(playlistDir, title.SourceFile);
        if (!File.Exists(mplsPath))
        {
            _logger.LogWarning("TheDiscDb: Playlist file not found: {Path}", mplsPath);
            return null;
        }

        if (!int.TryParse(title.Item.Season, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonNumber))
        {
            _logger.LogWarning("TheDiscDb: Unparseable season \"{Value}\" for {File}", title.Item.Season, title.SourceFile);
            return null;
        }

        if (!TryParseEpisodeRange(title.Item.Episode, out var episodeStart, out var episodeEnd))
        {
            _logger.LogWarning("TheDiscDb: Unparseable episode \"{Value}\" for {File}", title.Item.Episode, title.SourceFile);
            return null;
        }

        // Build episode tag for provider ID: "S01E03" or "S01E01-E02"
        var episodeTag = episodeEnd.HasValue
            ? $"S{seasonNumber:D2}E{episodeStart:D2}-E{episodeEnd.Value:D2}"
            : $"S{seasonNumber:D2}E{episodeStart:D2}";

        var fallbackName = episodeEnd.HasValue
            ? $"Episode {episodeStart}-{episodeEnd.Value}"
            : $"Episode {episodeStart}";

        var episode = new Episode
        {
            Path = mplsPath,
            VideoType = VideoType.BluRay,
            ParentIndexNumber = seasonNumber,
            IndexNumber = episodeStart,
            IndexNumberEnd = episodeEnd,
            Name = title.Item.Title ?? fallbackName,
            ProviderIds = new Dictionary<string, string>
            {
                // Format: "hash:playlist:SxxExx[-Eyy]:Title"
                ["TheDiscDb"] = $"{contentHash}:{Path.GetFileNameWithoutExtension(title.SourceFile)}:{episodeTag}:{title.Item.Title}"
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

        return episode;
    }

    private static bool TryParseEpisodeRange(string? value, out int start, out int? end)
    {
        start = 0;
        end = null;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var dash = value.IndexOf('-');
        if (dash < 0)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start);
        }

        if (int.TryParse(value.AsSpan(0, dash), NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
            && int.TryParse(value.AsSpan(dash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var endVal))
        {
            end = endVal;
            return true;
        }

        return false;
    }

    private Video? CreateExtra(
        DiscTitle title,
        string discRoot,
        string playlistDir,
        string contentHash,
        Season? season)
    {
        if (title.Item is null || title.SourceFile is null)
        {
            return null;
        }

        // SourceFile can be a .mpls (playlist) or .m2ts (stream) file
        var filePath = ResolveSourceFile(discRoot, playlistDir, title.SourceFile);
        if (filePath is null)
        {
            _logger.LogDebug("TheDiscDb: Source file not found for extra: {File}", title.SourceFile);
            return null;
        }

        var extraType = MapExtraType(title.Item.Type, title.Item.Title);

        var isBluRay = filePath.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase);

        var video = new Video
        {
            Path = filePath,
            VideoType = isBluRay ? VideoType.BluRay : VideoType.VideoFile,
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

        return video;
    }

    /// <summary>
    /// Resolves a TheDiscDb SourceFile to a full path. Playlist files (.mpls) live
    /// in BDMV/PLAYLIST/, stream files (.m2ts) in BDMV/STREAM/.
    /// </summary>
    private static string? ResolveSourceFile(string discRoot, string playlistDir, string sourceFile)
    {
        if (sourceFile.EndsWith(".mpls", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(playlistDir, sourceFile);
            return File.Exists(path) ? path : null;
        }

        if (sourceFile.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(discRoot, "BDMV", "STREAM", sourceFile);
            return File.Exists(path) ? path : null;
        }

        // Unknown extension — try PLAYLIST first, then STREAM
        var tryPlaylist = Path.Combine(playlistDir, sourceFile);
        if (File.Exists(tryPlaylist))
        {
            return tryPlaylist;
        }

        var tryStream = Path.Combine(discRoot, "BDMV", "STREAM", sourceFile);
        return File.Exists(tryStream) ? tryStream : null;
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

}
