using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal sealed class BrightnessCommand(HomeAssistantCommandAccess access) : IAdjustmentCommand
{
    public const string Name = "HomeAssistant.AdjustBrightness";
    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name, DisplayName = "Light brightness", Group = "Home Assistant",
        ParameterTemplate = "({EntityId})", Parameters = [new CommandParameter("EntityId", typeof(string))], HiddenFromMenu = true
    };
    public ButtonTargets SupportedTargets => ButtonTargets.RotaryEncoder;
    public Task Execute(CommandContext ctx) => ApplyReset(ctx);
    public Task ApplyReset(CommandContext ctx) => Send(ctx, 0);
    public Task ApplyAdjustment(CommandContext ctx, int ticks) => ticks == 0 ? Task.CompletedTask : Send(ctx, ticks);
    private async Task Send(CommandContext ctx, int ticks)
    {
        var entity = access.FindEntity(ctx.Parameters.FirstOrDefault() ?? "");
        if (entity is null || !EntityCapabilities.Brightness(entity) || entity.State is "unavailable" or "unknown" ||
            access.Client is not { State: HomeAssistantConnectionState.Connected } client) return;
        try
        {
            await client.CallServiceAsync("light", ticks == 0 ? "toggle" : "turn_on",
                JsonSerializer.SerializeToElement(new { entity_id = entity.EntityId }),
                ticks == 0 ? null : JsonSerializer.SerializeToElement(new { brightness_step_pct = Math.Clamp((long)ticks * 5, -100, 100) }));
        }
        catch (Exception ex) { ctx.Host.Logger.Warn($"Brightness adjustment failed ({ex.GetType().Name})."); }
    }
    public AdjustmentValue? GetValue(CommandContext ctx)
    {
        if (access.Client?.State != HomeAssistantConnectionState.Connected) return new(0, "Offline");
        var entity = access.FindEntity(ctx.Parameters.FirstOrDefault() ?? "");
        if (entity is null || entity.State is "unavailable" or "unknown") return new(0, entity?.State ?? "No state");
        double? brightness = entity.State == "off" ? 0 : EntityCapabilities.Number(entity, "brightness");
        return brightness is { } value ? new(Math.Clamp(value / 255, 0, 1), $"{Math.Clamp(value / 255 * 100, 0, 100):0}%") : new(0, "No value");
    }
}
