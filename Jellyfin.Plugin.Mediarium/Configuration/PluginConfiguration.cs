using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Mediarium.Configuration;

/// <summary>
/// What the settings page stores.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets Mediarium's address, with its URL base if it has one.</summary>
    public string MediariumUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the key from Mediarium's Settings > Other apps.</summary>
    public string PluginKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets this Jellyfin's address as Mediarium reaches it, used for the webhook.
    /// Empty means Mediarium doesn't send events and the plugin reads them itself.
    /// </summary>
    public string JellyfinUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the secret Mediarium signs events with. Made on first save.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the Request page is added to Jellyfin Web.</summary>
    public bool AddToJellyfinWeb { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether ready titles go into a "Ready for you" playlist.</summary>
    public bool ReadyPlaylist { get; set; } = true;
}
