using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Mediarium.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Mediarium.Api;

/// <summary>
/// An answer from Mediarium: the status and the raw JSON body.
/// </summary>
/// <param name="Status">The HTTP status; 502 when Mediarium couldn't be reached.</param>
/// <param name="Body">The JSON body.</param>
public record MediariumResponse(int Status, string Body)
{
    /// <summary>Gets a value indicating whether the status is 2xx.</summary>
    public bool Ok => Status >= 200 && Status < 300;

    /// <summary>Gets the error sentence Mediarium sent, or the body.</summary>
    public string ErrorMessage
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var e)
                    && e.ValueKind == JsonValueKind.String)
                {
                    return e.GetString() ?? Body;
                }
            }
            catch (JsonException)
            {
            }

            return Body;
        }
    }
}

/// <summary>
/// Talks to Mediarium's /plugin/v1/ API.
/// </summary>
public class MediariumClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediariumClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="userManager">Jellyfin's users.</param>
    public MediariumClient(IHttpClientFactory httpClientFactory, IUserManager userManager)
    {
        _httpClientFactory = httpClientFactory;
        _userManager = userManager;
    }

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets a value indicating whether an address and a key are set.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Config.MediariumUrl) && !string.IsNullOrWhiteSpace(Config.PluginKey);

    /// <summary>
    /// Calls Mediarium with the saved address and key.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path under /plugin/v1/, with any query.</param>
    /// <param name="userId">The Jellyfin user the call is made for, if any.</param>
    /// <param name="jsonBody">The body, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Mediarium's answer.</returns>
    public Task<MediariumResponse> SendAsync(HttpMethod method, string path, Guid? userId, string? jsonBody, CancellationToken cancellationToken)
    {
        var config = Config;
        return SendAsync(config.MediariumUrl, config.PluginKey, method, path, userId, jsonBody, cancellationToken);
    }

    /// <summary>
    /// Calls Mediarium with a given address and key (the Test button tries values before they're saved).
    /// </summary>
    /// <param name="baseUrl">Mediarium's address.</param>
    /// <param name="key">The plugin key.</param>
    /// <param name="method">The method.</param>
    /// <param name="path">The path under /plugin/v1/, with any query.</param>
    /// <param name="userId">The Jellyfin user the call is made for, if any.</param>
    /// <param name="jsonBody">The body, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Mediarium's answer.</returns>
    public async Task<MediariumResponse> SendAsync(string baseUrl, string key, HttpMethod method, string path, Guid? userId, string? jsonBody, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(key))
        {
            return new MediariumResponse(503, Error("The Mediarium plugin isn't set up yet. An administrator can add the address and key in Jellyfin's plugin settings."));
        }

        var url = baseUrl.Trim().TrimEnd('/') + "/plugin/v1/" + path.TrimStart('/');
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Plugin-Key", key.Trim());
        request.Headers.Accept.ParseAdd("application/json");
        if (userId is Guid id && id != Guid.Empty)
        {
            request.Headers.TryAddWithoutValidation("X-Jellyfin-User-Id", id.ToString("N"));
            var name = _userManager.GetUserById(id)?.Username;
            if (!string.IsNullOrEmpty(name))
            {
                // Header values must be ASCII; names that aren't are sent percent-encoded.
                request.Headers.TryAddWithoutValidation("X-Jellyfin-User-Name", IsAscii(name) ? name : Uri.EscapeDataString(name));
            }
        }

        if (jsonBody is not null)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(20);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                body = "{}";
            }

            return new MediariumResponse((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new MediariumResponse((int)HttpStatusCode.BadGateway, Error("Couldn't reach Mediarium at " + baseUrl + ": " + ex.Message));
        }
    }

    /// <summary>Builds Mediarium's error shape.</summary>
    /// <param name="message">The sentence.</param>
    /// <returns>The JSON.</returns>
    public static string Error(string message) => JsonSerializer.Serialize(new { error = message });

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 126 || c < 32)
            {
                return false;
            }
        }

        return true;
    }
}
