namespace LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

/// <summary>
/// Immutable snapshot of the settings that define one Home Assistant session. Value equality
/// lets the plugin skip a reconnect when a save did not actually change anything.
/// </summary>
public sealed record HomeAssistantConnectionSettings(string Url, string AccessToken)
{
    /// <summary>True when both a URL and an access token are present.</summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(AccessToken);
}
