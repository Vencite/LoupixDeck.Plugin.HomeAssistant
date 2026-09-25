using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal sealed class ConnectionStatusCommand(HomeAssistantCommandAccess access) : IDisplayImageCommand
{
    public const string Name = "HomeAssistant.ConnectionStatus";
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name, DisplayName = "Connection status", Group = "Home Assistant",
        Description = "Show HA connection and the last entity update.",
        ParameterTemplate = string.Empty, HiddenFromMenu = true
    };
    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(30);
    public Task Execute(CommandContext ctx) => Task.CompletedTask;

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        if (canvas.Width <= 0 || canvas.Height <= 0) return false;
        string status = access.Status ?? "Connected";
        bool connected = status == "Connected";
        PluginColor color = connected ? new PluginColor(75, 190, 126) : new PluginColor(220, 95, 95);
        int side = Math.Max(2, canvas.Width / 18);
        int width = canvas.Width - 2 * side;
        int center = canvas.Width / 2;
        int radius = Math.Max(3, Math.Min(canvas.Width, canvas.Height) / 14);
        canvas.FillCircle(center, Math.Max(8, canvas.Height / 7), radius, color);
        canvas.DrawText(status, side, canvas.Height / 4, width, canvas.Height / 4,
            color, 11, TextHAlign.Center, TextVAlign.Middle, bold: true);
        string updated = access.LastUpdatedAt?.ToLocalTime().ToString("dd.MM HH:mm") ?? "No sync";
        canvas.DrawText(updated, side, canvas.Height * 3 / 5, width, canvas.Height / 4,
            new PluginColor(200, 200, 200), 10, TextHAlign.Center, TextVAlign.Middle);
        return true;
    }
}
