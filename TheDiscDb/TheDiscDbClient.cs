using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheDiscDb.TheDiscDb;

/// <summary>
/// Client for querying the TheDiscDb GraphQL API.
/// Caches results to disk to minimize API calls across library scans.
/// </summary>
public class TheDiscDbClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, DiscNode?> _memoryCache = new();
    private bool _disposed;

    // The GraphQL schema queries through mediaItems -> releases -> discs.
    // We filter at the mediaItem level for releases containing our disc hash,
    // then extract the matching disc client-side.
    private const string Query = @"
        query GetDiscByHash($hash: String!) {
          mediaItems(
            where: {
              releases: {
                some: {
                  discs: {
                    some: { contentHash: { eq: $hash } }
                  }
                }
              }
            }
          ) {
            nodes {
              title
              slug
              externalids { tmdb imdb }
              releases {
                title
                slug
                discs {
                  contentHash
                  name
                  slug
                  titles {
                    index
                    sourceFile
                    segmentMap
                    duration
                    size
                    item {
                      title
                      type
                      season
                      episode
                    }
                  }
                }
              }
            }
          }
        }";

    /// <summary>
    /// Initializes a new instance of the <see cref="TheDiscDbClient"/> class.
    /// </summary>
    public TheDiscDbClient(ILogger logger, string cacheDir)
    {
        _logger = logger;
        _cacheDir = cacheDir;
        _httpClient = new HttpClient();

        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Looks up a disc by its ContentHash.
    /// Returns cached data if available, otherwise queries the API.
    /// </summary>
    public async Task<DiscNode?> GetDiscByHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        if (_memoryCache.TryGetValue(contentHash, out var cached))
        {
            return cached;
        }

        // Check disk cache
        var cacheFile = Path.Combine(_cacheDir, contentHash + ".json");
        if (File.Exists(cacheFile))
        {
            var cacheAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile);
            var maxAge = TimeSpan.FromHours(TheDiscDbPlugin.Instance?.Configuration.CacheHours ?? 168);

            if (cacheAge < maxAge)
            {
                try
                {
                    var cacheJson = await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
                    var cacheResult = JsonSerializer.Deserialize<DiscNode>(cacheJson);
                    _memoryCache[contentHash] = cacheResult;
                    return cacheResult;
                }
                catch (JsonException)
                {
                    // Corrupt cache, re-fetch
                }
            }
        }

        // Query API
        var endpoint = TheDiscDbPlugin.Instance?.Configuration.ApiEndpoint ?? "https://thediscdb.com/graphql";
        var disc = await QueryApiAsync(endpoint, contentHash, cancellationToken).ConfigureAwait(false);

        _memoryCache[contentHash] = disc;

        // Write to disk cache
        if (disc is not null)
        {
            try
            {
                var json = JsonSerializer.Serialize(disc);
                await File.WriteAllTextAsync(cacheFile, json, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to write TheDiscDb cache file: {Path}", cacheFile);
            }
        }

        return disc;
    }

    /// <summary>
    /// Gets the episodes from a disc, filtered to only Episode-type items.
    /// </summary>
    public static List<DiscTitle> GetEpisodes(DiscNode disc)
    {
        return disc.Titles?
            .Where(t => t.Item?.Type is not null
                && t.Item.Type.Equals("Episode", StringComparison.OrdinalIgnoreCase)
                && t.Item.Season is not null
                && t.Item.Episode is not null)
            .OrderBy(t => int.TryParse(t.Item!.Episode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ep) ? ep : 0)
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

    private async Task<DiscNode?> QueryApiAsync(string endpoint, string contentHash, CancellationToken cancellationToken)
    {
        try
        {
            var requestBody = new
            {
                query = Query,
                variables = new { hash = contentHash }
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TheDiscDb API returned {StatusCode} for hash {Hash}", response.StatusCode, contentHash);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<DiscQueryResult>(responseJson);

            // Navigate: mediaItems -> releases -> discs, find the disc matching our hash
            var mediaItem = result?.Data?.MediaItems?.Nodes?.FirstOrDefault();
            if (mediaItem is null)
            {
                _logger.LogDebug("No media item found in TheDiscDb for hash {Hash}", contentHash);
                return null;
            }

            foreach (var release in mediaItem.Releases ?? [])
            {
                foreach (var disc in release.Discs ?? [])
                {
                    if (string.Equals(disc.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // Attach the parent media item info to the disc for later use
                        disc.Release = release;
                        disc.MediaItem = mediaItem;
                        _logger.LogInformation(
                            "TheDiscDb matched hash {Hash} to \"{Series}\" - {DiscName}",
                            contentHash,
                            mediaItem.Title,
                            disc.Name);
                        return disc;
                    }
                }
            }

            _logger.LogDebug("Hash {Hash} matched media item \"{Title}\" but no disc with that hash found", contentHash, mediaItem.Title);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to query TheDiscDb API for hash {Hash}", contentHash);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}
