using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarium.Events;

/// <summary>
/// Handles Mediarium's events, whichever way they arrive.
/// </summary>
public class EventProcessor
{
    /// <summary>The playlist ready titles go into.</summary>
    public const string PlaylistName = "Ready for you";

    // Events older than this are marked handled without telling anyone, so a
    // first poll (or a long outage) doesn't bring up a pile of old pop-ups.
    private static readonly TimeSpan TellWithin = TimeSpan.FromHours(6);

    private readonly NoticeStore _store;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly ILogger<EventProcessor> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="EventProcessor"/> class.
    /// </summary>
    /// <param name="store">The notice store.</param>
    /// <param name="sessionManager">Jellyfin's sessions.</param>
    /// <param name="userManager">Jellyfin's users.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="playlistManager">Jellyfin's playlists.</param>
    /// <param name="logger">The logger.</param>
    public EventProcessor(
        NoticeStore store,
        ISessionManager sessionManager,
        IUserManager userManager,
        ILibraryManager libraryManager,
        IPlaylistManager playlistManager,
        ILogger<EventProcessor> logger)
    {
        _store = store;
        _sessionManager = sessionManager;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _logger = logger;
    }

    /// <summary>
    /// Handles one event; an id handled before is skipped.
    /// </summary>
    /// <param name="evt">The event.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task HandleAsync(MediariumEvent evt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_store.TryMarkHandled(evt.Id))
            {
                return;
            }

            try
            {
                if (string.Equals(evt.Kind, "available", StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.UtcNow - evt.CreatedAt < TellWithin)
                {
                    await HandleAvailableAsync(evt, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _store.Unmark(evt.Id);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleAvailableAsync(MediariumEvent evt, CancellationToken cancellationToken)
    {
        var item = FindItem(evt);
        var itemId = item?.Id.ToString("N");
        _logger.LogInformation("{Title} is ready (event {Id}, item {Item})", evt.Title, evt.Id, itemId ?? "not found yet");

        foreach (var raw in evt.JellyfinUserIds)
        {
            if (!Guid.TryParse(raw, out var userId) || _userManager.GetUserById(userId) is null)
            {
                continue;
            }

            _store.AddNotice(userId, new Notice
            {
                EventId = evt.Id,
                Title = evt.Title,
                Media = evt.Media,
                TmdbId = evt.TmdbId,
                ItemId = itemId,
                CreatedAt = evt.CreatedAt,
            });

            if (item is not null && Plugin.Instance?.Configuration.ReadyPlaylist != false)
            {
                try
                {
                    await AddToPlaylistAsync(userId, item).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Couldn't add {Title} to {Playlist} for {User}", evt.Title, PlaylistName, userId);
                }
            }

            await TellSessionsAsync(userId, evt.Title + " is ready to watch", cancellationToken, skipScripted: true).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a message to a person's active sessions.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="text">The message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="skipScripted">Leave out Jellyfin Web tabs that show the plugin's own pop-up.</param>
    /// <returns>A task.</returns>
    public async Task TellSessionsAsync(Guid userId, string text, CancellationToken cancellationToken, bool skipScripted = false)
    {
        var sessions = _sessionManager.Sessions
            .Where(s => s.UserId.Equals(userId) && s.IsActive && s.SupportsRemoteControl)
            .Where(s => !skipScripted || !_store.HasScript(s.DeviceId))
            .ToList();
        foreach (var session in sessions)
        {
            try
            {
                await _sessionManager.SendMessageCommand(
                    null,
                    session.Id,
                    new MessageCommand { Header = "Mediarium", Text = text, TimeoutMs = 15000 },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Couldn't send a message to session {Session}", session.Id);
            }
        }
    }

    private BaseItem? FindItem(MediariumEvent evt)
    {
        if (Guid.TryParse(evt.JellyfinItemId, out var id) && _libraryManager.GetItemById(id) is BaseItem byId)
        {
            return byId;
        }

        if (evt.TmdbId <= 0)
        {
            return null;
        }

        var kind = string.Equals(evt.Media, "tv", StringComparison.OrdinalIgnoreCase) ? BaseItemKind.Series : BaseItemKind.Movie;
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            HasAnyProviderId = new Dictionary<string, string>
            {
                [MetadataProvider.Tmdb.ToString()] = evt.TmdbId.ToString(CultureInfo.InvariantCulture),
            },
            Recursive = true,
            Limit = 1,
        }).FirstOrDefault();
    }

    private async Task AddToPlaylistAsync(Guid userId, BaseItem item)
    {
        var playlistId = _store.GetPlaylist(userId);
        Playlist? playlist = null;
        if (playlistId is Guid known)
        {
            playlist = _libraryManager.GetItemById(known) as Playlist;
        }

        if (playlist is null)
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = PlaylistName,
                UserId = userId,
                MediaType = MediaType.Video,
                ItemIdList = new[] { item.Id },
            }).ConfigureAwait(false);
            _store.SetPlaylist(userId, Guid.Parse(result.Id));
            return;
        }

        if (playlist.LinkedChildren.Any(c => c.ItemId.HasValue && c.ItemId.Value.Equals(item.Id)))
        {
            return;
        }

        await _playlistManager.AddItemToPlaylistAsync(playlist.Id, new[] { item.Id }, userId).ConfigureAwait(false);
    }
}
