using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal static class EntityCapabilities
{
    public static bool Has(HomeAssistantState entity, int flag) =>
        entity.Attributes.ValueKind == JsonValueKind.Object &&
        entity.Attributes.TryGetProperty("supported_features", out var features) &&
        features.ValueKind == JsonValueKind.Number && features.TryGetInt32(out int value) && (value & flag) == flag;

    public static string[] Strings(HomeAssistantState entity, string attribute) =>
        entity.Attributes.ValueKind == JsonValueKind.Object &&
        entity.Attributes.TryGetProperty(attribute, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray() : [];

    public static bool Brightness(HomeAssistantState entity) => entity.Domain == "light" &&
        Strings(entity, "supported_color_modes").Any(mode => mode is "brightness" or "color_temp" or "hs" or "xy" or "rgb" or "rgbw" or "rgbww" or "white");

    public static double? Number(HomeAssistantState entity, string attribute) =>
        entity.Attributes.ValueKind == JsonValueKind.Object && entity.Attributes.TryGetProperty(attribute, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number : null;
}
