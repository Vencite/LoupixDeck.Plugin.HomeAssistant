using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

using var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
var firstRequestSeen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseLateResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var secondSubscriptionSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var pendingBeforeDisconnectSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var client = new HomeAssistantClient(new SilentLogger());
var firstEvent = new TaskCompletionSource<HomeAssistantStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
var secondEvent = new TaskCompletionSource<HomeAssistantStateChangedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
client.StateChanged += (_, change) =>
{
    if (change.NewState is not null) firstEvent.TrySetResult(change);
    else secondEvent.TrySetResult(change);
};
client.StateChanged += (_, _) => throw new InvalidOperationException("Synthetic callback failure.");
Task server = ServeAsync();

await client.ConnectAsync($"http://127.0.0.1:{port}/", "test-token", timeout.Token);
Check(client.State == HomeAssistantConnectionState.Connected, "initial connection");
using (var cancelled = new CancellationTokenSource())
{
    Task<IReadOnlyList<HomeAssistantState>> request = client.GetStatesAsync(cancelled.Token);
    await firstRequestSeen.Task.WaitAsync(timeout.Token);
    cancelled.Cancel();
    await ThrowsAsync<OperationCanceledException>(request);
}
releaseLateResult.SetResult();
IReadOnlyList<HomeAssistantState> states = await client.GetStatesAsync(timeout.Token);
Check(states.Count == 1 && states[0].EntityId == "sensor.test" &&
      states[0].Attributes.GetProperty("unit_of_measurement").GetString() == "C", "state parsing and owned attributes");
try
{
    await client.GetStatesAsync(timeout.Token);
    throw new Exception("Expected a Home Assistant request error.");
}
catch (HomeAssistantRequestException ex)
{
    Check(ex.Code == "synthetic_error" && ex.Message.Contains("Synthetic failure"), "request error correlation");
}
await client.SubscribeStateChangesAsync(timeout.Token);
HomeAssistantStateChangedEvent added = await firstEvent.Task.WaitAsync(timeout.Token);
Check(added.OldState is null && added.NewState?.State == "on", "added entity event");
await secondSubscriptionSeen.Task.WaitAsync(timeout.Token);
HomeAssistantStateChangedEvent removed = await secondEvent.Task.WaitAsync(timeout.Token);
Check(removed.OldState?.State == "on" && removed.NewState is null, "restored subscription and removed entity event");
await ThrowsAsync<ArgumentException>(client.CallServiceAsync("Light", "toggle", "light.office"));
await ThrowsAsync<ArgumentException>(client.CallServiceAsync("light", "turn on", "light.office"));
await ThrowsAsync<ArgumentException>(client.CallServiceAsync("light", "turn_on", "Light.Office"));
await client.CallServiceAsync("homeassistant", "toggle", "light.office", timeout.Token);
await client.CallServiceAsync("script", "turn_on", null, timeout.Token);
Task<IReadOnlyList<HomeAssistantState>> interrupted = client.GetStatesAsync(timeout.Token);
await pendingBeforeDisconnectSeen.Task.WaitAsync(timeout.Token);
await client.DisconnectAsync();
await ThrowsAsync<IOException>(interrupted);
Check(client.State == HomeAssistantConnectionState.Disconnected, "manual disconnect");
await ThrowsAsync<HomeAssistantAuthenticationException>(
    client.ConnectAsync($"ws://127.0.0.1:{port}/api/websocket", "bad-token", timeout.Token));
Check(client.State == HomeAssistantConnectionState.Disconnected, "invalid auth stopped");
await client.ConnectAsync($"http://127.0.0.1:{port}/api/websocket/", "test-token", timeout.Token);
await client.DisconnectAsync();
await client.DisposeAsync();
await server.WaitAsync(timeout.Token);
Console.WriteLine("HomeAssistantClient smoke check passed.");
await EntityStoreSmoke.RunAsync();
await PluginLifecycleSmoke.RunAsync();
await CommandSmoke.RunAsync();

async Task ServeAsync()
{
    using (WebSocket socket = await AcceptAsync())
    {
        await AuthAsync(socket, fragmented: true);
        using JsonDocument cancelledRequest = await ReadAsync(socket);
        Check(cancelledRequest.RootElement.GetProperty("type").GetString() == "get_states", "first request");
        int oldId = cancelledRequest.RootElement.GetProperty("id").GetInt32();
        firstRequestSeen.SetResult(oldId);
        await releaseLateResult.Task.WaitAsync(timeout.Token);
        await SendAsync(socket, new { id = oldId, type = "result", success = true, result = Array.Empty<object>() });

        using JsonDocument stateRequest = await ReadAsync(socket);
        int stateId = stateRequest.RootElement.GetProperty("id").GetInt32();
        Check(stateId != oldId, "unique request IDs");
        await SendAsync(socket, new { id = stateId, type = "result", success = true, result = new[] { State("on") } }, fragmented: true);

        using JsonDocument failedRequest = await ReadAsync(socket);
        int failedId = failedRequest.RootElement.GetProperty("id").GetInt32();
        await SendAsync(socket, new { id = failedId, type = "result", success = false,
            error = new { code = "synthetic_error", message = "Synthetic failure" } });

        using JsonDocument subscription = await ReadAsync(socket);
        Check(subscription.RootElement.GetProperty("type").GetString() == "subscribe_events", "subscription type");
        Check(subscription.RootElement.GetProperty("event_type").GetString() == "state_changed", "subscription filter");
        int subscriptionId = subscription.RootElement.GetProperty("id").GetInt32();
        await SendAsync(socket, new { id = subscriptionId, type = "result", success = true, result = (object?)null });
        await SendAsync(socket, new { id = subscriptionId, type = "event", @event = new
        {
            event_type = "state_changed", time_fired = "2024-01-01T00:00:01+00:00",
            data = new { entity_id = "sensor.test", old_state = (object?)null, new_state = State("on") }
        } });
        await firstEvent.Task.WaitAsync(timeout.Token);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
    }

    using (WebSocket socket = await AcceptAsync())
    {
        await AuthAsync(socket);
        using JsonDocument subscription = await ReadAsync(socket);
        Check(subscription.RootElement.GetProperty("type").GetString() == "subscribe_events", "restored subscription");
        int subscriptionId = subscription.RootElement.GetProperty("id").GetInt32();
        await SendAsync(socket, new { id = subscriptionId, type = "result", success = true, result = (object?)null });
        secondSubscriptionSeen.SetResult();
        await SendAsync(socket, new { id = subscriptionId, type = "event", @event = new
        {
            event_type = "state_changed", time_fired = "2024-01-01T00:00:02+00:00",
            data = new { entity_id = "sensor.test", old_state = State("on"), new_state = (object?)null }
        } });
        await secondEvent.Task.WaitAsync(timeout.Token);
        using (JsonDocument serviceCall = await ReadAsync(socket))
        {
            Check(serviceCall.RootElement.GetProperty("type").GetString() == "call_service", "service call type");
            Check(serviceCall.RootElement.GetProperty("domain").GetString() == "homeassistant", "service call domain");
            Check(serviceCall.RootElement.GetProperty("service").GetString() == "toggle", "service call service");
            Check(serviceCall.RootElement.GetProperty("target").GetProperty("entity_id").GetString() == "light.office",
                "service call target");
            await SendAsync(socket, new
            {
                id = serviceCall.RootElement.GetProperty("id").GetInt32(), type = "result", success = true,
                result = (object?)null
            });
        }
        using (JsonDocument serviceCall = await ReadAsync(socket))
        {
            Check(serviceCall.RootElement.GetProperty("type").GetString() == "call_service", "untargeted service call type");
            Check(!serviceCall.RootElement.TryGetProperty("target", out _), "untargeted service call has no target");
            await SendAsync(socket, new
            {
                id = serviceCall.RootElement.GetProperty("id").GetInt32(), type = "result", success = true,
                result = (object?)null
            });
        }
        using JsonDocument unfinished = await ReadAsync(socket);
        Check(unfinished.RootElement.GetProperty("type").GetString() == "get_states", "pending before disconnect");
        pendingBeforeDisconnectSeen.SetResult();
        byte[] buffer = new byte[64];
        ValueWebSocketReceiveResult close = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Check(close.MessageType == WebSocketMessageType.Close, "manual close");
    }

    using (WebSocket socket = await AcceptAsync())
    {
        await SendAsync(socket, new { type = "auth_required" });
        using JsonDocument auth = await ReadAsync(socket);
        Check(auth.RootElement.GetProperty("type").GetString() == "auth", "auth request");
        await SendAsync(socket, new { type = "auth_invalid" });
    }

    using (WebSocket socket = await AcceptAsync())
    {
        await AuthAsync(socket);
        byte[] buffer = new byte[64];
        ValueWebSocketReceiveResult close = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Check(close.MessageType == WebSocketMessageType.Close, "final manual close");
    }
}

async Task<WebSocket> AcceptAsync()
{
    HttpListenerContext context = await listener.GetContextAsync().WaitAsync(timeout.Token);
    Check(context.Request.Url?.AbsolutePath == "/api/websocket", "normalized endpoint");
    return (await context.AcceptWebSocketAsync(null)).WebSocket;
}

async Task AuthAsync(WebSocket socket, bool fragmented = false)
{
    await SendAsync(socket, new { type = "auth_required" }, fragmented);
    using JsonDocument auth = await ReadAsync(socket);
    Check(auth.RootElement.GetProperty("type").GetString() == "auth", "auth request");
    await SendAsync(socket, new { type = "auth_ok" });
}

static object State(string value) => new
{
    entity_id = "sensor.test", state = value,
    attributes = new { unit_of_measurement = "C" },
    last_changed = "2024-01-01T00:00:00+00:00",
    last_updated = "2024-01-01T00:00:00+00:00"
};

async Task SendAsync(WebSocket socket, object value, bool fragmented = false)
{
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
    if (fragmented)
    {
        int split = bytes.Length / 2;
        await socket.SendAsync(bytes.AsMemory(0, split), WebSocketMessageType.Text, false, timeout.Token);
        await socket.SendAsync(bytes.AsMemory(split), WebSocketMessageType.Text, true, timeout.Token);
    }
    else await socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
}

async Task<JsonDocument> ReadAsync(WebSocket socket)
{
    byte[] buffer = new byte[8192];
    using var message = new MemoryStream();
    while (true)
    {
        ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
        Check(part.MessageType == WebSocketMessageType.Text, "server received text");
        message.Write(buffer, 0, part.Count);
        if (part.EndOfMessage) return JsonDocument.Parse(message.ToArray());
    }
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception($"Failed: {name}");
}

static async Task ThrowsAsync<T>(Task task) where T : Exception
{
    try { await task; }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class SilentLogger : IPluginLogger
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
