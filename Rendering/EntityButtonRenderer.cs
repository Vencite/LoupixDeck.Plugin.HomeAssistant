using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Rendering;

/// <summary>
/// Draws the cached state of one Home Assistant entity onto a touch button. Strictly synchronous and
/// cache-only: it never performs network I/O, and a missing entity stays visible as a dim placeholder
/// so a misconfigured parameter is easy to spot.
/// The button is split into three stacked, never-overlapping zones: the state indicator on top,
/// the short state text in the middle and the friendly name at the bottom. Long names are wrapped
/// to at most two lines and ellipsized instead of shrinking the font.
/// An optional <see cref="Commands.ButtonDisplayOptions"/> overrides the label and the icon per
/// button; resolving those overrides touches no network and never throws rendering off.
/// </summary>
internal static class EntityButtonRenderer
{
    private const string Ellipsis = "…";

    private static readonly PluginColor Active = new(255, 193, 7);
    private static readonly PluginColor Inactive = new(158, 158, 158);
    private static readonly PluginColor Missing = new(110, 110, 110);

    public static bool Render(HomeAssistantState? state, string entityId, IRenderCanvas canvas) =>
        Render(state, entityId, null, canvas);

    public static bool Render(HomeAssistantState? state, string entityId,
        Commands.ButtonDisplayOptions? options, IRenderCanvas canvas)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return false;
        if (canvas.Width <= 0 || canvas.Height <= 0) return false;

        bool active = state is not null && IsActive(state.State);
        PluginColor accent = state is null ? Missing : active ? Active : Inactive;

        int width = canvas.Width;
        int height = canvas.Height;
        int size = Math.Min(width, height);

        int side = Math.Max(2, width / 22);
        int top = Math.Max(2, height / 22);
        int bottom = Math.Max(2, height / 22);
        int gap = Math.Max(2, height / 45);

        int radius = Math.Max(6, size / 8);
        int stroke = Math.Max(2, radius / 4);
        int centerX = width / 2;
        int centerY = top + radius + stroke;

        // Hiding the icon removes the whole indicator zone, so the state text and the label move
        // up and use the freed space.
        bool showIcon = options?.ShowIcon != false;
        string? symbol = showIcon ? ResolveSymbol(state, options?.Icon) : null;
        if (showIcon)
        {
            if (symbol is not null)
                canvas.DrawSymbol(symbol, centerX - radius - stroke, centerY - radius - stroke,
                    (radius + stroke) * 2, (radius + stroke) * 2, accent);
            else if (active) canvas.FillCircle(centerX, centerY, radius, accent);
            else canvas.DrawCircle(centerX, centerY, radius, stroke, accent);
        }

        int indicatorBottom = showIcon ? centerY + radius + stroke : top;
        int boxWidth = Math.Max(0, width - side * 2);

        string stateText = state?.State.Trim() ?? string.Empty;
        if (state?.Domain == "sensor" &&
            state.Attributes.ValueKind == System.Text.Json.JsonValueKind.Object &&
            state.Attributes.TryGetProperty("unit_of_measurement", out System.Text.Json.JsonElement unit) &&
            unit.ValueKind == System.Text.Json.JsonValueKind.String &&
            unit.GetString() is { Length: > 0 } unitText)
            stateText += $" {unitText}";
        int nameTop;
        if (stateText.Length > 0 && boxWidth > 0)
        {
            int stateHeight = Math.Min(Math.Max(12, size / 6), Math.Max(0, height - bottom - indicatorBottom - gap));
            if (stateHeight > 0)
            {
                float stateFont = Math.Max(9f, size / 8f);
                string fittedState = Ellipsize(stateText, boxWidth, stateFont, bold: false, canvas);
                canvas.DrawText(fittedState, side, indicatorBottom + gap, boxWidth, stateHeight,
                    Inactive, stateFont, TextHAlign.Center, TextVAlign.Middle);
                nameTop = indicatorBottom + gap + stateHeight + gap;
            }
            else nameTop = indicatorBottom + gap;
        }
        else nameTop = indicatorBottom + gap;

        int nameHeight = height - bottom - nameTop;
        if (boxWidth <= 0 || nameHeight <= 0) return true;

        string label = options?.Label?.Trim() is { Length: > 0 } custom
            ? custom
            : (state?.FriendlyName ?? entityId.Replace('_', ' ')).Trim();
        if (label.Length == 0) label = entityId;

        float nameFont = Math.Max(10f, size / 7f);
        string fittedName = FitTwoLines(label, boxWidth, nameFont, canvas);
        canvas.DrawText(fittedName, side, nameTop, boxWidth, nameHeight, accent,
            nameFont, TextHAlign.Center, TextVAlign.Top, bold: true);

        return true;
    }

    private static string FitTwoLines(string label, float maxWidth, float fontSize, IRenderCanvas canvas)
    {
        string[] words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return label.Trim();
        if (words.Length == 1) return Ellipsize(words[0], maxWidth, fontSize, bold: true, canvas);

        string line = words[0];
        int next = 1;
        while (next < words.Length)
        {
            string candidate = line + ' ' + words[next];
            if (canvas.MeasureText(candidate, fontSize, bold: true) > maxWidth) break;
            line = candidate;
            next++;
        }

        if (next >= words.Length) return line;
        if (canvas.MeasureText(line, fontSize, bold: true) > maxWidth)
            return Ellipsize(line, maxWidth, fontSize, bold: true, canvas);

        string rest = string.Join(' ', words.Skip(next));
        if (canvas.MeasureText(rest, fontSize, bold: true) <= maxWidth) return line + '\n' + rest;
        return line + '\n' + Ellipsize(rest, maxWidth, fontSize, bold: true, canvas);
    }

    private static string Ellipsize(string text, float maxWidth, float fontSize, bool bold, IRenderCanvas canvas)
    {
        if (canvas.MeasureText(text, fontSize, bold) <= maxWidth) return text;
        if (canvas.MeasureText(Ellipsis, fontSize, bold) > maxWidth) return Ellipsis;

        int length = text.Length - 1;
        while (length > 0 && canvas.MeasureText(text[..length] + Ellipsis, fontSize, bold) > maxWidth) length--;
        return length <= 0 ? Ellipsis : text[..length].TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// Resolves the symbol for the indicator zone without any network I/O: an explicit per-button
    /// icon wins, then the entity's own icon, device class and domain defaults.
    /// </summary>
    private static string? ResolveSymbol(HomeAssistantState? state, string? iconOverride)
    {
        if (!string.IsNullOrWhiteSpace(iconOverride)) return iconOverride.Trim();
        if (state is not null &&
            state.Attributes.ValueKind == System.Text.Json.JsonValueKind.Object &&
            state.Attributes.TryGetProperty("icon", out System.Text.Json.JsonElement icon) &&
            icon.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string? custom = Commands.ButtonDisplayOptions.StripMdiPrefix(icon.GetString());
            if (custom is not null) return custom;
        }
        if (state is null) return null;
        if (state.Attributes.ValueKind == System.Text.Json.JsonValueKind.Object &&
            state.Attributes.TryGetProperty("device_class", out System.Text.Json.JsonElement deviceClass) &&
            deviceClass.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string? classified = (state.Domain, deviceClass.GetString()) switch
            {
                ("sensor", "temperature") => "thermometer",
                ("sensor", "humidity") => "water-percent",
                ("sensor", "battery") => "battery",
                ("binary_sensor", "motion") => "motion-sensor",
                ("binary_sensor", "door") => "door",
                ("binary_sensor", "window") => "window-closed",
                _ => null
            };
            if (classified is not null) return classified;
        }
        return state.Domain switch
        {
            "light" => state.State == "on" ? "lightbulb" : "lightbulb-outline",
            "switch" => state.State == "on" ? "toggle-switch" : "toggle-switch-off",
            "binary_sensor" => "checkbox-blank-circle-outline",
            "sensor" => "eye",
            "scene" => "palette",
            "script" => "script-text",
            "button" => "gesture-tap-button",
            _ => null
        };
    }

    private static bool IsActive(string state) =>
        state is "on" or "open" or "opening" or "playing" or "home" or "unlocked" or "cleaning"
            or "heat" or "cool" or "auto" or "dry" or "fan_only";
}
