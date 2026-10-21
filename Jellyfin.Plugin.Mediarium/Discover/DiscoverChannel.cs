using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarium.Api;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarium.Discover;

/// <summary>
/// One "Discover" library mixing trending and popular movies and shows that
/// aren't in the library yet. Favouriting one asks Mediarium for it.
/// </summary>
public class DiscoverChannel : IChannel, IHasCacheKey
{
    /// <summary>The channel's name, which is also how the favourite watcher recognises it.</summary>
    public const string ChannelName = "Discover";

    /// <summary>The line every placeholder starts with.</summary>
    public const string Placeholder = "Not in library, favourite to request.";

    private readonly MediariumClient _client;
    private readonly ILogger<DiscoverChannel> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscoverChannel"/> class.
    /// </summary>
    /// <param name="client">The Mediarium client.</param>
    /// <param name="logger">The logger.</param>
    public DiscoverChannel(MediariumClient client, ILogger<DiscoverChannel> logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => ChannelName;

    /// <inheritdoc />
    public string Description => "Trending and popular movies and shows from Mediarium. " + Placeholder;

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => "https://github.com/TheAlek0513/media-jellyfin";

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>
    /// Parses a placeholder's id, "movie-693134" or "tv-1399".
    /// </summary>
    /// <param name="externalId">The channel item id.</param>
    /// <param name="media">"movie" or "tv".</param>
    /// <param name="tmdbId">The TMDB id.</param>
    /// <returns>True when it is one of ours.</returns>
    public static bool TryParseId(string? externalId, out string media, out long tmdbId)
    {
        media = string.Empty;
        tmdbId = 0;
        if (string.IsNullOrEmpty(externalId))
        {
            return false;
        }

        var dash = externalId.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0)
        {
            return false;
        }

        media = externalId[..dash];
        return (media == "movie" || media == "tv")
            && long.TryParse(externalId[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out tmdbId);
    }

    /// <inheritdoc />
    public string GetCacheKey(string? userId)
    {
        // Per person (their states differ), refreshed every 10 minutes.
        var slot = DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(10).Ticks;
        return (userId ?? string.Empty) + "-" + slot.ToString(CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
            MaxPageSize = 100,
            SupportsContentDownloading = false,
            AutoRefreshLevels = 1,
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => MediariumClient.IsConfigured;

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        var items = new List<ChannelItemInfo>();

        // A show is a folder with nothing inside; opening it shows the same placeholder text.
        if (!string.IsNullOrEmpty(query.FolderId))
        {
            return new ChannelItemResult { Items = items, TotalRecordCount = 0 };
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in new[] { "trending", "popular" })
        {
            var response = await _client.SendAsync(HttpMethod.Get, "discover/mixed/" + list, query.UserId, null, cancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                _logger.LogInformation("Discover {List} for {User}: {Status} {Message}", list, query.UserId, response.Status, response.ErrorMessage);
                continue;
            }

            List<Title>? titles;
            try
            {
                titles = JsonSerializer.Deserialize<List<Title>>(response.Body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Discover {List}: unexpected answer", list);
                continue;
            }

            foreach (var t in titles ?? new List<Title>())
            {
                if (t.State == "available" || t.TmdbId <= 0)
                {
                    continue;
                }

                var id = t.Media + "-" + t.TmdbId.ToString(CultureInfo.InvariantCulture);
                if (seen.Add(id))
                {
                    items.Add(ToItem(id, t));
                }
            }
        }

        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Enumerable.Empty<ImageType>();

    private static ChannelItemInfo ToItem(string id, Title t)
    {
        var tv = t.Media == "tv";
        var item = new ChannelItemInfo
        {
            Id = id,
            Name = t.Name,
            Overview = StateLine(t) + (string.IsNullOrEmpty(t.Overview) ? string.Empty : "\n\n" + t.Overview),
            ImageUrl = t.PosterUrl,
            ProductionYear = t.Year,
            CommunityRating = t.Rating is > 0 ? (float)t.Rating.Value : null,
            Type = tv ? ChannelItemType.Folder : ChannelItemType.Media,
            FolderType = tv ? ChannelFolderType.Series : ChannelFolderType.Container,
            ContentType = ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            DateCreated = DateTime.UtcNow,
            ProviderIds = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = t.TmdbId.ToString(CultureInfo.InvariantCulture) },
            Tags = new List<string> { "Not in library" },
        };
        return item;
    }

    private static string StateLine(Title t) => t.State switch
    {
        "pending" => "Requested, waiting for an administrator.",
        "declined" => "Declined." + (string.IsNullOrEmpty(t.Note) ? string.Empty : " " + t.Note),
        "approved" => "Approved, being looked for.",
        "downloading" => "Downloading" + (t.Progress is double p ? " (" + Math.Round(p).ToString(CultureInfo.InvariantCulture) + "%)." : "."),
        _ => Placeholder,
    };

    private sealed class Title
    {
        [JsonPropertyName("media")]
        public string Media { get; set; } = string.Empty;

        [JsonPropertyName("tmdbId")]
        public long TmdbId { get; set; }

        [JsonPropertyName("title")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("year")]
        public int? Year { get; set; }

        [JsonPropertyName("overview")]
        public string? Overview { get; set; }

        [JsonPropertyName("posterUrl")]
        public string? PosterUrl { get; set; }

        [JsonPropertyName("rating")]
        public double? Rating { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = "none";

        [JsonPropertyName("progress")]
        public double? Progress { get; set; }

        [JsonPropertyName("note")]
        public string? Note { get; set; }
    }
}
