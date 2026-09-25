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
        Description = "Call any Home Assistant service, optionally targeting one entity.",
        ParameterTemplate = "({Domain},{Service},{EntityId})",
        Parameters =
        [
            new CommandParameter("Domain", typeof(string)),
            new CommandParameter("Service", typeof(string)),
            new CommandParameter("EntityId", typeof(string)) { DefaultValue = string.Empty }
        ],
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.All;

    public async Task Execute(CommandContext ctx)
    {
        if (ctx.Parameters.Length is < 2 or > 3)
        {
            ctx.Host.Logger.Warn($"{Name} expects a domain, a service and an optional entity_id.");
            return;
        }

        string domain = ctx.Parameters[0];
        string service = ctx.Parameters[1];
        string? entityId = ctx.Parameters.Length == 3 && ctx.Parameters[2].Length > 0 ? ctx.Parameters[2] : null;
        if (!HomeAssistantIdentifiers.IsServiceToken(domain) ||
            !HomeAssistantIdentifiers.IsServiceToken(service) ||
            (entityId is not null && !HomeAssistantIdentifiers.IsEntityId(entityId)))
        {
            ctx.Host.Logger.Warn($"{Name}: invalid domain, service or entity_id.");
            return;
        }

        HomeAssistantClient? client = access.Client;
        if (client is null)
        {
            ctx.Host.Logger.Warn($"{Name}: Home Assistant is not connected.");
            return;
        }

        try
        {
            await client.CallServiceAsync(domain, service, entityId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error($"{Name} failed for {domain}.{service}.", ex);
        }
    }
}
