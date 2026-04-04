#pragma warning disable CS1591 // JSON DTO classes don't need XML doc comments

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.TheDiscDb.TheDiscDb;

/// <summary>
/// A single disc from the TheDiscDb data repo (discNN.json).
/// </summary>
public class DiscNode
{
    [JsonPropertyName("ContentHash")]
    public string? ContentHash { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("Slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("Titles")]
    public List<DiscTitle>? Titles { get; set; }

    /// <summary>
    /// Gets or sets the path to the disc JSON file (populated at index time).
    /// </summary>
    [JsonIgnore]
    public string? SourcePath { get; set; }

    /// <summary>
    /// Gets or sets the parent media item metadata (populated at index time).
    /// </summary>
    [JsonIgnore]
    public MediaItemMetadata? MediaItem { get; set; }
}

/// <summary>
/// A title (episode, extra, main movie) on a disc.
/// </summary>
public class DiscTitle
{
    [JsonPropertyName("Index")]
    public int Index { get; set; }

    [JsonPropertyName("SourceFile")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("SegmentMap")]
    public string? SegmentMap { get; set; }

    [JsonPropertyName("Duration")]
    public string? Duration { get; set; }

    [JsonPropertyName("Size")]
    public long Size { get; set; }

    [JsonPropertyName("Item")]
    public DiscTitleItem? Item { get; set; }
}

/// <summary>
/// Metadata for an identified title on a disc.
/// </summary>
public class DiscTitleItem
{
    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("Type")]
    public string? Type { get; set; }

    [JsonPropertyName("Season")]
    public string? Season { get; set; }

    [JsonPropertyName("Episode")]
    public string? Episode { get; set; }
}

/// <summary>
/// Media item metadata from the repo (metadata.json).
/// </summary>
public class MediaItemMetadata
{
    [JsonPropertyName("Title")]
    public string? Title { get; set; }

    [JsonPropertyName("Slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("ExternalIds")]
    public ExternalIds? ExternalIds { get; set; }
}

/// <summary>
/// External identifiers for a media item.
/// </summary>
public class ExternalIds
{
    [JsonPropertyName("Tmdb")]
    public string? Tmdb { get; set; }

    [JsonPropertyName("Imdb")]
    public string? Imdb { get; set; }
}
