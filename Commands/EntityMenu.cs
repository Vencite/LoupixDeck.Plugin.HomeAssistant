using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

/// <summary>
/// Builds the dynamic entity submenus from the current entity cache: one folder per controllable
/// domain, one subfolder per entity and one leaf per action. Each leaf bakes the entity id into the
/// shared <c>EntityId</c> parameter of a stable command, so no per-entity command is generated.
/// </summary>
internal static class EntityMenu
{
    private static readonly Dictionary<string, string> Domains = new(StringComparer.Ordinal)
    {
        ["automation"] = "Automations",
        ["button"] = "Buttons",
        ["binary_sensor"] = "Binary sensors",
        ["climate"] = "Climate",
        ["cover"] = "Covers",
        ["fan"] = "Fans",
        ["group"] = "Groups",
        ["humidifier"] = "Humidifiers",
        ["input_boolean"] = "Input booleans",
        ["light"] = "Lights",
        ["lock"] = "Locks",
        ["input_number"] = "Input numbers",
        ["media_player"] = "Media players",
        ["scene"] = "Scenes",
        ["sensor"] = "Sensors",
        ["script"] = "Scripts",
        ["siren"] = "Sirens",
        ["switch"] = "Switches"
    };

    public static IReadOnlyList<MenuNode> Build(IReadOnlyList<HomeAssistantState> snapshot,
        HomeAssistantMenuMetadata? metadata = null)
    {
        metadata ??= HomeAssistantMenuMetadata.Empty;
        HomeAssistantState[] visible = snapshot.Where(state => Domains.ContainsKey(state.Domain) &&
            (!metadata.Entities.TryGetValue(state.EntityId, out HomeAssistantMenuEntity? info) ||
             !(info.Hidden || info.Disabled || info.Auxiliary))).ToArray();

        if (metadata.AreaNames.Count == 0) return BuildDomains(visible, metadata);

        return visible.GroupBy(state => AreaName(state, metadata), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key == "Other" ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MenuNode { Name = group.Key, Children = BuildDomains(group, metadata) })
            .ToArray();
    }

    private static IReadOnlyList<MenuNode> BuildDomains(IEnumerable<HomeAssistantState> states,
        HomeAssistantMenuMetadata metadata)
    {
        List<MenuNode> folders = [];
        foreach (IGrouping<string, HomeAssistantState> group in states
                     .GroupBy(state => state.Domain)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            List<MenuNode> entities = group
                .OrderBy(state => EntityName(state, metadata), StringComparer.OrdinalIgnoreCase)
                .Select(state => new MenuNode { Name = EntityName(state, metadata),
                    Children = Actions(state, EntityName(state, metadata)) })
                .ToList();
            if (entities.Count > 0)
                folders.Add(new MenuNode { Name = Domains[group.Key], Children = entities });
        }
        return folders;
    }

    private static string EntityName(HomeAssistantState state, HomeAssistantMenuMetadata metadata) =>
        state.FriendlyName == state.EntityId && metadata.Entities.TryGetValue(state.EntityId, out var info) &&
        !string.IsNullOrWhiteSpace(info.Name) ? info.Name : state.FriendlyName;

    private static string AreaName(HomeAssistantState state, HomeAssistantMenuMetadata metadata)
    {
        if (!metadata.Entities.TryGetValue(state.EntityId, out var info)) return "Other";
        string? areaId = info.AreaId;
        if (areaId is null && info.DeviceId is not null)
            metadata.DeviceAreas.TryGetValue(info.DeviceId, out areaId);
        return areaId is not null && metadata.AreaNames.TryGetValue(areaId, out string? name) ? name : "Other";
    }

    internal static IReadOnlyList<MenuNode> Actions(HomeAssistantState entity, string name, bool includeFolder = true)
    {
        List<MenuNode> actions = [];
        void Add(string label, string service, object? data = null) => actions.Add(new MenuNode
        {
            Name = $"{name} · {label}", CommandName = EntityServiceCommand.Name,
            // Host 1.34 only preserves the first menu parameter. Carry the action there;
            // trailing parameters remain available as explicit editor overrides.
            Parameters = new Dictionary<string, string> { ["EntityId"] = entity.EntityId + "|" + service + "|" +
                (data is null ? "none" : Uri.EscapeDataString(JsonSerializer.Serialize(data))) }
        });
        void Feature(int flag, string label, string service, object? data = null)
        {
            if (EntityCapabilities.Has(entity, flag)) Add(label, service, data);
        }
        void Basic() => actions.AddRange([
            Leaf(entity, name, "Toggle", ToggleEntityCommand.Name),
            Leaf(entity, name, "On", TurnOnEntityCommand.Name),
            Leaf(entity, name, "Off", TurnOffEntityCommand.Name)]);
        switch (entity.Domain)
        {
            case "scene": actions.Add(Leaf(entity, name, "Activate", ActivateSceneCommand.Name)); break;
            case "script": actions.Add(Leaf(entity, name, "Run", RunScriptCommand.Name)); break;
            case "button": actions.Add(Leaf(entity, name, "Press", PressButtonCommand.Name)); break;
            case "light":
                Basic();
                if (EntityCapabilities.Brightness(entity))
                    foreach (int value in new[] { 25, 50, 75, 100 }) Add($"Brightness {value}%", "turn_on", new { brightness_pct = value });
                break;
            case "cover":
                Feature(1, "Open", "open_cover"); Feature(2, "Close", "close_cover"); Feature(8, "Stop", "stop_cover");
                Feature(4, "Position 50%", "set_cover_position", new { position = 50 }); break;
            case "climate":
                if ((EntityCapabilities.Has(entity, 1) && EntityCapabilities.Number(entity, "temperature") is not null) ||
                    (EntityCapabilities.Has(entity, 2) && EntityCapabilities.Number(entity, "target_temp_low") is not null &&
                     EntityCapabilities.Number(entity, "target_temp_high") is not null))
                {
                    actions.Add(Leaf(entity, name, "Temperature +", IncreaseTemperatureCommand.Name));
                    actions.Add(Leaf(entity, name, "Temperature −", DecreaseTemperatureCommand.Name));
                }
                Feature(256, "On", "turn_on"); Feature(128, "Off", "turn_off");
                foreach (string mode in EntityCapabilities.Strings(entity, "hvac_modes"))
                    if (mode != "off" || !EntityCapabilities.Has(entity, 128))
                        Add(mode, "set_hvac_mode", new { hvac_mode = mode });
                foreach (string mode in EntityCapabilities.Strings(entity, "swing_modes"))
                    Add($"Vertical swing: {mode}", "set_swing_mode", new { swing_mode = mode });
                foreach (string mode in EntityCapabilities.Strings(entity, "swing_horizontal_modes"))
                    Add($"Horizontal swing: {mode}", "set_swing_horizontal_mode", new { swing_horizontal_mode = mode });
                break;
            case "fan":
                Feature(32, "On", "turn_on"); Feature(16, "Off", "turn_off");
                Feature(1, "Speed 50%", "set_percentage", new { percentage = 50 }); break;
            case "media_player":
                Feature(128, "On", "turn_on"); Feature(256, "Off", "turn_off");
                Feature(16384, "Play", "media_play"); Feature(1, "Pause", "media_pause"); Feature(4096, "Stop", "media_stop");
                Feature(16, "Previous", "media_previous_track"); Feature(32, "Next", "media_next_track");
                Feature(4, "Volume 50%", "volume_set", new { volume_level = 0.5 }); break;
            case "lock": Add("Lock", "lock"); Add("Unlock", "unlock"); Feature(1, "Open", "open"); break;
            case "input_number": Add("Increase", "increment"); Add("Decrease", "decrement"); break;
            case "automation": case "group": case "humidifier": case "input_boolean": case "siren": case "switch": Basic(); break;
        }
        if (actions.Count == 0) actions.Add(Leaf(entity, name, "Show state", ShowEntityCommand.Name));
        if (includeFolder && actions.Count > 1)
            actions.Insert(0, Leaf(entity, name, "Controls folder", OpenEntityControlsCommand.Name));
        return actions;
    }

    private static MenuNode Leaf(HomeAssistantState entity, string name, string action, string commandName) => new()
    {
        Name = $"{name} · {action}",
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { ["EntityId"] = entity.EntityId }
    };
}
