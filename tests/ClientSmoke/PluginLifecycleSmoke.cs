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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

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

        // Shutdown while the owned lifecycle is still connecting: it must stop the lifecycle first,
        // so the attempt can neither publish a runtime nor keep running behind the shutdown.
        var racingLogger = new CapturingLogger();
        var racingHost = new FakeHost(racingLogger);
        racingHost.Settings.Set("url", url);
        racingHost.Settings.Set("accessToken", "slow-shutdown");
        var racingPlugin = new HomeAssistantPlugin();
        racingPlugin.Initialize(racingHost);
        await UntilAsync(() => server.ConnectionCount > afterShutdown, timeout.Token);

        racingPlugin.Shutdown();
        Check(racingLogger.Count(ShutdownTimedOut) == 0, "shutdown awaited the lifecycle instead of timing out");
        Check(!server.IsActive("slow-shutdown"), "shutdown prevented a late runtime publish");

        // Outlive the delayed authentication: nothing may be published or retried afterwards.
        await Task.Delay(TimeSpan.FromMilliseconds(2500), timeout.Token);
        Check(!server.IsActive("slow-shutdown"), "stopped lifecycle did not become active later");
        Check(racingLogger.Count("Home Assistant connection lifecycle failed.") == 0,
            "shutdown did not fault the lifecycle");

        // A save after shutdown must not start a session again.
        int racingConnections = server.ConnectionCount;
        racingHost.Settings.Set("accessToken", "after-shutdown");
        racingPlugin.OnSettingsSaved();
        await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
        Check(server.ConnectionCount == racingConnections, "no session starts after shutdown");
        racingPlugin.Shutdown(); // Repeated shutdown is safe.

        // An unreachable endpoint keeps the initial connection in its retry loop, which both a
        // settings save and a shutdown must cancel immediately.
        int unreachablePort = FreePort();
        string unreachableUrl = $"http://127.0.0.1:{unreachablePort}";

        var retryLogger = new CapturingLogger();
        var retryHost = new FakeHost(retryLogger);
        retryHost.Settings.Set("url", unreachableUrl);
        retryHost.Settings.Set("accessToken", "retry-a");
        var retryPlugin = new HomeAssistantPlugin();
        retryPlugin.Initialize(retryHost);
        await retryLogger.WaitForCountAsync(RetryFailed, 2, timeout.Token);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        retryPlugin.Shutdown();
        stopwatch.Stop();
        Check(stopwatch.Elapsed < TimeSpan.FromSeconds(4), "shutdown does not wait out the retry timeout");
        Check(retryLogger.Count(ShutdownTimedOut) == 0, "cooperative cancellation is not a timeout");
        Check(retryLogger.Count("Home Assistant runtime stopped.") == 1, "retry scenario stopped cleanly");
        int retryFailures = retryLogger.Count(RetryFailed);
        await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
        Check(retryLogger.Count(RetryFailed) == retryFailures, "shutdown stopped the retry loop");
        retryPlugin.Shutdown(); // Repeated shutdown is safe.

        var switchedLogger = new CapturingLogger();
        var switchedHost = new FakeHost(switchedLogger);
        switchedHost.Settings.Set("url", unreachableUrl);
        switchedHost.Settings.Set("accessToken", "retry-b");
        var switchedPlugin = new HomeAssistantPlugin();
        switchedPlugin.Initialize(switchedHost);
        await switchedLogger.WaitForCountAsync(RetryFailed, 1, timeout.Token);

        switchedHost.Settings.Set("url", url);
        switchedHost.Settings.Set("accessToken", "retry-c");
        switchedPlugin.OnSettingsSaved();
        await UntilAsync(() => server.IsActive("retry-c"), timeout.Token);
        int switchedFailures = switchedLogger.Count(RetryFailed);
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        Check(switchedLogger.Count(RetryFailed) == switchedFailures, "settings save stopped the retry loop");
        switchedPlugin.Shutdown();

        // A lifecycle that cannot stop within the budget must not lead to a concurrent cleanup or to
        // the disposal of cancellation sources it is still using.
        var blockingLogger = new BlockingRetryLogger();
        var blockingHost = new FakeHost(blockingLogger);
        blockingHost.Settings.Set("url", $"http://127.0.0.1:{FreePort()}");
        blockingHost.Settings.Set("accessToken", "blocked");
        var blockingPlugin = new HomeAssistantPlugin();
        blockingPlugin.Initialize(blockingHost);
        blockingLogger.WaitUntilBlocked();

        var blockingWatch = System.Diagnostics.Stopwatch.StartNew();
        blockingPlugin.Shutdown();
        blockingWatch.Stop();
        Check(blockingWatch.Elapsed >= TimeSpan.FromSeconds(4), "slow lifecycle consumed the shutdown budget");
        Check(blockingWatch.Elapsed < TimeSpan.FromSeconds(10), "shutdown stayed bounded");
        Check(blockingLogger.Count(ShutdownTimedOut) == 1, "slow lifecycle reported the timeout");
        Check(blockingLogger.Count("Home Assistant runtime stopped.") == 0,
            "timed-out shutdown did not clean up concurrently");

        // Let the stuck lifecycle continue. It may not touch anything the shutdown disposed.
        blockingLogger.Release();
        await blockingLogger.WaitForCountAsync("Home Assistant disconnected.", 1, timeout.Token);
        Check(blockingLogger.Count("Home Assistant connection lifecycle failed.") == 0,
            "running lifecycle did not use a disposed resource");
        blockingPlugin.Shutdown(); // Repeated shutdown is safe.

        // An owned lifecycle that ends as canceled or faulted must not surface to the host.
        await ShutdownAfterInvalidSettingsAsync(cancel: true, timeout.Token);
        await ShutdownAfterInvalidSettingsAsync(cancel: false, timeout.Token);

        Console.WriteLine("HomeAssistantPlugin lifecycle smoke check passed.");
    }

    /// <summary>
    /// Drives the owned lifecycle into a canceled (or faulted) state by making the host logger throw
    /// while the plugin reports a non-transient connection failure, then shuts down.
    /// </summary>
    private static async Task ShutdownAfterInvalidSettingsAsync(bool cancel, CancellationToken cancellationToken)
    {
        var logger = new ThrowingLogger(cancel);
        var host = new FakeHost(logger);
        host.Settings.Set("url", "not-a-url");
        host.Settings.Set("accessToken", "token");
        var plugin = new HomeAssistantPlugin();
        plugin.Initialize(host);

        await UntilAsync(() => logger.Errors > 0, cancellationToken);

        plugin.Shutdown(); // Must not throw, in particular no AggregateException.
        plugin.Shutdown();

        string kind = cancel ? "canceled" : "faulted";
        Check(logger.Errors == 1, $"{kind} lifecycle ended once");
    }

    private const string RetryFailed = "Initial connection to Home Assistant failed";
    private const string ShutdownTimedOut = "Home Assistant connection lifecycle did not stop within the shutdown timeout";

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition()) await Task.Delay(20, cancellationToken);
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

                if (token.StartsWith("slow-", StringComparison.Ordinal))
                {
                    // Deliberately slow: a superseded or shut-down attempt is cancelled while this
                    // is pending, so it must never reach the request phase.
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

                if (token == "bad-token" || token.StartsWith("slow-", StringComparison.Ordinal))
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

    private sealed class FakeHost(IPluginLogger? logger = null) : IPluginHost
    {
        public IPluginLogger Logger { get; } = logger ?? new SilentLifecycleLogger();
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

    /// <summary>Keeps the plugin's lifecycle messages so the retry loop can be observed.</summary>
    private class CapturingLogger : IPluginLogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public virtual void Info(string message) => _messages.Enqueue(message);
        public virtual void Warn(string message) => _messages.Enqueue(message);
        public virtual void Error(string message, Exception? exception = null) => _messages.Enqueue(message);

        public int Count(string prefix) =>
            _messages.Count(message => message.StartsWith(prefix, StringComparison.Ordinal));

        public async Task WaitForCountAsync(string prefix, int expected, CancellationToken cancellationToken)
        {
            while (Count(prefix) < expected) await Task.Delay(20, cancellationToken);
        }
    }

    /// <summary>
    /// Blocks inside the first retry warning, which is an uncancellable call from the lifecycle's
    /// point of view: the shutdown budget has to expire while the lifecycle is still running.
    /// </summary>
    private sealed class BlockingRetryLogger : CapturingLogger
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private int _blocked;

        public override void Warn(string message)
        {
            base.Warn(message);
            if (!message.StartsWith(RetryFailed, StringComparison.Ordinal)) return;
            if (Interlocked.Exchange(ref _blocked, 1) != 0) return;
            _entered.Set();
            _release.Wait(TimeSpan.FromSeconds(60));
        }

        public void WaitUntilBlocked() => Check(_entered.Wait(TimeSpan.FromSeconds(10)), "retry warning blocked");

        public void Release() => _release.Set();
    }

    /// <summary>
    /// A host logger that fails, driving the owned lifecycle into a canceled (or faulted) state so
    /// the shutdown has to cope with an owned task that did not complete successfully.
    /// </summary>
    private sealed class ThrowingLogger(bool cancel) : IPluginLogger
    {
        private int _errors;

        public int Errors => Volatile.Read(ref _errors);

        public void Info(string message) { }
        public void Warn(string message) { }

        public void Error(string message, Exception? exception = null)
        {
            Interlocked.Increment(ref _errors);
            if (cancel) throw new OperationCanceledException();
            throw new InvalidOperationException("Synthetic host logger failure.");
        }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }
}
