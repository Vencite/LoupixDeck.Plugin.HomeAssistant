using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.Plugin.HomeAssistant.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

/// <summary>
/// Base for the per-entity commands. They are thin adapters: the entity id is the first parameter,
/// execution forwards to a service call and rendering reads the cached state of that same entity.
/// All of them share the stable parameters, so no command type or identifier is generated per
/// entity. Extra trailing parameters (<c>ShowIcon</c>, <c>Label</c>, <c>Icon</c>) are optional
/// per-button display overrides; bindings that only contain <c>EntityId</c> keep working unchanged.
/// </summary>
internal abstract class EntityCommand(HomeAssistantCommandAccess access) : IDisplayImageCommand
{
    public abstract CommandDescriptor Descriptor { get; }

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    /// <summary>The state is pushed through <see cref="EntityStore.EntityChanged"/>; the poll is a safety net.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public async Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length < 1 || !HomeAssistantIdentifiers.IsEntityId(ctx.Parameters[0]))
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} expects an entity_id as its first parameter such as light.office.");
            return;
        }

        HomeAssistantClient? client = access.Client;
        if (client is null)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName}: Home Assistant is not connected.");
            return;
        }

        string entityId = ctx.Parameters[0];
        try
        {
            await CallAsync(client, entityId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Descriptor.CommandName} failed for {entityId}.", ex);
        }
    }

    protected abstract Task CallAsync(HomeAssistantClient client, string entityId);

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        string entityId = ctx.Parameters.Length >= 1 ? ctx.Parameters[0] : string.Empty;
        return EntityButtonRenderer.Render(access.FindEntity(entityId), entityId,
            ButtonDisplayOptions.FromParameters(ctx.Parameters), canvas);
    }

    protected static CommandDescriptor Describe(string commandName, string displayName, string description) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = "Home Assistant",
        Description = description,
        ParameterTemplate = "({EntityId},{ShowIcon},{Label},{Icon})",
        Parameters =
        [
            new CommandParameter("EntityId", typeof(string)),
            new CommandParameter("ShowIcon", typeof(bool)) { DefaultValue = "True" },
            // A single space, not an empty default: the host pre-fills menu-built commands with
            // DefaultValue when set, but falls back to a type placeholder (literally "string")
            // when it is null. The space trims to empty when the command string is parsed back.
            new CommandParameter("Label", typeof(string)) { DefaultValue = " " },
            new CommandParameter("Icon", typeof(string)) { DefaultValue = " " }
        ],
        HiddenFromMenu = true
    };
}

internal sealed class ToggleEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.ToggleEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Toggle Entity", "Toggle a Home Assistant entity such as a light or a switch.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId) =>
        client.CallServiceAsync("homeassistant", "toggle", entityId);
}

internal sealed class TurnOnEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.TurnOnEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Turn On Entity", "Turn a Home Assistant entity on.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId) =>
        client.CallServiceAsync("homeassistant", "turn_on", entityId);
}

internal sealed class TurnOffEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.TurnOffEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Turn Off Entity", "Turn a Home Assistant entity off.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId) =>
        client.CallServiceAsync("homeassistant", "turn_off", entityId);
}

internal sealed class ActivateSceneCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.ActivateScene";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Activate Scene", "Activate a Home Assistant scene.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId) =>
        client.CallServiceAsync("scene", "turn_on", entityId);
}

internal sealed class RunScriptCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.RunScript";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Run Script", "Run a Home Assistant script.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId) =>
        client.CallServiceAsync("script", "turn_on", entityId);
}

internal sealed class PressButtonCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.PressButton";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Press Button", "Press a Home Assistant button entity.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId) =>
        client.CallServiceAsync("button", "press", entityId);
}
