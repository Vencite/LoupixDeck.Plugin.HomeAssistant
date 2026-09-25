using System.Text.Json;

namespace LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

public sealed record HomeAssistantState(
    string EntityId,
    string State,
    JsonElement Attributes,
    DateTimeOffset LastChanged,
    DateTimeOffset LastUpdated);

public sealed record HomeAssistantStateChangedEvent(
    string EntityId,
    HomeAssistantState? OldState,
    HomeAssistantState? NewState,
    DateTimeOffset TimeFired);

public enum HomeAssistantConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public sealed class HomeAssistantAuthenticationException()
    : Exception("Home Assistant rejected the access token.");

public sealed class HomeAssistantRequestException(string code, string message)
    : Exception($"Home Assistant request failed ({code}): {message}")
{
    public string Code { get; } = code;
}
