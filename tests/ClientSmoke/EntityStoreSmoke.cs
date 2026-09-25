using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

internal static class EntityStoreSmoke
{
    public static async Task RunAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var releaseLive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSynchronized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int synchronized = 0;
        int appliedLiveChanges = 0;
        Task server = ServeAsync();

        await using var client = new HomeAssistantClient(new SilentStoreLogger());
        await client.ConnectAsync($"http://127.0.0.1:{port}", "synthetic-token", timeout.Token);
        await using var store = new EntityStore(client, new SilentStoreLogger());
        store.Synchronized += (_, _) =>
        {
            if (Interlocked.Increment(ref synchronized) == 2) secondSynchronized.TrySetResult();
        };
        store.EntityChanged += (_, change) =>
        {
            if (change.EntityId != "sensor.transition") Interlocked.Increment(ref appliedLiveChanges);
        };
        store.EntityChanged += (_, _) => throw new InvalidOperationException("Synthetic consumer failure.");
        await store.InitializeAsync(timeout.Token);
        await store.InitializeAsync(timeout.Token); // Idempotent; the server expects only one subscription.
        Check(store.IsInitialized && store.Count >= 3, "initial synchronization");
        Check(store.TryGet("sensor.alpha", out HomeAssistantState? alpha) && alpha?.State == "snapshot",
            "stale buffered event did not roll back snapshot");
        Check(store.TryGet("sensor.beta", out HomeAssistantState? beta) && beta?.State == "buffered",
            "newer buffered event applied");
        Check(store.TryGet("sensor.gamma", out _), "buffered entity addition");
        Check(!store.TryGet("sensor.delta", out _), "buffered entity removal");
        Check(store.GetSnapshot().Count == store.Count, "snapshot copy");

        releaseLive.SetResult();
        await liveSent.Task.WaitAsync(timeout.Token);
        await UntilAsync(() => Volatile.Read(ref appliedLiveChanges) == 2, timeout.Token);
        Check(store.TryGet("sensor.beta", out beta) && beta?.State == "live",
            "stale and duplicate live events did not roll back current state");
        Check(!store.TryGet("sensor.gamma", out _), "removal tombstone rejects late add");
        await UntilAsync(() => store.TryGet("sensor.transition", out _), timeout.Token);

        releaseReconnect.SetResult();
        await secondSynchronized.Task.WaitAsync(timeout.Token);
        Check(store.IsInitialized && store.Count == 2, "reconnect snapshot replaced old cache");
        Check(store.TryGet("sensor.alpha", out alpha) && alpha?.State == "resynced",
            "reconnect snapshot applied");
        Check(store.TryGet("sensor.zeta", out _) && !store.TryGet("sensor.beta", out _),
            "reconnect recovered additions and removals missed during outage");
        await client.DisconnectAsync();
        await server.WaitAsync(timeout.Token);
        Console.WriteLine("EntityStore smoke check passed.");

        async Task ServeAsync()
        {
            using (WebSocket socket = await AcceptAsync())
            {
                await AuthenticateAsync(socket);
                int subscriptionId = await ReadRequestIdAsync(socket, "subscribe_events");
                await SendAsync(socket, new { id = subscriptionId, type = "result", success = true, result = (object?)null });
                await EventAsync(socket, subscriptionId, "sensor.alpha", null, State("sensor.alpha", "older", 1), 1);
                await EventAsync(socket, subscriptionId, "sensor.alpha", State("sensor.alpha", "older", 1), null, 1);
                await EventAsync(socket, subscriptionId, "sensor.beta", State("sensor.beta", "old", 1), State("sensor.beta", "buffered", 3), 3);
                await EventAsync(socket, subscriptionId, "sensor.gamma", null, State("sensor.gamma", "added", 3), 3);
                await EventAsync(socket, subscriptionId, "sensor.delta", State("sensor.delta", "present", 2), null, 3);
                int snapshotId = await ReadRequestIdAsync(socket, "get_states");
                await SendAsync(socket, new { id = snapshotId, type = "result", success = true,
                    result = new[] { State("sensor.alpha", "snapshot", 2), State("sensor.beta", "old", 1), State("sensor.delta", "present", 2) } });
                await EventAsync(socket, subscriptionId, "sensor.transition", null, State("sensor.transition", "boundary", 4), 4);

                await releaseLive.Task.WaitAsync(timeout.Token);
                await EventAsync(socket, subscriptionId, "sensor.beta", State("sensor.beta", "buffered", 3), State("sensor.beta", "live", 5), 5);
                await EventAsync(socket, subscriptionId, "sensor.beta", null, State("sensor.beta", "stale", 4), 4);
                await EventAsync(socket, subscriptionId, "sensor.beta", null, State("sensor.beta", "duplicate", 5), 5);
                await EventAsync(socket, subscriptionId, "sensor.gamma", State("sensor.gamma", "added", 3), null, 6);
                await EventAsync(socket, subscriptionId, "sensor.gamma", null, State("sensor.gamma", "late", 5), 5);
                liveSent.SetResult();
                await releaseReconnect.Task.WaitAsync(timeout.Token);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }

            using (WebSocket socket = await AcceptAsync())
            {
                await AuthenticateAsync(socket);
                int subscriptionId = await ReadRequestIdAsync(socket, "subscribe_events");
                await SendAsync(socket, new { id = subscriptionId, type = "result", success = true, result = (object?)null });
                int snapshotId = await ReadRequestIdAsync(socket, "get_states");
                await SendAsync(socket, new { id = snapshotId, type = "result", success = true,
                    result = new[] { State("sensor.alpha", "resynced", 10), State("sensor.zeta", "new", 10) } });
                byte[] buffer = new byte[64];
                ValueWebSocketReceiveResult close = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                Check(close.MessageType == WebSocketMessageType.Close, "reconnect session clean close");
            }
        }

        async Task<WebSocket> AcceptAsync()
        {
            HttpListenerContext context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            return (await context.AcceptWebSocketAsync(null)).WebSocket;
        }

        async Task AuthenticateAsync(WebSocket socket)
        {
            await SendAsync(socket, new { type = "auth_required" });
            using JsonDocument auth = await ReadAsync(socket);
            Check(auth.RootElement.GetProperty("type").GetString() == "auth", "synthetic auth");
            await SendAsync(socket, new { type = "auth_ok" });
        }

        async Task<int> ReadRequestIdAsync(WebSocket socket, string expectedType)
        {
            using JsonDocument request = await ReadAsync(socket);
            Check(request.RootElement.GetProperty("type").GetString() == expectedType, expectedType);
            return request.RootElement.GetProperty("id").GetInt32();
        }

        async Task EventAsync(WebSocket socket, int id, string entityId, object? oldState, object? newState, int second) =>
            await SendAsync(socket, new { id, type = "event", @event = new
            {
                event_type = "state_changed", time_fired = Timestamp(second),
                data = new { entity_id = entityId, old_state = oldState, new_state = newState }
            } });

        async Task SendAsync(WebSocket socket, object message) =>
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, timeout.Token);

        async Task<JsonDocument> ReadAsync(WebSocket socket)
        {
            byte[] buffer = new byte[8192];
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                Check(part.MessageType == WebSocketMessageType.Text, "synthetic request frame");
                message.Write(buffer, 0, part.Count);
                if (part.EndOfMessage) return JsonDocument.Parse(message.ToArray());
            }
        }
    }

    private static object State(string entityId, string value, int second) => new
    {
        entity_id = entityId, state = value, attributes = new { },
        last_changed = Timestamp(second), last_updated = Timestamp(second)
    };

    private static string Timestamp(int second) => $"2024-01-01T00:00:{second:00}+00:00";

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(10, cancellationToken);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private sealed class SilentStoreLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
