using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarium.Events;

/// <summary>
/// A "ready to watch" notice waiting for a person to see it.
/// </summary>
public class Notice
{
    /// <summary>Gets or sets the event's id.</summary>
    public long EventId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string Media { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB id.</summary>
    public long TmdbId { get; set; }

    /// <summary>Gets or sets the Jellyfin item to play, if known.</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets when it became ready.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// What the plugin keeps between restarts: handled events, the polling cursor,
/// unseen notices and each person's "Ready for you" playlist.
/// </summary>
public class NoticeStore
{
    private const int KeepHandled = 1000;
    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromDays(3);
    private static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(90);

    private readonly object _lock = new();
    private readonly ILogger<NoticeStore> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _heartbeats = new(StringComparer.OrdinalIgnoreCase);
    private State _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="NoticeStore"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public NoticeStore(ILogger<NoticeStore> logger)
    {
        _logger = logger;
        _state = Load();
    }

    private static string FilePath => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "state.json");

    /// <summary>Gets or sets the highest event id read with GET /events.</summary>
    public long Cursor
    {
        get
        {
            lock (_lock)
            {
                return _state.Cursor;
            }
        }

        set
        {
            lock (_lock)
            {
                if (value > _state.Cursor)
                {
                    _state.Cursor = value;
                    Save();
                }
            }
        }
    }

    /// <summary>
    /// Marks an event handled.
    /// </summary>
    /// <param name="eventId">The event's id.</param>
    /// <returns>False when it was handled before.</returns>
    public bool TryMarkHandled(long eventId)
    {
        lock (_lock)
        {
            if (_state.Handled.Contains(eventId))
            {
                return false;
            }

            _state.Handled.Add(eventId);
            if (_state.Handled.Count > KeepHandled)
            {
                _state.Handled = _state.Handled.OrderByDescending(x => x).Take(KeepHandled).ToList();
            }

            Save();
            return true;
        }
    }

    /// <summary>Takes back a mark when handling failed, so a retry handles it.</summary>
    /// <param name="eventId">The event's id.</param>
    public void Unmark(long eventId)
    {
        lock (_lock)
        {
            _state.Handled.Remove(eventId);
            Save();
        }
    }

    /// <summary>Adds a notice for a person.</summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="notice">The notice.</param>
    public void AddNotice(Guid userId, Notice notice)
    {
        lock (_lock)
        {
            var key = userId.ToString("N");
            if (!_state.Notices.TryGetValue(key, out var list))
            {
                list = new List<Notice>();
                _state.Notices[key] = list;
            }

            list.RemoveAll(n => n.EventId == notice.EventId);
            list.Add(notice);
            Save();
        }
    }

    /// <summary>Gets a person's unseen notices, oldest first.</summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <returns>The notices.</returns>
    public IReadOnlyList<Notice> GetNotices(Guid userId)
    {
        lock (_lock)
        {
            if (!_state.Notices.TryGetValue(userId.ToString("N"), out var list))
            {
                return Array.Empty<Notice>();
            }

            var cutoff = DateTimeOffset.UtcNow - NoticeLifetime;
            if (list.RemoveAll(n => n.CreatedAt < cutoff) > 0)
            {
                Save();
            }

            return list.OrderBy(n => n.EventId).ToList();
        }
    }

    /// <summary>Drops a notice the person saw.</summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="eventId">The event's id.</param>
    public void Dismiss(Guid userId, long eventId)
    {
        lock (_lock)
        {
            if (_state.Notices.TryGetValue(userId.ToString("N"), out var list) && list.RemoveAll(n => n.EventId == eventId) > 0)
            {
                Save();
            }
        }
    }

    /// <summary>Gets the person's "Ready for you" playlist, if one was made.</summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <returns>The playlist's id.</returns>
    public Guid? GetPlaylist(Guid userId)
    {
        lock (_lock)
        {
            return _state.Playlists.TryGetValue(userId.ToString("N"), out var id) ? id : null;
        }
    }

    /// <summary>Keeps the person's "Ready for you" playlist.</summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="playlistId">The playlist's id.</param>
    public void SetPlaylist(Guid userId, Guid playlistId)
    {
        lock (_lock)
        {
            _state.Playlists[userId.ToString("N")] = playlistId;
            Save();
        }
    }

    /// <summary>Notes that Jellyfin Web on this device runs the plugin's script, which shows its own pop-up.</summary>
    /// <param name="deviceId">The device.</param>
    public void Heartbeat(string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            _heartbeats[deviceId] = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Gets a value indicating whether the device ran the plugin's script lately.</summary>
    /// <param name="deviceId">The device.</param>
    /// <returns>True when it shows its own pop-up.</returns>
    public bool HasScript(string? deviceId)
    {
        return !string.IsNullOrEmpty(deviceId)
            && _heartbeats.TryGetValue(deviceId, out var at)
            && DateTimeOffset.UtcNow - at < HeartbeatLifetime;
    }

    private State Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath)) ?? new State();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't read {Path}; starting afresh", FilePath);
        }

        return new State();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Couldn't write {Path}", FilePath);
        }
    }

    private sealed class State
    {
        public long Cursor { get; set; }

        public List<long> Handled { get; set; } = new();

        public Dictionary<string, List<Notice>> Notices { get; set; } = new();

        public Dictionary<string, Guid> Playlists { get; set; } = new();
    }
}
