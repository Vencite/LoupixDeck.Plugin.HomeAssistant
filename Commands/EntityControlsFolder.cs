using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

internal sealed class OpenEntityControlsCommand(HomeAssistantCommandAccess access,
    Func<IEnumerable<IPluginCommand>> commands) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.OpenEntityControls";
    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Open entity controls", "Open live controls for this entity.");
    protected override string? DefaultLabel => "Controls";
    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) => Task.CompletedTask;
    public override Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length == 0 || !HomeAssistantIdentifiers.IsEntityId(ctx.Parameters[0])) return Task.CompletedTask;
        ctx.Host.OpenFolder(new EntityControlsFolder(ctx.Host, Access, commands, ctx.Parameters[0]));
        return Task.CompletedTask;
    }
}

internal sealed class EntityControlsFolder(IPluginHost host, HomeAssistantCommandAccess access,
    Func<IEnumerable<IPluginCommand>> commands, string entityId, string? modeService = null) : IFolderProvider
{
    private int _page;
    public string Title => modeService switch
    {
        "set_hvac_mode" => "HVAC mode",
        "set_swing_mode" => "Vertical swing",
        "set_swing_horizontal_mode" => "Horizontal swing",
        _ => access.FindEntity(entityId)?.FriendlyName ?? entityId
    };
    public event Action EntriesChanged = delegate { };
    public IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => new Dictionary<int, RotaryOverride>();
    public void OnEnter() => access.Changed += Refresh;
    public void OnExit() => access.Changed -= Refresh;
    private void Refresh() => EntriesChanged();

    public IReadOnlyList<FolderEntry> BuildEntries()
    {
        HomeAssistantState? entity = access.FindEntity(entityId);
        if (entity is null) return [new FolderEntry { SlotIndex = 0, Text = "No state", TextSize = 12,
            Image = FolderIcons.Get("status", "No state", 12), TextColor = PluginColor.Transparent }];
        string label = entity.Domain == "climate" && EntityCapabilities.Number(entity, "temperature") is { } temp
            ? $"{entity.State} · {temp:0.#}°" : entity.State;
        string? connectionStatus = access.Status;
        if (connectionStatus is { } status) label = status;
        IReadOnlyList<MenuNode> all = EntityMenu.Actions(entity, entity.FriendlyName, includeFolder: false);
        string[] modeServices = ["set_hvac_mode", "set_swing_mode", "set_swing_horizontal_mode"];
        List<MenuNode> actions = modeService is null ? all.ToList() : all.Where(node =>
            node.Parameters.TryGetValue("EntityId", out string? value) &&
            value.Contains($"|{modeService}|", StringComparison.Ordinal)).ToList();
        if (modeService is null)
            foreach (string service in modeServices)
            {
                MenuNode[] modes = actions.Where(node => node.Parameters.TryGetValue("EntityId", out string? value) &&
                    value.Contains($"|{service}|", StringComparison.Ordinal)).ToArray();
                if (modes.Length == 0) continue;
                actions.RemoveAll(node => modes.Contains(node));
                actions.Insert(0, new MenuNode { Name = service, Children = modes });
            }
        int capacity = Math.Max(1, host.FolderGrid.TotalSlots - 4);
        int maxPage = actions.Count == 0 ? 0 : (actions.Count - 1) / capacity;
        _page = Math.Min(_page, maxPage);
        List<FolderEntry> entries = [new() { SlotIndex = host.FolderGrid.SlotForIndex(0),
            Text = ShortLabel(entity.State == "off" && connectionStatus is null ? "State off" : label), TextSize = 11,
            TextColor = PluginColor.Transparent,
            Image = FolderIcons.Get(connectionStatus is null ? "status" : "offline",
                ShortLabel(entity.State == "off" && connectionStatus is null ? "State off" : label), 11) }];
        int index = 1;
        foreach (MenuNode node in actions.Skip(_page * capacity).Take(capacity))
        {
            int slot = host.FolderGrid.SlotForIndex(index++);
            if (slot < 0) break;
            if (node.Children.Count > 0)
            {
                string title = node.Name switch
                {
                    "set_hvac_mode" => "HVAC mode",
                    "set_swing_mode" => "Swing ↕",
                    _ => "Swing ↔"
                };
                entries.Add(new FolderEntry { SlotIndex = slot, Text = title, TextSize = 12,
                    TextColor = PluginColor.Transparent, Image = FolderIcons.Get("folder", title),
                    OpensFolder = new EntityControlsFolder(host, access, commands, entityId, node.Name) });
            }
            else
            {
                string text = node.Name[(node.Name.LastIndexOf('·') + 1)..].Trim();
                if (modeService is "set_swing_mode" or "set_swing_horizontal_mode")
                    text = text[(text.IndexOf(':') + 1)..].Trim();
                string caption = ShortLabel(text);
                int textSize = caption.Length > 8 ? 11 : 12;
                entries.Add(new FolderEntry { SlotIndex = slot, Text = caption, TextSize = textSize,
                    TextColor = PluginColor.Transparent, Image = FolderIcons.Get(IconFor(node, text), caption, textSize),
                    OnPress = () => Execute(node) });
            }
        }
        if (_page > 0) entries.Add(new FolderEntry { SlotIndex = host.FolderGrid.SlotForIndex(index++), Text = "Prev", TextSize = 12, TextColor = PluginColor.Transparent, Image = FolderIcons.Get("left", "Prev"),
            OnPress = () => { _page--; Refresh(); return Task.CompletedTask; } });
        if (_page < maxPage) entries.Add(new FolderEntry { SlotIndex = host.FolderGrid.SlotForIndex(index), Text = "Next", TextSize = 12, TextColor = PluginColor.Transparent, Image = FolderIcons.Get("right", "Next"),
            OnPress = () => { _page++; Refresh(); return Task.CompletedTask; } });
        return entries;
    }

    private static string ShortLabel(string value)
    {
        if (value.StartsWith("Brightness ", StringComparison.Ordinal) ||
            value.StartsWith("Position ", StringComparison.Ordinal) ||
            value.StartsWith("Volume ", StringComparison.Ordinal) ||
            value.StartsWith("Speed ", StringComparison.Ordinal)) value = value[(value.IndexOf(' ') + 1)..];
        value = value switch { "Temperature +" => "Temp +", "Temperature −" => "Temp −",
            "Increase" => "More", "Decrease" => "Less", "Unavailable" => "Unavailable", _ => value };
        return value.Length <= 10 ? value : value[..9] + "…";
    }

    private static string IconFor(MenuNode node, string action) => node.CommandName switch
    {
        IncreaseTemperatureCommand.Name => "up",
        DecreaseTemperatureCommand.Name => "down",
        ToggleEntityCommand.Name => "power",
        TurnOnEntityCommand.Name => "on",
        TurnOffEntityCommand.Name => "off",
        ActivateSceneCommand.Name or RunScriptCommand.Name => "play",
        PressButtonCommand.Name => "press",
        _ => action switch
        {
            "On" => "on", "Off" => "off", "Toggle" => "power",
            "Open" => "up", "Close" => "down", "Stop" => "stop",
            "Lock" => "lock", "Unlock" => "unlock", "Press" => "press",
            "Play" => "play", "Pause" => "pause", "Previous" => "prev", "Next" => "next",
            "Increase" => "up", "Decrease" => "down",
            "heat" => "heat", "cool" => "cool", "off" => "off",
            "auto" => "auto", "fan_only" => "fan", "dry" => "dry",
            _ when action.StartsWith("Brightness", StringComparison.Ordinal) => "brightness",
            _ when action.StartsWith("Volume", StringComparison.Ordinal) => "value",
            _ when action.StartsWith("Position", StringComparison.Ordinal) => "value",
            _ when action.StartsWith("Speed", StringComparison.Ordinal) => "value",
            _ => "mode"
        }
    };

    private Task Execute(MenuNode node)
    {
        IPluginCommand? command = commands().FirstOrDefault(item => item.Descriptor.CommandName == node.CommandName);
        if (command is null) return Task.CompletedTask;
        string[] parameters = command.Descriptor.Parameters.Select(parameter =>
            node.Parameters.TryGetValue(parameter.Name, out string? value) ? value : parameter.DefaultValue ?? "").ToArray();
        return command.Execute(new CommandContext { Parameters = parameters, Target = ButtonTargets.TouchButton, Host = host });
    }
}
