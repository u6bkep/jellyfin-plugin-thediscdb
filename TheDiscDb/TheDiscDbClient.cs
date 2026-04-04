using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheDiscDb.TheDiscDb;

/// <summary>
/// Reads disc metadata from a local clone of the TheDiscDb/data git repository.
/// Builds an in-memory index of ContentHash -> DiscNode at startup.
/// </summary>
public class TheDiscDbClient
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, DiscNode> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="TheDiscDbClient"/> class.
    /// Indexes all disc JSON files in the repository.
    /// </summary>
    public TheDiscDbClient(ILogger logger, string repoPath)
    {
        _logger = logger;

        var dataDir = Path.Combine(repoPath, "data");
        if (!Directory.Exists(dataDir))
        {
            // Maybe they pointed directly at the data/ subdirectory
            dataDir = repoPath;
        }

        BuildIndex(dataDir);
    }

    /// <summary>
    /// Looks up a disc by its ContentHash.
    /// </summary>
    public DiscNode? GetDiscByHash(string contentHash)
    {
        _index.TryGetValue(contentHash, out var disc);
        return disc;
    }

    /// <summary>
    /// Gets the number of indexed discs.
    /// </summary>
    public int IndexedDiscCount => _index.Count;

    /// <summary>
    /// Gets the episodes from a disc, filtered to only Episode-type items.
    /// </summary>
    public static List<DiscTitle> GetEpisodes(DiscNode disc)
    {
        return disc.Titles?
            .Where(t => t.Item?.Type is not null
                && t.Item.Type.Equals("Episode", StringComparison.OrdinalIgnoreCase)
                && t.Item.Season is not null
                && !string.IsNullOrEmpty(t.Item.Episode))
            .OrderBy(t =>
            {
                var ep = t.Item!.Episode!;
                var dash = ep.IndexOf('-');
                var start = dash >= 0 ? ep.AsSpan(0, dash) : ep.AsSpan();
                return int.TryParse(start, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            })
            .ToList() ?? [];
    }

    /// <summary>
    /// Gets extras (non-episode, non-main-movie) from a disc.
    /// </summary>
    public static List<DiscTitle> GetExtras(DiscNode disc)
    {
        return disc.Titles?
            .Where(t => t.Item?.Type is not null
                && !t.Item.Type.Equals("Episode", StringComparison.OrdinalIgnoreCase)
                && !t.Item.Type.Equals("MainMovie", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Index)
            .ToList() ?? [];
    }

    private void BuildIndex(string dataDir)
    {
        if (!Directory.Exists(dataDir))
        {
            _logger.LogError("TheDiscDb data directory not found: {Path}", dataDir);
            return;
        }

        var discFiles = Directory.GetFiles(dataDir, "disc*.json", SearchOption.AllDirectories);
        _logger.LogInformation("TheDiscDb: Indexing {Count} disc files from {Path}", discFiles.Length, dataDir);

        foreach (var discFile in discFiles)
        {
            try
            {
                var json = File.ReadAllText(discFile);
                var disc = JsonSerializer.Deserialize<DiscNode>(json);

                if (disc?.ContentHash is null)
                {
                    continue;
                }

                disc.SourcePath = discFile;

                // Try to load parent metadata.json
                var releaseDir = Path.GetDirectoryName(discFile);
                var titleDir = releaseDir is not null ? Path.GetDirectoryName(releaseDir) : null;
                if (titleDir is not null)
                {
                    var metadataFile = Path.Combine(titleDir, "metadata.json");
                    if (File.Exists(metadataFile))
                    {
                        try
                        {
                            var metaJson = File.ReadAllText(metadataFile);
                            disc.MediaItem = JsonSerializer.Deserialize<MediaItemMetadata>(metaJson);
                        }
                        catch (JsonException)
                        {
                            // Skip corrupt metadata
                        }
                    }
                }

                _index[disc.ContentHash] = disc;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "TheDiscDb: Failed to parse {File}", discFile);
            }
        }

        _logger.LogInformation("TheDiscDb: Indexed {Count} discs", _index.Count);
    }
}
