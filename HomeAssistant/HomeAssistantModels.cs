using System.Text.Json;

namespace LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

public sealed record HomeAssistantState(
    string EntityId,
    string State,
    JsonElement Attributes,
    DateTimeOffset LastChanged,
    DateTimeOffset LastUpdated)
{
    public string? RegistryIcon { get; init; }
    /// <summary>The domain part of <see cref="EntityId"/>, for example <c>light</c>.</summary>
    public string Domain
    {
        get
        {
            int dot = EntityId.IndexOf('.');
            return dot > 0 ? EntityId[..dot] : string.Empty;
        }
    }

    /// <summary>The <c>friendly_name</c> attribute, falling back to the entity id.</summary>
    public string FriendlyName
    {
        get
        {
            if (Attributes.ValueKind == JsonValueKind.Object &&
                Attributes.TryGetProperty("friendly_name", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } value)
                return value;
            return EntityId;
        }
    }
}

public sealed record HomeAssistantMenuEntity(string? AreaId, string? DeviceId, string? Name,
    string? Icon, bool Hidden, bool Disabled, bool Auxiliary);

public sealed record HomeAssistantMenuMetadata(
    IReadOnlyDictionary<string, HomeAssistantMenuEntity> Entities,
    IReadOnlyDictionary<string, string> DeviceAreas,
    IReadOnlyDictionary<string, string> AreaNames)
{
    public static HomeAssistantMenuMetadata Empty { get; } = new(
        new Dictionary<string, HomeAssistantMenuEntity>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>());
}

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
