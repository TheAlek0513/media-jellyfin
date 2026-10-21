using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Mediarium.Discover;
using Jellyfin.Plugin.Mediarium.Events;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediarium.Api;

/// <summary>
/// The plugin's own endpoints under /MediariumRequests.
/// </summary>
[ApiController]
[Route("MediariumRequests")]
public partial class MediariumController : ControllerBase
{
    private readonly MediariumClient _client;
    private readonly IAuthorizationContext _authContext;
    private readonly EventProcessor _processor;
    private readonly NoticeStore _store;
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediariumController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediariumController"/> class.
    /// </summary>
    /// <param name="client">The Mediarium client.</param>
    /// <param name="authContext">Jellyfin's authorization context.</param>
    /// <param name="processor">The event processor.</param>
    /// <param name="store">The notice store.</param>
    /// <param name="sessionManager">Jellyfin's sessions.</param>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="logger">The logger.</param>
    public MediariumController(
        MediariumClient client,
        IAuthorizationContext authContext,
        EventProcessor processor,
        NoticeStore store,
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        ILogger<MediariumController> logger)
    {
        _client = client;
        _authContext = authContext;
        _processor = processor;
        _store = store;
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>The script added to Jellyfin Web, also used by the plugin's own page.</summary>
    /// <returns>The script.</returns>
    [HttpGet("client.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetScript() => Resource("Web.client.js", "application/javascript; charset=utf-8");

    /// <summary>The Request page on its own, for when it isn't added to Jellyfin Web.</summary>
    /// <returns>The page.</returns>
    [HttpGet("Page")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Text.Html)]
    public ActionResult GetPage() => Resource("Web.page.html", "text/html; charset=utf-8");

    /// <summary>Calls Mediarium's API for the signed-in person.</summary>
    /// <param name="path">The path under /plugin/v1/.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Mediarium's answer, as it is.</returns>
    [HttpGet("Api/{**path}")]
    [HttpPost("Api/{**path}")]
    [HttpDelete("Api/{**path}")]
    [Authorize]
    public async Task<ActionResult> Proxy([FromRoute] string path, CancellationToken cancellationToken)
    {
        var method = new HttpMethod(Request.Method);
        if (!Allowed(method, path))
        {
            return Json(404, MediariumClient.Error("Not found."));
        }

        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        string? body = null;
        if (method == HttpMethod.Post)
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8);
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var response = await _client.SendAsync(method, path + Request.QueryString.Value, auth.UserId, body, cancellationToken).ConfigureAwait(false);
        return Json(response.Status, response.Body);
    }

    /// <summary>What the script needs to know about this server: the Discover library's id.</summary>
    /// <returns>The info.</returns>
    [HttpGet("Info")]
    [Authorize]
    public ActionResult GetInfo()
    {
        var channelId = _libraryManager.GetNewItemId("Channel " + DiscoverChannel.ChannelName, typeof(Channel));
        return Json(200, JsonSerializer.Serialize(new { discoverChannelId = channelId.ToString("N") }, JsonOptions));
    }

    /// <summary>Says whether an item is a Discover placeholder, and which title it stands for.</summary>
    /// <param name="itemId">The Jellyfin item.</param>
    /// <returns>The title's media and TMDB id, or 404.</returns>
    [HttpGet("Placeholder/{itemId}")]
    [Authorize]
    public ActionResult GetPlaceholder([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null
            || item.SourceType != SourceType.Channel
            || !DiscoverChannel.TryParseId(item.ExternalId, out var media, out var tmdbId)
            || !item.ChannelId.Equals(_libraryManager.GetNewItemId("Channel " + DiscoverChannel.ChannelName, typeof(Channel))))
        {
            return Json(404, MediariumClient.Error("Not a Discover title."));
        }

        return Json(200, JsonSerializer.Serialize(new { media, tmdbId, title = item.Name }, JsonOptions));
    }

    /// <summary>The signed-in person's unseen "ready to watch" notices. Also tells the plugin this device shows its own pop-up.</summary>
    /// <param name="deviceId">The Jellyfin Web device asking.</param>
    /// <returns>The notices.</returns>
    [HttpGet("Notices")]
    [Authorize]
    public async Task<ActionResult> GetNotices([FromQuery] string? deviceId)
    {
        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        _store.Heartbeat(deviceId ?? auth.DeviceId);
        return Json(200, JsonSerializer.Serialize(_store.GetNotices(auth.UserId), JsonOptions));
    }

    /// <summary>Drops a notice the person saw.</summary>
    /// <param name="eventId">The event's id.</param>
    /// <returns>No content.</returns>
    [HttpDelete("Notices/{eventId}")]
    [Authorize]
    public async Task<ActionResult> DismissNotice([FromRoute] long eventId)
    {
        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        _store.Dismiss(auth.UserId, eventId);
        return NoContent();
    }

    /// <summary>Starts playing an item on the person's own Jellyfin Web tab.</summary>
    /// <param name="body">The item and the device.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>No content.</returns>
    [HttpPost("Play")]
    [Authorize]
    public async Task<ActionResult> Play([FromBody] PlayBody body, CancellationToken cancellationToken)
    {
        var auth = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (!Guid.TryParse(body.ItemId, out var itemId))
        {
            return Json(400, MediariumClient.Error("Nothing to play."));
        }

        var deviceId = string.IsNullOrEmpty(body.DeviceId) ? auth.DeviceId : body.DeviceId;
        var session = _sessionManager.Sessions.FirstOrDefault(s => s.UserId.Equals(auth.UserId) && string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal));
        if (session is null)
        {
            return Json(404, MediariumClient.Error("Couldn't find this Jellyfin window. Open the title from the library instead."));
        }

        await _sessionManager.SendPlayCommand(null, session.Id, new PlayRequest { ItemIds = new[] { itemId }, PlayCommand = PlayCommand.PlayNow }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Where Mediarium sends events.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>200 once handled.</returns>
    [HttpPost("Event")]
    [AllowAnonymous]
    public async Task<ActionResult> ReceiveEvent(CancellationToken cancellationToken)
    {
        var secret = Plugin.Instance?.Configuration.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return StatusCode(503);
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        var raw = ms.ToArray();
        if (!SignatureMatches(raw, secret, Request.Headers["X-Mediarium-Signature"].ToString()))
        {
            _logger.LogWarning("Ignored an event with a wrong signature from {Remote}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        MediariumEvent? evt;
        try
        {
            evt = JsonSerializer.Deserialize<MediariumEvent>(raw);
        }
        catch (JsonException)
        {
            return BadRequest();
        }

        if (evt is null)
        {
            return BadRequest();
        }

        await _processor.HandleAsync(evt, cancellationToken).ConfigureAwait(false);
        return Ok();
    }

    /// <summary>The Test button: pings Mediarium with the values on the settings page.</summary>
    /// <param name="body">The address and key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Mediarium's /ping answer.</returns>
    [HttpPost("Admin/Test")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Test([FromBody] TestBody body, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(body.MediariumUrl ?? string.Empty, body.PluginKey ?? string.Empty, HttpMethod.Get, "ping", null, null, cancellationToken).ConfigureAwait(false);
        return Json(response.Status, response.Body);
    }

    /// <summary>Tells Mediarium where to send events (or to stop), with the saved settings.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Mediarium's answer.</returns>
    [HttpPost("Admin/Register")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult> Register(CancellationToken cancellationToken)
    {
        var plugin = Plugin.Instance!;
        var config = plugin.Configuration;
        if (string.IsNullOrEmpty(config.WebhookSecret))
        {
            config.WebhookSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            plugin.SaveConfiguration();
        }

        var callback = string.IsNullOrWhiteSpace(config.JellyfinUrl)
            ? string.Empty
            : config.JellyfinUrl.Trim().TrimEnd('/') + "/MediariumRequests/Event";
        var payload = JsonSerializer.Serialize(new { callbackUrl = callback, secret = config.WebhookSecret });
        var response = await _client.SendAsync(HttpMethod.Post, "register", null, payload, cancellationToken).ConfigureAwait(false);
        return Json(response.Status, response.Body);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Checks Mediarium's signature on an event.</summary>
    /// <param name="body">The raw body.</param>
    /// <param name="secret">The shared secret.</param>
    /// <param name="header">The X-Mediarium-Signature header.</param>
    /// <returns>True when it matches.</returns>
    public static bool SignatureMatches(byte[] body, string secret, string header)
    {
        const string Prefix = "sha256=";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        byte[] given;
        try
        {
            given = Convert.FromHexString(header.AsSpan(Prefix.Length).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    private static bool Allowed(HttpMethod method, string path)
    {
        if (method == HttpMethod.Get)
        {
            return GetPaths().IsMatch(path);
        }

        if (method == HttpMethod.Post)
        {
            return path == "requests";
        }

        return method == HttpMethod.Delete && DeletePaths().IsMatch(path);
    }

    [GeneratedRegex(@"^(discover/(movie|tv|mixed)/(trending|popular)|search|titles/(movie|tv)/\d+|quota|requests)$")]
    private static partial Regex GetPaths();

    [GeneratedRegex(@"^requests/\d+$")]
    private static partial Regex DeletePaths();

    private ContentResult Json(int status, string body) => new()
    {
        StatusCode = status,
        Content = body,
        ContentType = "application/json; charset=utf-8",
    };

    private ActionResult Resource(string name, string contentType)
    {
        var stream = GetType().Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + "." + name);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, contentType);
    }

    /// <summary>The Test button's body.</summary>
    public class TestBody
    {
        /// <summary>Gets or sets Mediarium's address.</summary>
        public string? MediariumUrl { get; set; }

        /// <summary>Gets or sets the plugin key.</summary>
        public string? PluginKey { get; set; }
    }

    /// <summary>The Play button's body.</summary>
    public class PlayBody
    {
        /// <summary>Gets or sets the item to play.</summary>
        public string? ItemId { get; set; }

        /// <summary>Gets or sets the device to play it on.</summary>
        public string? DeviceId { get; set; }
    }
}
