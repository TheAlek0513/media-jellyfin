using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Mediarium.Events;

/// <summary>
/// An event from Mediarium, as the webhook and GET /events carry it.
/// </summary>
public class MediariumEvent
{
    /// <summary>Gets or sets the event's id.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>Gets or sets the kind; only "available" exists now.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the request's id.</summary>
    [JsonPropertyName("requestId")]
    public long RequestId { get; set; }

    /// <summary>Gets or sets "movie" or "tv".</summary>
    [JsonPropertyName("media")]
    public string Media { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB id.</summary>
    [JsonPropertyName("tmdbId")]
    public long TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    [JsonPropertyName("year")]
    public int? Year { get; set; }

    /// <summary>Gets or sets a value indicating whether this was a 4K copy.</summary>
    [JsonPropertyName("fourK")]
    public bool FourK { get; set; }

    /// <summary>Gets or sets the Jellyfin item, empty when Jellyfin hadn't found it in time.</summary>
    [JsonPropertyName("jellyfinItemId")]
    public string? JellyfinItemId { get; set; }

    /// <summary>Gets or sets the Jellyfin users to tell.</summary>
    [JsonPropertyName("jellyfinUserIds")]
    public List<string> JellyfinUserIds { get; set; } = new();

    /// <summary>Gets or sets when it happened.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }
}
