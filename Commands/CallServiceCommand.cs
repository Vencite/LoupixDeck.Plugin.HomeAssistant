using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

/// <summary>
/// The escape hatch for services the dedicated commands do not cover. It is the only place where a
/// domain and a service are user input, so both are validated before anything is sent.
/// </summary>
internal sealed class CallServiceCommand(HomeAssistantCommandAccess access) : IPluginCommand
{
    public const string Name = "HomeAssistant.CallService";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Call Service",
        Group = "Home Assistant",
        Description = "Call any Home Assistant service, with optional targets and service data (URI-escaped JSON).",
        ParameterTemplate = "({Domain},{Service},{EntityId},{ServiceData},{Target})",
        Parameters =
        [
            new CommandParameter("Domain", typeof(string)),
            new CommandParameter("Service", typeof(string)),
            new CommandParameter("EntityId", typeof(string)) { DefaultValue = "none" },
            new CommandParameter("ServiceData", typeof(string)) { DefaultValue = "none" },
            new CommandParameter("Target", typeof(string)) { DefaultValue = "none" }
        ],
        HiddenFromMenu = false
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length is < 2 or > 5)
        {
            ctx.Host.Logger.Warn($"{Name} expects a domain, a service and an optional entity_id.");
            return;
        }

        string domain = ctx.Parameters[0];
        string service = ctx.Parameters[1];
        string? entityId = ctx.Parameters.Length >= 3 && ctx.Parameters[2] is not ("" or "none") ? ctx.Parameters[2] : null;
        if (!HomeAssistantIdentifiers.IsServiceToken(domain) ||
            !HomeAssistantIdentifiers.IsServiceToken(service) ||
            (entityId is not null && !HomeAssistantIdentifiers.IsEntityId(entityId)))
        {
            ctx.Host.Logger.Warn($"{Name}: invalid domain, service or entity_id.");
            return;
        }

        HomeAssistantClient? client = access.Client;
        if (client is null || client.State != HomeAssistantConnectionState.Connected)
        {
            return;
        }

        try
        {
            JsonElement? data = Decode(ctx.Parameters.ElementAtOrDefault(3));
            JsonElement? target = Decode(ctx.Parameters.ElementAtOrDefault(4));
            if (entityId is not null)
            {
                if (target is not null) throw new ArgumentException("Use either EntityId or Target.");
                target = JsonSerializer.SerializeToElement(new { entity_id = entityId });
            }
            await client.CallServiceAsync(domain, service, target, data).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Warn($"{Name} failed ({ex.GetType().Name}); check the service and parameters.");
        }
    }
    private static JsonElement? Decode(string? value) => value is null or "" or "none"
        ? null : JsonSerializer.Deserialize<JsonElement>(Uri.UnescapeDataString(value));
}
