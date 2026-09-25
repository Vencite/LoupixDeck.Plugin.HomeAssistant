using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

namespace LoupixDeck.Plugin.HomeAssistant.Commands;

/// <summary>
/// Live access to the plugin's current runtime, so commands always act on the client and the entity
/// cache of the active session instead of a captured, possibly already replaced, instance.
/// </summary>
internal sealed class HomeAssistantCommandAccess(
    Func<HomeAssistantClient?> client,
    Func<string, HomeAssistantState?> entity,
    Func<string?>? status = null)
{
    public string? Status => status?.Invoke();

    public HomeAssistantClient? Client => client();

    public HomeAssistantState? FindEntity(string entityId) => entity(entityId);
}
