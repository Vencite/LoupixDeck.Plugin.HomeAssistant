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
    private static readonly PluginColor Unavailable = new(220, 95, 95);
    private static readonly PluginColor Unknown = new(100, 165, 220);
    private static readonly PluginColor Missing = new(110, 110, 110);
    // ponytail: mirror the host's curated ids until the SDK exposes symbol validation; unknown MDI names draw a dashed box.
    private static readonly HashSet<string> HostSymbols = new((
        "play pause stop record skip-previous skip-next rewind fast-forward repeat shuffle eject movie-open-outline " +
        "volume-high volume-medium volume-low volume-off volume-mute microphone microphone-off headphones speaker music equalizer tune " +
        "camera camera-off video video-off webcam monitor monitor-screenshot broadcast television cast " +
        "lightbulb lightbulb-on lightbulb-off white-balance-sunny weather-night brightness-6 flash flashlight " +
        "power power-plug cog restart sleep lock lock-open folder folder-open file home web magnify delete refresh sync " +
        "download upload content-copy content-paste content-cut content-save email message chat phone bell bell-off send account " +
        "arrow-up arrow-down arrow-left arrow-right chevron-up chevron-down chevron-left chevron-right undo redo exit-to-app " +
        "menu dots-horizontal star star-outline heart heart-outline check close plus minus alert alert-circle information help-circle " +
        "eye eye-off flag bookmark tag fire rocket-launch trophy gift thumb-up keyboard mouse desktop-classic laptop " +
        "gamepad-variant wifi bluetooth usb printer calendar clock image palette pencil numeric-1-box numeric-2-box numeric-3-box"
        ).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    public static bool Render(HomeAssistantState? state, string entityId, IRenderCanvas canvas) =>
        Render(state, entityId, null, canvas);

    public static bool Render(HomeAssistantState? state, string entityId,
        Commands.ButtonDisplayOptions? options, IRenderCanvas canvas, string? connectionStatus = null)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return false;
        if (canvas.Width <= 0 || canvas.Height <= 0) return false;

        bool active = connectionStatus is null && state is not null && IsActive(state.State);
        PluginColor accent = connectionStatus is not null ? Unavailable : state?.State switch
        {
            null => Missing,
            "unavailable" => Unavailable,
            "unknown" => Unknown,
            _ => active ? Active : Inactive
        };

        int width = canvas.Width;
        int height = canvas.Height;
        int size = Math.Min(width, height);

        int side = Math.Max(2, width / 22);
        int top = Math.Max(2, height / 22);
        int bottom = Math.Max(2, height / 22);
        int gap = Math.Max(2, height / 45);
        int boxWidth = Math.Max(0, width - side * 2);

        string label = options?.Label?.Trim() is { Length: > 0 } custom
            ? custom
            : (state?.FriendlyName ?? entityId.Replace('_', ' ')).Trim();
        if (label.Length == 0) label = entityId;
        float nameFont = options?.LabelTextSize ?? Math.Max(10f, size / 7f);
        string fittedName = FitTwoLines(label, boxWidth, nameFont, canvas);
        int nameLines = fittedName.Contains('\n') ? 2 : 1;
        int reservedNameHeight = (int)Math.Ceiling(nameFont * 1.2f * nameLines);
        float stateFont = options?.StateTextSize ?? Math.Max(9f, size / 8f);
        int reservedStateHeight = (int)Math.Ceiling(stateFont * 1.25f);

        // Reserve both text blocks first, including the full icon stroke. Small keys or large
        // fonts may leave no room for an icon; text remains readable in that case.
        int iconSpace = height - top - bottom - reservedNameHeight - reservedStateHeight - gap * 2;
        int radius = Math.Min(size / 5, Math.Max(0, (iconSpace - 4) / 2));
        int stroke = Math.Max(2, radius / 4);
        while (radius > 0 && (radius + stroke) * 2 > iconSpace)
        {
            radius--;
            stroke = Math.Max(2, radius / 4);
        }
        int centerX = width / 2;
        int centerY = top + radius + stroke;

        // Hiding the icon removes the whole indicator zone, so the state text and the label move
        // up and use the freed space.
        bool showIcon = options?.ShowIcon != false && radius >= 3;
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

        string stateText = connectionStatus ?? state?.State.Trim() ?? "No state";
        if (connectionStatus is null && state?.Domain == "climate")
        {
            double? target = Commands.EntityCapabilities.Number(state, "temperature");
            double? low = Commands.EntityCapabilities.Number(state, "target_temp_low");
            double? high = Commands.EntityCapabilities.Number(state, "target_temp_high");
            if (target is { } value) stateText += $" · {value:0.#}°";
            else if (low is { } lower && high is { } upper) stateText += $" · {lower:0.#}–{upper:0.#}°";
        }
        if (connectionStatus is null && state?.Domain == "sensor" &&
            state.Attributes.ValueKind == System.Text.Json.JsonValueKind.Object &&
            state.Attributes.TryGetProperty("unit_of_measurement", out System.Text.Json.JsonElement unit) &&
            unit.ValueKind == System.Text.Json.JsonValueKind.String &&
            unit.GetString() is { Length: > 0 } unitText)
            stateText += $" {unitText}";
        int nameTop;
        if (stateText.Length > 0 && boxWidth > 0)
        {
            int stateHeight = Math.Min(reservedStateHeight, Math.Max(0, height - bottom - indicatorBottom - gap));
            if (stateHeight > 0)
            {
                string fittedState = Ellipsize(stateText, boxWidth, stateFont, bold: false, canvas);
                canvas.DrawText(fittedState, side, indicatorBottom + gap, boxWidth, stateHeight,
                    accent, stateFont, TextHAlign.Center, TextVAlign.Middle);
                nameTop = indicatorBottom + gap + stateHeight + gap;
            }
            else nameTop = indicatorBottom + gap;
        }
        else nameTop = indicatorBottom + gap;

        int nameHeight = height - bottom - nameTop;
        if (boxWidth <= 0 || nameHeight <= 0) return true;

        if (nameLines == 2 && nameHeight < reservedNameHeight)
            fittedName = Ellipsize(label, boxWidth, nameFont, bold: true, canvas);
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
        string? custom = SupportedSymbol(iconOverride);
        if (custom is not null) return custom;
        custom = SupportedSymbol(state?.RegistryIcon);
        if (custom is not null) return custom;
        if (state is not null &&
            state.Attributes.ValueKind == System.Text.Json.JsonValueKind.Object &&
            state.Attributes.TryGetProperty("icon", out System.Text.Json.JsonElement icon) &&
            icon.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            custom = SupportedSymbol(icon.GetString());
            if (custom is not null) return custom;
        }
        if (state is null) return null;
        if (state.Attributes.ValueKind == System.Text.Json.JsonValueKind.Object &&
            state.Attributes.TryGetProperty("device_class", out System.Text.Json.JsonElement deviceClass) &&
            deviceClass.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            string? classified = (state.Domain, deviceClass.GetString()) switch
            {
                ("sensor", "temperature") => "brightness-6",
                ("sensor", "humidity") => "brightness-6",
                ("sensor", "battery") => "power",
                ("binary_sensor", "motion") => "eye",
                ("binary_sensor", "door") => "home",
                ("binary_sensor", "window") => "home",
                _ => null
            };
            if (classified is not null) return classified;
        }
        // LoupixDeck currently resolves only its curated SymbolLibrary ids. Keep defaults there;
        // an arbitrary MDI name would be drawn as a dashed missing-symbol box.
        return state.Domain switch
        {
            "light" => state.State == "on" ? "lightbulb-on" : "lightbulb-off",
            "switch" => "power",
            "binary_sensor" => "eye",
            "sensor" => "eye",
            "scene" => "palette",
            "script" => "play",
            "button" => "power",
            _ => null
        };
    }

    private static string? SupportedSymbol(string? icon)
    {
        string? id = Commands.ButtonDisplayOptions.StripMdiPrefix(icon);
        return id is not null && HostSymbols.Contains(id) ? id : null;
    }

    private static bool IsActive(string state) =>
        state is "on" or "open" or "opening" or "playing" or "home" or "unlocked" or "cleaning"
            or "heat" or "cool" or "auto" or "dry" or "fan_only";
}
