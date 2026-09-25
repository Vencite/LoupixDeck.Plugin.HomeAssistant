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

    public virtual async Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length < 1 || !HomeAssistantIdentifiers.IsEntityId(ctx.Parameters[0]))
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} expects an entity_id as its first parameter such as light.office.");
            return;
        }

        HomeAssistantClient? client = access.Client;
        if (client is null || client.State != HomeAssistantConnectionState.Connected)
        {
            return;
        }

        string entityId = ctx.Parameters[0];
        try
        {
            await CallAsync(client, entityId, ctx.Parameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} failed ({ex.GetType().Name}).");
        }
    }

    protected abstract Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters);

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        string entityId = ctx.Parameters.Length >= 1 ? ctx.Parameters[0] : string.Empty;
        return EntityButtonRenderer.Render(access.FindEntity(entityId), entityId,
            ButtonDisplayOptions.FromParameters(ctx.Parameters.Take(6).ToArray()), canvas, access.Status);
    }

    protected static CommandDescriptor Describe(string commandName, string displayName, string description,
        params CommandParameter[] extra) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = "Home Assistant",
        Description = description,
        ParameterTemplate = "({EntityId},{ShowIcon},{Label},{Icon},{StateSize},{LabelSize}" +
            string.Concat(extra.Select(parameter => ",{" + parameter.Name + "}")) + ")",
        Parameters =
        [
            new CommandParameter("EntityId", typeof(string)),
            new CommandParameter("ShowIcon", typeof(bool)) { DefaultValue = "True" },
            // Non-empty defaults keep later positional values aligned when the host reopens
            // the editor; its command parser discards empty pieces.
            new CommandParameter("Label", typeof(string)) { DefaultValue = "auto" },
            new CommandParameter("Icon", typeof(string)) { DefaultValue = "auto" },
            new CommandParameter("StateSize", typeof(string)) { DefaultValue = "11" },
            new CommandParameter("LabelSize", typeof(string)) { DefaultValue = "13" },
            ..extra
        ],
        HiddenFromMenu = true
    };
}

internal sealed class ToggleEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.ToggleEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Toggle Entity", "Toggle a Home Assistant entity such as a light or a switch.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) =>
        client.CallServiceAsync("homeassistant", "toggle", entityId);
}

internal sealed class TurnOnEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.TurnOnEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Turn On Entity", "Turn a Home Assistant entity on.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) =>
        client.CallServiceAsync("homeassistant", "turn_on", entityId);
}

internal sealed class TurnOffEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.TurnOffEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Turn Off Entity", "Turn a Home Assistant entity off.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) =>
        client.CallServiceAsync("homeassistant", "turn_off", entityId);
}

internal sealed class ActivateSceneCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.ActivateScene";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Activate Scene", "Activate a Home Assistant scene.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) =>
        client.CallServiceAsync("scene", "turn_on", entityId);
}

internal sealed class RunScriptCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.RunScript";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Run Script", "Run a Home Assistant script.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) =>
        client.CallServiceAsync("script", "turn_on", entityId);
}

internal sealed class PressButtonCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.PressButton";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Press Button", "Press a Home Assistant button entity.");

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) =>
        client.CallServiceAsync("button", "press", entityId);
}

internal sealed class ShowEntityCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.ShowEntity";

    public override CommandDescriptor Descriptor { get; } =
        Describe(Name, "Show Entity", "Display a Home Assistant entity state without controlling it.");

    public override Task Execute(CommandContext ctx) => Task.CompletedTask;

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters) => Task.CompletedTask;
}

internal sealed class EntityServiceCommand(HomeAssistantCommandAccess access) : EntityCommand(access)
{
    public const string Name = "HomeAssistant.EntityService";
    public override CommandDescriptor Descriptor { get; } = Describe(Name, "Entity action",
        "Call a domain service for one entity, with live state and display options.",
        new CommandParameter("Service", typeof(string)),
        new CommandParameter("ServiceData", typeof(string)) { DefaultValue = "none" });

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters)
    {
        if (parameters.Length != 8) throw new ArgumentException("Entity action requires a service and data.");
        var data = parameters[7] == "none" ? (System.Text.Json.JsonElement?)null :
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(Uri.UnescapeDataString(parameters[7]));
        return client.CallServiceAsync(entityId[..entityId.IndexOf('.')], parameters[6],
            System.Text.Json.JsonSerializer.SerializeToElement(new { entity_id = entityId }), data);
    }
}
