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
        ["climate"] = "Climate",
        ["cover"] = "Covers",
        ["fan"] = "Fans",
        ["group"] = "Groups",
        ["humidifier"] = "Humidifiers",
        ["input_boolean"] = "Input booleans",
        ["light"] = "Lights",
        ["media_player"] = "Media players",
        ["scene"] = "Scenes",
        ["script"] = "Scripts",
        ["siren"] = "Sirens",
        ["switch"] = "Switches"
    };

    public static IReadOnlyList<MenuNode> Build(IReadOnlyList<HomeAssistantState> snapshot)
    {
        List<MenuNode> folders = [];
        foreach (IGrouping<string, HomeAssistantState> group in snapshot
                     .Where(state => Domains.ContainsKey(state.Domain))
                     .GroupBy(state => state.Domain)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            List<MenuNode> entities = group
                .OrderBy(state => state.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .Select(state => new MenuNode { Name = state.FriendlyName, Children = Actions(state) })
                .ToList();
            if (entities.Count > 0)
                folders.Add(new MenuNode { Name = Domains[group.Key], Children = entities });
        }
        return folders;
    }

    private static IReadOnlyList<MenuNode> Actions(HomeAssistantState entity) => entity.Domain switch
    {
        "scene" => [Leaf("Activate", ActivateSceneCommand.Name, entity.EntityId)],
        "script" => [Leaf("Run", RunScriptCommand.Name, entity.EntityId)],
        "button" => [Leaf("Press", PressButtonCommand.Name, entity.EntityId)],
        _ =>
        [
            Leaf("Toggle", ToggleEntityCommand.Name, entity.EntityId),
            Leaf("On", TurnOnEntityCommand.Name, entity.EntityId),
            Leaf("Off", TurnOffEntityCommand.Name, entity.EntityId)
        ]
    };

    private static MenuNode Leaf(string name, string commandName, string entityId) => new()
    {
        Name = name,
        CommandName = commandName,
        Parameters = new Dictionary<string, string> { ["EntityId"] = entityId }
    };
}
