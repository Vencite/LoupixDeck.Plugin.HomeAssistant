using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Rendering;

/// <summary>
/// Draws the cached state of one Home Assistant entity onto a touch button. Strictly synchronous and
/// cache-only: it never performs network I/O, and a missing entity stays visible as a dim placeholder
/// so a misconfigured parameter is easy to spot.
/// </summary>
internal static class EntityButtonRenderer
{
    private static readonly PluginColor Active = new(255, 193, 7);
    private static readonly PluginColor Inactive = new(158, 158, 158);
    private static readonly PluginColor Missing = new(110, 110, 110);

    public static bool Render(HomeAssistantState? state, string entityId, IRenderCanvas canvas)
    {
        if (string.IsNullOrWhiteSpace(entityId)) return false;

        bool active = state is not null && IsActive(state.State);
        PluginColor accent = state is null ? Missing : active ? Active : Inactive;

        int size = Math.Min(canvas.Width, canvas.Height);
        int radius = Math.Max(6, size / 7);
        int centerX = canvas.Width / 2;
        int centerY = (int)(canvas.Height * 0.38);

        if (active) canvas.FillCircle(centerX, centerY, radius, accent);
        else canvas.DrawCircle(centerX, centerY, radius, Math.Max(2, radius / 4), accent);

        string label = state?.FriendlyName ?? entityId.Replace('_', ' ');
        int nameTop = (int)(canvas.Height * 0.62);
        canvas.DrawText(label, 2, nameTop, canvas.Width - 4, canvas.Height - nameTop - 2, accent,
            size / 7f, bold: true);

        if (state is not null)
        {
            int stateHeight = Math.Max(12, size / 5);
            canvas.DrawText(state.State, 2, canvas.Height - stateHeight - 2, canvas.Width - 4, stateHeight,
                Inactive, size / 9f);
        }

        return true;
    }

    private static bool IsActive(string state) =>
        state is "on" or "open" or "opening" or "playing" or "home" or "unlocked" or "cleaning"
            or "heat" or "cool" or "auto" or "dry" or "fan_only";
}
