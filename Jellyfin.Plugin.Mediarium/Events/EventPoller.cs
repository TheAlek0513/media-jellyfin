using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mediarium.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarium.Events;

/// <summary>
/// Reads GET /events now and then, in case a webhook didn't get through
/// (or when Mediarium can't reach Jellyfin at all).
/// </summary>
public class EventPoller : BackgroundService
{
    private readonly MediariumClient _client;
    private readonly EventProcessor _processor;
    private readonly NoticeStore _store;
    private readonly ILogger<EventPoller> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventPoller"/> class.
    /// </summary>
    /// <param name="client">The Mediarium client.</param>
    /// <param name="processor">The event processor.</param>
    /// <param name="store">The notice store.</param>
    /// <param name="logger">The logger.</param>
    public EventPoller(MediariumClient client, EventProcessor processor, NoticeStore store, ILogger<EventPoller> logger)
    {
        _client = client;
        _processor = processor;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let Jellyfin finish starting first.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Reading Mediarium's events failed");
            }

            // With a webhook, polling is only a safety net.
            var webhook = !string.IsNullOrWhiteSpace(Plugin.Instance?.Configuration.JellyfinUrl);
            await Task.Delay(webhook ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(20), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Reads and handles the events after the cursor.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!MediariumClient.IsConfigured)
        {
            return;
        }

        while (true)
        {
            var path = "events?limit=100&after=" + _store.Cursor.ToString(CultureInfo.InvariantCulture);
            var response = await _client.SendAsync(HttpMethod.Get, path, null, null, cancellationToken).ConfigureAwait(false);
            if (!response.Ok)
            {
                _logger.LogDebug("GET /events answered {Status}: {Message}", response.Status, response.ErrorMessage);
                return;
            }

            var events = JsonSerializer.Deserialize<List<MediariumEvent>>(response.Body) ?? new List<MediariumEvent>();
            foreach (var evt in events)
            {
                await _processor.HandleAsync(evt, cancellationToken).ConfigureAwait(false);
                _store.Cursor = evt.Id;
            }

            if (events.Count < 100)
            {
                return;
            }
        }
    }
}
