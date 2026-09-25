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
/// </summary>
internal static class EntityButtonRenderer
{
    private const string Ellipsis = "…";

    private static readonly PluginColor Active = new(255, 193, 7);
    private static readonly PluginColor Inactive = new(158, 158, 158);
    private static readonly PluginColor Missing = new(110, 110, 110);

    public static bool Render(HomeAssistantState? state, string entityId, IRenderCanvas canvas)
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

        if (active) canvas.FillCircle(centerX, centerY, radius, accent);
        else canvas.DrawCircle(centerX, centerY, radius, stroke, accent);

        int indicatorBottom = centerY + radius + stroke;
        int boxWidth = Math.Max(0, width - side * 2);

        string stateText = state?.State.Trim() ?? string.Empty;
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

        string label = (state?.FriendlyName ?? entityId.Replace('_', ' ')).Trim();
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

    private static bool IsActive(string state) =>
        state is "on" or "open" or "opening" or "playing" or "home" or "unlocked" or "cleaning"
            or "heat" or "cool" or "auto" or "dry" or "fan_only";
}
