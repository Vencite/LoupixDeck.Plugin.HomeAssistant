using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

/// <summary>
/// Lifecycle checks for <see cref="HomeAssistantPlugin"/> against a synthetic Home Assistant
/// server: settings validation, the newest-save-wins race, no-op saves and clean shutdown.
/// </summary>
internal static class PluginLifecycleSmoke
{
    public static async Task RunAsync()
    {
        Check(!new HomeAssistantConnectionSettings("", "token").IsComplete, "missing URL is incomplete");
        Check(!new HomeAssistantConnectionSettings("   ", "token").IsComplete, "blank URL is incomplete");
        Check(!new HomeAssistantConnectionSettings("http://ha.local:8123", "").IsComplete, "missing token is incomplete");
        Check(!new HomeAssistantConnectionSettings("http://ha.local:8123", "  ").IsComplete, "blank token is incomplete");
        Check(new HomeAssistantConnectionSettings("http://ha.local:8123", "token").IsComplete, "complete settings");

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var server = new SyntheticServer(listener, timeout.Token);
        _ = server.RunAsync();

        var host = new FakeHost();
        string url = $"http://127.0.0.1:{port}";
        host.Settings.Set("url", url);
        host.Settings.Set("accessToken", "slow-a");

        var plugin = new HomeAssistantPlugin();
        plugin.Initialize(host);

        // An incomplete configuration is reported without touching the network.
        IReadOnlyList<PluginSettingAction> actions = plugin.SettingsActions;
        Check(actions.Count == 1 && actions[0].Label == "Test Connection", "settings expose Test Connection");
        host.Settings.Set("accessToken", "");
        Check(await actions[0].Invoke() == "Access token is missing.", "missing token reported");
        host.Settings.Set("url", "");
        host.Settings.Set("accessToken", "slow-a");
        Check(await actions[0].Invoke() == "Home Assistant URL is missing.", "missing URL reported");
        host.Settings.Set("url", url);

        // The second save must win: the first, slow attempt is cancelled before it authenticates.
        await server.FirstConnectionSeen.Task.WaitAsync(timeout.Token);
        host.Settings.Set("accessToken", "good-b");
        plugin.OnSettingsSaved();
        await server.GoodActive.Task.WaitAsync(timeout.Token);
        int connectionsAfterStart = server.ConnectionCount;

        // Outlive the slow attempt's delay: it must never have issued a command or retried.
        await Task.Delay(TimeSpan.FromMilliseconds(2500), timeout.Token);
        Check(!server.IsActive("slow-a"), "cancelled save never became active");
        Check(server.IsActive("good-b"), "newest save is the active one");
        Check(server.ConnectionCount == connectionsAfterStart, "cancelled save did not retry");

        // Saving the identical settings again must not restart the connection.
        int connections = server.ConnectionCount;
        plugin.OnSettingsSaved();
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        Check(server.ConnectionCount == connections, "unchanged settings do not reconnect");
        Check(!server.GoodClosed.Task.IsCompleted, "unchanged settings keep the connection open");

        // Test Connection is isolated: it uses a temporary client and never restarts the runtime.
        host.Settings.Set("accessToken", "bad-token");
        Check(await actions[0].Invoke() == "Authentication failed.", "authentication failure reported");
        host.Settings.Set("accessToken", "test-ok");
        Check(await actions[0].Invoke() == "Connected successfully. Home Assistant returned 2 entities.",
            "successful connection test reports entity count");
        Check(!server.GoodClosed.Task.IsCompleted, "connection test keeps the runtime alive");

        plugin.Shutdown();
        await server.GoodClosed.Task.WaitAsync(timeout.Token);
        int afterShutdown = server.ConnectionCount;
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        Check(server.ConnectionCount == afterShutdown, "shutdown stops reconnecting");

        plugin.Shutdown(); // Must be safe to repeat.
        Console.WriteLine("HomeAssistantPlugin lifecycle smoke check passed.");
    }

    private sealed class SyntheticServer(HttpListener listener, CancellationToken cancellationToken)
    {
        private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
        private int _connections;

        public TaskCompletionSource FirstConnectionSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GoodActive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GoodClosed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ConnectionCount => Volatile.Read(ref _connections);

        public bool IsActive(string token) => _active.ContainsKey(token);

        public async Task RunAsync()
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync().WaitAsync(cancellationToken); }
                catch (Exception) { return; }

                WebSocket socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                _ = HandleAsync(socket);
            }
        }

        private async Task HandleAsync(WebSocket socket)
        {
            int index = Interlocked.Increment(ref _connections);
            if (index == 1) FirstConnectionSeen.TrySetResult();
            string token = "";
            try
            {
                await SendAsync(socket, new { type = "auth_required" });
                using (JsonDocument auth = await ReadAsync(socket))
                    token = auth.RootElement.GetProperty("access_token").GetString() ?? "";

                if (token == "slow-a")
                {
                    // Deliberately slow: a superseded attempt is cancelled while this is pending.
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                    await SendAsync(socket, new { type = "auth_ok" });
                }
                else if (token == "bad-token")
                {
                    await SendAsync(socket, new { type = "auth_invalid" });
                }
                else
                {
                    await SendAsync(socket, new { type = "auth_ok" });
                }

                if (token is "slow-a" or "bad-token")
                {
                    using JsonDocument _ = await ReadAsync(socket);
                    return;
                }

                await ServeRequestsAsync(socket, token);
            }
            catch (Exception)
            {
                // A closed or aborted socket is a normal end of the synthetic session.
            }
            finally
            {
                if (token == "good-b") GoodClosed.TrySetResult();
            }
        }

        private async Task ServeRequestsAsync(WebSocket socket, string token)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                JsonDocument message;
                try { message = await ReadAsync(socket); }
                catch (Exception) { return; }

                using (message)
                {
                    int id = message.RootElement.GetProperty("id").GetInt32();
                    string type = message.RootElement.GetProperty("type").GetString() ?? "";
                    _active[token] = 1;

                    if (type == "get_states")
                    {
                        await SendAsync(socket, new
                        {
                            id, type = "result", success = true,
                            result = new object[] { State("sensor.one", "1"), State("sensor.two", "2") }
                        });
                        if (token == "good-b") GoodActive.TrySetResult();
                    }
                    else
                    {
                        await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                    }
                }
            }
        }

        private static object State(string entityId, string value) => new
        {
            entity_id = entityId, state = value, attributes = new { },
            last_changed = "2024-01-01T00:00:00+00:00", last_updated = "2024-01-01T00:00:00+00:00"
        };

        private async Task SendAsync(WebSocket socket, object message) =>
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, cancellationToken);

        private async Task<JsonDocument> ReadAsync(WebSocket socket)
        {
            byte[] buffer = new byte[8192];
            using var message = new MemoryStream();
            while (true)
            {
                ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (part.MessageType == WebSocketMessageType.Close)
                    throw new IOException("client closed");
                message.Write(buffer, 0, part.Count);
                if (part.EndOfMessage) return JsonDocument.Parse(message.ToArray());
            }
        }
    }

    private sealed class FakeHost : IPluginHost
    {
        public IPluginLogger Logger { get; } = new SilentLifecycleLogger();
        public IPluginSettings Settings { get; } = new FakeSettings();
        public string CurrentLanguage => "en";
        public string Tr(string english) => english;
        public FolderGridInfo FolderGrid => new(5, 3, 14);
        public DeviceInfo? ActiveDevice => null;
        public bool IsInExclusiveMode => false;
        public void RequestButtonRefresh(string commandName) { }
        public void ExecuteCommand(string command) { }
        public void OpenFolder(IFolderProvider provider) { }
        public bool OpenBrowser(string url) => false;
        public void OverlayTouchText(int slot, string text, TimeSpan duration) { }
        public int GetTouchSlotForRotary(int rotaryIndex) => -1;
        public bool RequestExclusiveMode(IExclusiveModeProvider provider) => false;
        public void ReleaseExclusiveMode(IExclusiveModeProvider provider) { }
        public IFullDisplayRenderSession? RequestFullDisplayRenderer(IFullDisplayRenderer renderer) => null;
        public IReadOnlyList<string> GetButtonStates(string commandName) => [];
        public string? GetActiveButtonState(string commandName) => null;
        public bool SetActiveButtonState(string commandName, string stateNameOrId) => false;
    }

    private sealed class FakeSettings : IPluginSettings
    {
        private readonly Dictionary<string, object> _values = [];

        public T? Get<T>(string key, T? defaultValue = default) =>
            _values.TryGetValue(key, out object? value) && value is T typed ? typed : defaultValue;

        public void Set<T>(string key, T value) => _values[key] = value!;

        public bool Contains(string key) => _values.ContainsKey(key);

        public void Remove(string key) => _values.Remove(key);

        public IEnumerable<string> Keys => _values.Keys;

        public void Save() { }
    }

    private sealed class SilentLifecycleLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }
}
