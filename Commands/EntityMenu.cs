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

    private static IReadOnlyList<MenuNode> Actions(HomeAssistantState entity, string name) => entity.Domain switch
    {
        "sensor" or "binary_sensor" => [Leaf(entity, name, "Show state", ShowEntityCommand.Name)],
        "scene" => [Leaf(entity, name, "Activate", ActivateSceneCommand.Name)],
        "script" => [Leaf(entity, name, "Run", RunScriptCommand.Name)],
        "button" => [Leaf(entity, name, "Press", PressButtonCommand.Name)],
        _ =>
        [
            Leaf(entity, name, "Toggle", ToggleEntityCommand.Name),
            Leaf(entity, name, "On", TurnOnEntityCommand.Name),
            Leaf(entity, name, "Off", TurnOffEntityCommand.Name)
        ]
    };

    private static MenuNode Leaf(HomeAssistantState entity, string name, string action, string commandName) => new()
    {
        Name = $"{name} · {action}",
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { ["EntityId"] = entity.EntityId }
    };
}
