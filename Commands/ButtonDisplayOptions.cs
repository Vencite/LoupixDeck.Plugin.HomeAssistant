namespace LoupixDeck.Plugin.HomeAssistant.Commands;

/// <summary>
/// Per-button display overrides carried as stable trailing command parameters. Bindings that only
/// contain <c>EntityId</c> keep working unchanged; the host drops empty pieces when it parses a
/// command string back, so missing trailing values simply fall back to the automatic presentation.
/// </summary>
internal sealed record ButtonDisplayOptions(string? Label, string? Icon, bool ShowIcon)
{
    public static ButtonDisplayOptions Default { get; } = new(null, null, true);

    /// <summary>
    /// Reads the overrides from the raw positional parameters. The first value is always the
    /// entity id; the rest are optional. A non-boolean second value is read as the label (the
    /// icon flag was left at its default), and a lone <c>mdi:</c> value is read as the icon, so a
    /// hand-typed <c>Toggle(light.office,mdi:lightbulb)</c> just works.
    /// </summary>
    public static ButtonDisplayOptions FromParameters(string[] parameters)
    {
        if (parameters.Length <= 1) return Default;

        int index = 1;
        bool showIcon = true;
        bool flagParsed = TryParseFlag(parameters[index], out bool flag);
        if (flagParsed)
        {
            showIcon = flag;
            index++;
        }

        if (!flagParsed && parameters.Length == 2 && IsIconReference(parameters[index]))
            return new ButtonDisplayOptions(null, NormalizeIcon(parameters[index]), showIcon);

        string? label = index < parameters.Length ? NullIfBlank(parameters[index++]) : null;
        string? icon = index < parameters.Length ? NormalizeIcon(parameters[index]) : null;
        return new ButtonDisplayOptions(label, icon, showIcon);
    }

    private static bool TryParseFlag(string? value, out bool flag)
    {
        if (bool.TryParse(value, out flag)) return true;
        if (value == "1") { flag = true; return true; }
        if (value == "0") { flag = false; return true; }
        flag = true;
        return false;
    }

    /// <summary>
    /// The host resolves a symbol id from its own library and draws a placeholder for unknown
    /// ids, so any string is safe to pass through; only the <c>mdi:</c> prefix is stripped.
    /// </summary>
    private static bool IsIconReference(string? value) =>
        value is not null && value.Trim().StartsWith("mdi:", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeIcon(string? value)
    {
        string? stripped = StripMdiPrefix(value);
        return string.IsNullOrWhiteSpace(stripped) ? null : stripped.Trim();
    }

    /// <summary>Strips the <c>mdi:</c> prefix Home Assistant uses and returns the bare symbol id.</summary>
    internal static string? StripMdiPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        if (trimmed.StartsWith("mdi:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["mdi:".Length..].Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
