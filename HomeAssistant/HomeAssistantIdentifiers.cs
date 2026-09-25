namespace LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

/// <summary>
/// Validates the Home Assistant identifier formats the plugin accepts from user input before any
/// of it reaches the WebSocket protocol.
/// </summary>
internal static class HomeAssistantIdentifiers
{
    /// <summary>A domain or service name: lowercase ASCII letters, digits and underscores.</summary>
    public static bool IsServiceToken(string? value) => !string.IsNullOrEmpty(value) && value.All(IsTokenChar);

    /// <summary>An entity id: <c>domain.object_id</c> with exactly one dot and lowercase parts.</summary>
    public static bool IsEntityId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        int dot = value.IndexOf('.');
        return dot > 0 && dot == value.LastIndexOf('.') && dot < value.Length - 1 &&
               value.All(character => IsTokenChar(character) || character == '.');
    }

    private static bool IsTokenChar(char character) =>
        char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_';
}
