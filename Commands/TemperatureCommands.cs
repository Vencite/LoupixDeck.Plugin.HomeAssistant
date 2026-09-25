using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal abstract class TemperatureCommand(HomeAssistantCommandAccess access, int direction) : EntityCommand(access)
{
    protected override string? DefaultLabel => direction > 0 ? "Temperature +" : "Temperature −";

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters)
    {
        HomeAssistantState? state = Access.FindEntity(entityId);
        if (state is null || state.Domain != "climate" || state.State is "unavailable" or "unknown")
            return Task.CompletedTask;

        double step = EntityCapabilities.Number(state, "target_temp_step") is { } configured && configured is > 0 and <= 10
            ? configured : 0.5;
        double delta = step * direction;
        double min = EntityCapabilities.Number(state, "min_temp") ?? double.NegativeInfinity;
        double max = EntityCapabilities.Number(state, "max_temp") ?? double.PositiveInfinity;
        object data;
        if (EntityCapabilities.Has(state, 1) && EntityCapabilities.Number(state, "temperature") is { } current)
            data = new { temperature = Math.Clamp(current + delta, min, max) };
        else if (EntityCapabilities.Has(state, 2) &&
            EntityCapabilities.Number(state, "target_temp_low") is { } low &&
            EntityCapabilities.Number(state, "target_temp_high") is { } high)
        {
            double bounded = Math.Clamp(delta, min - low, max - high);
            data = new { target_temp_low = low + bounded, target_temp_high = high + bounded };
        }
        else return Task.CompletedTask;
        return client.CallServiceAsync("climate", "set_temperature",
            JsonSerializer.SerializeToElement(new { entity_id = entityId }), JsonSerializer.SerializeToElement(data));
    }
}

internal sealed class IncreaseTemperatureCommand(HomeAssistantCommandAccess access) : TemperatureCommand(access, 1)
{
    public const string Name = "HomeAssistant.IncreaseTemperature";
    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Increase temperature", "Raise the climate target temperature by one supported step.");
}

internal sealed class DecreaseTemperatureCommand(HomeAssistantCommandAccess access) : TemperatureCommand(access, -1)
{
    public const string Name = "HomeAssistant.DecreaseTemperature";
    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Decrease temperature", "Lower the climate target temperature by one supported step.");
}
