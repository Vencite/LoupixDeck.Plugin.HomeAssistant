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
    Func<IEnumerable<IPluginCommand>> commands, string entityId, bool modesOnly = false) : IFolderProvider
{
    private int _page;
    public string Title => modesOnly ? "HVAC mode" : access.FindEntity(entityId)?.FriendlyName ?? entityId;
    public event Action EntriesChanged = delegate { };
    public IReadOnlyDictionary<int, RotaryOverride> RotaryOverrides => new Dictionary<int, RotaryOverride>();
    public void OnEnter() => access.Changed += Refresh;
    public void OnExit() => access.Changed -= Refresh;
    private void Refresh() => EntriesChanged();

    public IReadOnlyList<FolderEntry> BuildEntries()
    {
        HomeAssistantState? entity = access.FindEntity(entityId);
        if (entity is null) return [new FolderEntry { SlotIndex = 0, Text = "No state" }];
        string label = entity.Domain == "climate" && EntityCapabilities.Number(entity, "temperature") is { } temp
            ? $"{entity.State} · {temp:0.#}°" : entity.State;
        if (access.Status is { } status) label = status;
        IReadOnlyList<MenuNode> all = EntityMenu.Actions(entity, entity.FriendlyName, includeFolder: false);
        MenuNode[] modes = all.Where(node => node.Parameters.TryGetValue("EntityId", out string? value) &&
            value.Contains("|set_hvac_mode|", StringComparison.Ordinal)).ToArray();
        List<MenuNode> actions = modesOnly ? modes.ToList() : all.Except(modes).ToList();
        if (!modesOnly && modes.Length > 0)
            actions.Insert(0, new MenuNode { Name = "HVAC mode", Children = modes });
        int capacity = Math.Max(1, host.FolderGrid.TotalSlots - 4);
        int maxPage = actions.Count == 0 ? 0 : (actions.Count - 1) / capacity;
        _page = Math.Min(_page, maxPage);
        List<FolderEntry> entries = [new() { SlotIndex = host.FolderGrid.SlotForIndex(0), Text = label }];
        int index = 1;
        foreach (MenuNode node in actions.Skip(_page * capacity).Take(capacity))
        {
            int slot = host.FolderGrid.SlotForIndex(index++);
            if (slot < 0) break;
            if (node.Children.Count > 0)
                entries.Add(new FolderEntry { SlotIndex = slot, Text = node.Name,
                    OpensFolder = new EntityControlsFolder(host, access, commands, entityId, modesOnly: true) });
            else
            {
                string text = node.Name[(node.Name.LastIndexOf('·') + 1)..].Trim();
                entries.Add(new FolderEntry { SlotIndex = slot, Text = text, OnPress = () => Execute(node) });
            }
        }
        if (_page > 0) entries.Add(new FolderEntry { SlotIndex = host.FolderGrid.SlotForIndex(index++), Text = "Previous",
            OnPress = () => { _page--; Refresh(); return Task.CompletedTask; } });
        if (_page < maxPage) entries.Add(new FolderEntry { SlotIndex = host.FolderGrid.SlotForIndex(index), Text = "Next",
            OnPress = () => { _page++; Refresh(); return Task.CompletedTask; } });
        return entries;
    }

    private Task Execute(MenuNode node)
    {
        IPluginCommand? command = commands().FirstOrDefault(item => item.Descriptor.CommandName == node.CommandName);
        if (command is null) return Task.CompletedTask;
        string[] parameters = command.Descriptor.Parameters.Select(parameter =>
            node.Parameters.TryGetValue(parameter.Name, out string? value) ? value : parameter.DefaultValue ?? "").ToArray();
        return command.Execute(new CommandContext { Parameters = parameters, Target = ButtonTargets.TouchButton, Host = host });
    }
}
