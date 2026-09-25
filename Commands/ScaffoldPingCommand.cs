using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

/// <summary>
/// Temporary scaffold command used only to verify that the plugin loads and executes.
/// Replace/remove it when real Home Assistant commands are implemented.
/// </summary>
internal sealed class ScaffoldPingCommand : IPluginCommand
{
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = "HomeAssistant.ScaffoldPing",
        DisplayName = "Scaffold ping",
        Group = "Home Assistant",
        Description = "Temporary development command."
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public Task Execute(CommandContext ctx)
    {
        ctx.Host.Logger.Info("Home Assistant scaffold ping.");
        return Task.CompletedTask;
    }
}
