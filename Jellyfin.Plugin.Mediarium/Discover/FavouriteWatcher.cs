using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mediarium.Api;
using Jellyfin.Plugin.Mediarium.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarium.Discover;

/// <summary>
/// Asks Mediarium for a Discover placeholder when someone favourites it, as that person.
/// </summary>
public class FavouriteWatcher : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly MediariumClient _client;
    private readonly EventProcessor _processor;
    private readonly ILogger<FavouriteWatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FavouriteWatcher"/> class.
    /// </summary>
    /// <param name="userDataManager">Jellyfin's user data.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="client">The Mediarium client.</param>
    /// <param name="processor">Used to tell the person how it went.</param>
    /// <param name="logger">The logger.</param>
    public FavouriteWatcher(IUserDataManager userDataManager, ILibraryManager libraryManager, MediariumClient client, EventProcessor processor, ILogger<FavouriteWatcher> logger)
    {
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _client = client;
        _processor = processor;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.UserData is null || !e.UserData.IsFavorite || e.Item is not BaseItem item || item.SourceType != SourceType.Channel)
        {
            return;
        }

        if (!DiscoverChannel.TryParseId(item.ExternalId, out var media, out var tmdbId))
        {
            return;
        }

        if (_libraryManager.GetItemById(item.ChannelId)?.Name != DiscoverChannel.ChannelName)
        {
            return;
        }

        var userId = e.UserId;
        var title = item.Name;
        _ = Task.Run(() => RequestAsync(userId, media, tmdbId, title));
    }

    private async Task RequestAsync(Guid userId, string media, long tmdbId, string title)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { media, tmdbId, fourK = false });
            var response = await _client.SendAsync(HttpMethod.Post, "requests", userId, body, CancellationToken.None).ConfigureAwait(false);
            string message;
            if (response.Ok)
            {
                message = MessageOf(response.Body) ?? ("Asked for " + title + ".");
            }
            else
            {
                message = title + ": " + response.ErrorMessage;
            }

            _logger.LogInformation("Favourite {Media} {TmdbId} by {User}: {Status} {Message}", media, tmdbId, userId, response.Status, message);
            await _processor.TellSessionsAsync(userId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Requesting {Media} {TmdbId} for {User} failed", media, tmdbId, userId);
        }
    }

    private static string? MessageOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
