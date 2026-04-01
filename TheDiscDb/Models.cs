#pragma warning disable CS1591 // JSON DTO classes don't need XML doc comments

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TheDiscDb.TheDiscDb;

/// <summary>
/// Root response from a TheDiscDb GraphQL query.
/// </summary>
public class DiscQueryResult
{
    [JsonPropertyName("data")]
    public DiscQueryData? Data { get; set; }
}

/// <summary>
/// Top-level data wrapper.
/// </summary>
public class DiscQueryData
{
    [JsonPropertyName("mediaItems")]
    public MediaItemConnection? MediaItems { get; set; }
}

/// <summary>
/// Relay-style connection for media items.
/// </summary>
public class MediaItemConnection
{
    [JsonPropertyName("nodes")]
    public List<MediaItemNode>? Nodes { get; set; }
}

/// <summary>
/// A media item (series or movie) from TheDiscDb.
/// </summary>
public class MediaItemNode
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("externalids")]
    public ExternalIds? ExternalIds { get; set; }

    [JsonPropertyName("releases")]
    public List<ReleaseNode>? Releases { get; set; }
}

/// <summary>
/// A physical release (box set, single disc release) from TheDiscDb.
/// </summary>
public class ReleaseNode
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("discs")]
    public List<DiscNode>? Discs { get; set; }
}

/// <summary>
/// A single disc from TheDiscDb.
/// </summary>
public class DiscNode
{
    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("titles")]
    public List<DiscTitle>? Titles { get; set; }

    /// <summary>
    /// Gets or sets the parent release (populated client-side after query).
    /// </summary>
    [JsonIgnore]
    public ReleaseNode? Release { get; set; }

    /// <summary>
    /// Gets or sets the parent media item (populated client-side after query).
    /// </summary>
    [JsonIgnore]
    public MediaItemNode? MediaItem { get; set; }
}

/// <summary>
/// A title (episode, extra, main movie) on a disc.
/// </summary>
public class DiscTitle
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("sourceFile")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("segmentMap")]
    public string? SegmentMap { get; set; }

    [JsonPropertyName("duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("item")]
    public DiscTitleItem? Item { get; set; }
}

/// <summary>
/// Metadata for an identified title on a disc.
/// </summary>
public class DiscTitleItem
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonPropertyName("episode")]
    public string? Episode { get; set; }
}

/// <summary>
/// External identifiers for a media item.
/// </summary>
public class ExternalIds
{
    [JsonPropertyName("tmdb")]
    public string? Tmdb { get; set; }

    [JsonPropertyName("imdb")]
    public string? Imdb { get; set; }
}
