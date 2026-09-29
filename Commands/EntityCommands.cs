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
    protected readonly HomeAssistantCommandAccess Access = access;
    public abstract CommandDescriptor Descriptor { get; }

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    /// <summary>The state is pushed through <see cref="EntityStore.EntityChanged"/>; the poll is a safety net.</summary>
    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(5);

    public virtual async Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length < 1 || !HomeAssistantIdentifiers.IsEntityId(GetEntityId(ctx.Parameters)))
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} expects an entity_id as its first parameter such as light.office.");
            return;
        }

        HomeAssistantClient? client = Access.Client;
        if (client is null || client.State != HomeAssistantConnectionState.Connected)
        {
            return;
        }

        string entityId = GetEntityId(ctx.Parameters);
        try
        {
            await CallAsync(client, entityId, ctx.Parameters).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Descriptor.CommandName} failed ({ex.GetType().Name}).");
        }
    }

    protected virtual string GetEntityId(string[] parameters) => parameters.FirstOrDefault() ?? string.Empty;

    protected abstract Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters);

    protected virtual string? DefaultLabel => null;

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        string entityId = GetEntityId(ctx.Parameters);
        ButtonDisplayOptions options = ButtonDisplayOptions.FromParameters(ctx.Parameters.Take(6).ToArray());
        if (ctx.Parameters.Length == Descriptor.Parameters.Count)
        {
            if (bool.TryParse(ctx.Parameters[^4], out bool showLabel)) options = options with { ShowLabel = showLabel };
            if (bool.TryParse(ctx.Parameters[^3], out bool showState)) options = options with { ShowState = showState };
            if (Enum.TryParse(ctx.Parameters[^2], true, out ButtonLayout layout)) options = options with { Layout = layout };
            options = options with { IconColor = ButtonDisplayOptions.ParseIconColor(ctx.Parameters[^1]) };
        }
        if (options.Label is null && DefaultLabel is not null) options = options with { Label = DefaultLabel };
        return EntityButtonRenderer.Render(Access.FindEntity(entityId), entityId, options, canvas, Access.Status);
    }

    protected static CommandDescriptor Describe(string commandName, string displayName, string description,
        params CommandParameter[] extra) => new()
    {
        CommandName = commandName,
        DisplayName = displayName,
        Group = "Home Assistant",
        Description = description,
        ParameterTemplate = "({EntityId},{ShowIcon},{Label},{Icon},{StateSize (8-18)},{LabelSize (8-18)}" +
            string.Concat(extra.Select(parameter => ",{" + parameter.Name + "}")) +
            ",{ShowLabel},{ShowState},{Layout},{IconColor (#RRGGBB)})",
        Parameters =
        [
            new CommandParameter("EntityId", typeof(string)),
            new CommandParameter("ShowIcon", typeof(bool)) { DefaultValue = "True" },
            // Non-empty defaults keep later positional values aligned when the host reopens
            // the editor; its command parser discards empty pieces.
            new CommandParameter("Label", typeof(string)) { DefaultValue = "auto" },
            new CommandParameter("Icon", typeof(string)) { DefaultValue = "auto" },
            new CommandParameter("StateSize (8-18)", typeof(string)) { DefaultValue = "11" },
            new CommandParameter("LabelSize (8-18)", typeof(string)) { DefaultValue = "13" },
            ..extra,
            new CommandParameter("ShowLabel", typeof(bool)) { DefaultValue = "True" },
            new CommandParameter("ShowState", typeof(bool)) { DefaultValue = "True" },
            new CommandParameter("Layout", typeof(ButtonLayout)) { DefaultValue = "Auto" },
            new CommandParameter("IconColor (#RRGGBB)", typeof(string)) { DefaultValue = "auto" }
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
        new CommandParameter("Service", typeof(string)) { DefaultValue = "auto" },
        new CommandParameter("ServiceData", typeof(string)) { DefaultValue = "none" });

    // Saved bindings from older hosts pack entity, service and data into the first parameter.
    protected override string GetEntityId(string[] parameters) => base.GetEntityId(parameters).Split('|', 2)[0];

    protected override Task CallAsync(HomeAssistantClient client, string entityId, string[] parameters)
    {
        string[] action = parameters[0].Split('|', 3);
        string service = parameters.ElementAtOrDefault(6) ?? "auto";
        string payload = parameters.ElementAtOrDefault(7) ?? "none";
        if (service == "auto" && action.Length == 3)
        {
            service = action[1];
            payload = action[2];
        }
        var data = payload == "none" ? (System.Text.Json.JsonElement?)null :
            System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(Uri.UnescapeDataString(payload));
        return client.CallServiceAsync(entityId[..entityId.IndexOf('.')], service,
            System.Text.Json.JsonSerializer.SerializeToElement(new { entity_id = entityId }), data);
    }
}
