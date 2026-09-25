using System.Net.WebSockets;
using System.Text.Json;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

/// <summary>Home Assistant WebSocket transport. Callbacks should return promptly.</summary>
public sealed class HomeAssistantClient(IPluginLogger logger) : IAsyncDisposable
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _subscriptionGate = new(1, 1);
    private readonly SemaphoreSlim _disconnectGate = new(1, 1);
    private readonly Dictionary<int, PendingRequest> _pending = [];
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _stop;
    private Task? _runTask;
    private HomeAssistantConnectionState _state;
    private bool _stopping;
    private bool _disposed;
    private bool _wantsStateChanges;
    private int? _subscriptionId;
    private int _nextId;

    public event EventHandler<HomeAssistantStateChangedEvent>? StateChanged;

    /// <summary>Raised after a reconnect is authenticated, before subscriptions are restored.</summary>
    public event EventHandler? ConnectionRestored;

    public HomeAssistantConnectionState State
    {
        get { lock (_sync) return _state; }
    }

    public async Task ConnectAsync(string url, string accessToken, CancellationToken cancellationToken = default)
    {
        Uri endpoint = NormalizeWebSocketUri(url);
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("A Home Assistant access token is required.", nameof(accessToken));

        var firstConnection = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping || _runTask is { IsCompleted: false })
                throw new InvalidOperationException("Home Assistant client is already running.");
            _stop?.Dispose();
            var stop = new CancellationTokenSource();
            _stop = stop;
            _state = HomeAssistantConnectionState.Connecting;
            _runTask = Task.Run(() => RunAsync(endpoint, accessToken, firstConnection, stop.Token));
        }

        try
        {
            await firstConnection.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            Task? runTask;
            lock (_sync) runTask = _runTask;
            if (runTask is not null) await runTask.ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<HomeAssistantState>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        (_, JsonElement result) = await SendRequestAsync(id => new { id, type = "get_states" }, false, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Home Assistant get_states returned a non-array result.");
        try
        {
            return result.EnumerateArray().Select(ParseState).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            throw new InvalidDataException("Home Assistant get_states returned an invalid state.", ex);
        }
    }

    public async Task SubscribeStateChangesAsync(CancellationToken cancellationToken = default)
    {
        await _subscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_wantsStateChanges && _subscriptionId.HasValue)
                    return;
            }
            await SendRequestAsync(id => new { id, type = "subscribe_events", event_type = "state_changed" }, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _subscriptionGate.Release();
        }
    }

    /// <summary>
    /// Calls a Home Assistant service, optionally targeting a single entity. The service and the
    /// optional entity id are validated locally, so malformed user input never reaches the server.
    /// </summary>
    public async Task CallServiceAsync(string domain, string service, string? entityId = null,
        CancellationToken cancellationToken = default)
    {
        if (!HomeAssistantIdentifiers.IsServiceToken(domain))
            throw new ArgumentException("Provide a Home Assistant domain such as light or script.", nameof(domain));
        if (!HomeAssistantIdentifiers.IsServiceToken(service))
            throw new ArgumentException("Provide a Home Assistant service such as toggle or turn_on.", nameof(service));

        string? target = string.IsNullOrWhiteSpace(entityId) ? null : entityId.Trim();
        if (target is not null && !HomeAssistantIdentifiers.IsEntityId(target))
            throw new ArgumentException("Provide a Home Assistant entity id such as light.office.", nameof(entityId));

        await SendRequestAsync(
            id => target is null
                ? (object)new { id, type = "call_service", domain, service }
                : new { id, type = "call_service", domain, service, target = new { entity_id = target } },
            false, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _disconnectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ClientWebSocket? socket;
            Task? runTask;
            CancellationTokenSource? stop;
            lock (_sync)
            {
                _stopping = true;
                _wantsStateChanges = false;
                _subscriptionId = null;
                _state = HomeAssistantConnectionState.Disconnected;
                socket = _socket;
                runTask = _runTask;
                stop = _stop;
            }
            FailPending(new IOException("Home Assistant connection was closed."));

            if (socket?.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _sendGate.WaitAsync(closeTimeout.Token).ConfigureAwait(false);
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    finally { _sendGate.Release(); }
                }
                catch (Exception)
                {
                    logger.Warn("Home Assistant socket could not close gracefully.");
                }
            }

            stop?.Cancel();
            try { socket?.Abort(); }
            catch (ObjectDisposedException) { }
            if (runTask is not null)
                await runTask.ConfigureAwait(false);
            FailPending(new IOException("Home Assistant connection was closed."));
            lock (_sync)
            {
                _runTask = null;
                _socket = null;
                _stop = null;
                _stopping = false;
                _state = HomeAssistantConnectionState.Disconnected;
            }
            stop?.Dispose();
            logger.Info("Home Assistant disconnected.");
        }
        finally { _disconnectGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
        _subscriptionGate.Dispose();
        _disconnectGate.Dispose();
    }

    internal static Uri NormalizeWebSocketUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme is not ("http" or "https" or "ws" or "wss") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Provide an absolute Home Assistant HTTP or WebSocket URL without credentials, query, or fragment.", nameof(url));

        string path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length != 0 && path != "/api/websocket")
            throw new ArgumentException("Home Assistant URL must point to the server root or /api/websocket.", nameof(url));

        return new UriBuilder(uri)
        {
            Scheme = uri.Scheme switch { "http" => "ws", "https" => "wss", _ => uri.Scheme },
            Path = "/api/websocket",
            Query = "",
            Fragment = ""
        }.Uri;
    }

    private async Task RunAsync(Uri endpoint, string token, TaskCompletionSource<bool> firstConnection, CancellationToken stopping)
    {
        bool connectedOnce = false;
        int retry = 0;
        while (!stopping.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            Task? receiveTask = null;
            lock (_sync)
            {
                _socket = socket;
                _state = connectedOnce ? HomeAssistantConnectionState.Reconnecting : HomeAssistantConnectionState.Connecting;
            }

            try
            {
                logger.Info(connectedOnce ? "Reconnecting to Home Assistant." : "Connecting to Home Assistant.");
                using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
                {
                    handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    await socket.ConnectAsync(endpoint, handshakeTimeout.Token).ConfigureAwait(false);
                    await AuthenticateAsync(socket, token, handshakeTimeout.Token).ConfigureAwait(false);
                }
                logger.Info("Home Assistant authenticated.");
                lock (_sync) _state = HomeAssistantConnectionState.Connected;
                receiveTask = ReceiveLoopAsync(socket, stopping);

                if (connectedOnce)
                {
                    EventHandler? handlers = ConnectionRestored;
                    if (handlers is not null)
                        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
                        {
                            try { handler(this, EventArgs.Empty); }
                            catch (Exception ex) { logger.Warn($"Home Assistant reconnect callback failed ({ex.GetType().Name})."); }
                        }
                }

                bool restore;
                lock (_sync) restore = _wantsStateChanges;
                if (restore)
                {
                    await _subscriptionGate.WaitAsync(stopping).ConfigureAwait(false);
                    try
                    {
                        lock (_sync) restore = _wantsStateChanges && _subscriptionId is null;
                        if (restore)
                            await SendRequestAsync(id => new { id, type = "subscribe_events", event_type = "state_changed" }, true, stopping).ConfigureAwait(false);
                    }
                    finally { _subscriptionGate.Release(); }
                }

                firstConnection.TrySetResult(true);
                logger.Info(connectedOnce ? "Home Assistant reconnected." : "Home Assistant connected.");
                connectedOnce = true;
                retry = 0;
                await receiveTask.ConfigureAwait(false);
                if (!stopping.IsCancellationRequested)
                    throw new IOException("Home Assistant closed the WebSocket connection.");
            }
            catch (HomeAssistantAuthenticationException ex)
            {
                logger.Warn("Home Assistant authentication failed; automatic reconnect stopped.");
                firstConnection.TrySetException(ex);
                break;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                logger.Warn("Home Assistant connection or authentication timed out.");
                if (!connectedOnce)
                {
                    firstConnection.TrySetException(new TimeoutException("Home Assistant connection or authentication timed out."));
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Home Assistant connection failed ({ex.GetType().Name}).");
                if (!connectedOnce)
                {
                    firstConnection.TrySetException(ex);
                    break;
                }
            }
            finally
            {
                socket.Abort();
                if (receiveTask is not null)
                {
                    try { await receiveTask.ConfigureAwait(false); }
                    catch (Exception) { /* The connection failure is handled above. */ }
                }
                lock (_sync)
                {
                    if (ReferenceEquals(_socket, socket)) _socket = null;
                    _subscriptionId = null;
                    _state = connectedOnce && !stopping.IsCancellationRequested && !_stopping
                        ? HomeAssistantConnectionState.Reconnecting
                        : HomeAssistantConnectionState.Disconnected;
                }
                FailPending(new IOException("Home Assistant connection was lost."));
                logger.Info("Home Assistant disconnected.");
            }

            if (stopping.IsCancellationRequested) break;
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(retry++, 5)));
            try { await Task.Delay(delay, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        firstConnection.TrySetCanceled(stopping);
        lock (_sync) _state = HomeAssistantConnectionState.Disconnected;
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, string token, CancellationToken cancellationToken)
    {
        using (JsonDocument required = ParseMessage(await ReadMessageAsync(socket, cancellationToken).ConfigureAwait(false)))
        {
            if (ReadType(required.RootElement) != "auth_required")
                throw new InvalidDataException("Home Assistant did not send auth_required.");
        }
        await SendJsonAsync(socket, new { type = "auth", access_token = token }, cancellationToken, cancellationToken)
            .ConfigureAwait(false);
        using JsonDocument response = ParseMessage(await ReadMessageAsync(socket, cancellationToken).ConfigureAwait(false));
        switch (ReadType(response.RootElement))
        {
            case "auth_ok": return;
            case "auth_invalid": throw new HomeAssistantAuthenticationException();
            default: throw new InvalidDataException("Unexpected Home Assistant authentication response.");
        }
    }

    private async Task<(int Id, JsonElement Result)> SendRequestAsync(
        Func<int, object> buildMessage, bool subscription, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int id = Interlocked.Increment(ref _nextId);
        if (id <= 0) throw new InvalidOperationException("Home Assistant request IDs are exhausted.");
        var pending = new PendingRequest(subscription);
        ClientWebSocket socket;
        CancellationToken sessionToken;
        lock (_sync)
        {
            if (_state != HomeAssistantConnectionState.Connected || _socket is null || _stop is null)
                throw new InvalidOperationException("Home Assistant is not connected.");
            socket = _socket;
            sessionToken = _stop.Token;
            _pending.Add(id, pending);
        }

        using var registration = cancellationToken.Register(() => CancelPending(id, cancellationToken));
        try
        {
            Task send = SendJsonAsync(socket, buildMessage(id), cancellationToken, sessionToken);
            try
            {
                await send.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A started send may finish after the caller is cancelled; observe its failure.
                _ = send.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }
            return (id, await pending.Completion.Task.ConfigureAwait(false));
        }
        finally
        {
            lock (_sync) _pending.Remove(id);
        }
    }

    private async Task SendJsonAsync(ClientWebSocket socket, object message,
        CancellationToken waitToken, CancellationToken sendToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendGate.WaitAsync(waitToken).ConfigureAwait(false);
        try
        {
            // A caller cancelling its request must not abort a partly sent WebSocket frame.
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, sendToken)
                .ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using JsonDocument message = ParseMessage(await ReadMessageAsync(socket, cancellationToken).ConfigureAwait(false));
                JsonElement root = message.RootElement;
                switch (ReadType(root))
                {
                    case "result": HandleResult(root); break;
                    case "event":
                        try { HandleEvent(root); }
                        catch (InvalidDataException ex)
                        {
                            // A single malformed event must not kill the receive loop; the poll
                            // remains the safety net for whatever this event would have carried.
                            logger.Warn($"Home Assistant sent an invalid event ({ex.Message}).");
                        }
                        break;
                    case "pong": break;
                    default: logger.Warn("Home Assistant sent an unrecognized message type."); break;
                }
            }
        }
        finally { FailPending(new IOException("Home Assistant connection was lost.")); }
    }

    private void HandleResult(JsonElement root)
    {
        int id = ReadId(root);
        if (!root.TryGetProperty("success", out JsonElement success) ||
            success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Home Assistant result is missing success.");

        PendingRequest? pending;
        lock (_sync)
        {
            if (!_pending.Remove(id, out pending)) return; // A cancelled request may still receive a result.
            if (success.ValueKind == JsonValueKind.True && pending.Subscription && !_stopping)
            {
                _subscriptionId = id;
                _wantsStateChanges = true;
            }
        }

        if (success.ValueKind == JsonValueKind.False)
        {
            string code = "unknown";
            string errorMessage = "Unknown error";
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                code = ReadOptionalString(error, "code") ?? code;
                errorMessage = ReadOptionalString(error, "message") ?? errorMessage;
            }
            pending.Completion.TrySetException(new HomeAssistantRequestException(code, errorMessage));
        }
        else
        {
            JsonElement result = root.TryGetProperty("result", out JsonElement value) ? value.Clone() : default;
            pending.Completion.TrySetResult(result);
        }
    }

    private void HandleEvent(JsonElement root)
    {
        int id = ReadId(root);
        lock (_sync) if (_stopping || _subscriptionId != id) return;
        if (!root.TryGetProperty("event", out JsonElement eventValue) || eventValue.ValueKind != JsonValueKind.Object ||
            ReadOptionalString(eventValue, "event_type") != "state_changed" ||
            !eventValue.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Home Assistant state_changed event has invalid structure.");

        string entityId = ReadRequiredString(data, "entity_id");
        HomeAssistantState? oldState = ReadNullableState(data, "old_state");
        HomeAssistantState? newState = ReadNullableState(data, "new_state");
        DateTimeOffset timeFired;
        try { timeFired = eventValue.GetProperty("time_fired").GetDateTimeOffset(); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Home Assistant state_changed event has invalid time_fired.", ex);
        }
        var change = new HomeAssistantStateChangedEvent(entityId, oldState, newState, timeFired);
        EventHandler<HomeAssistantStateChangedEvent>? handlers = StateChanged;
        if (handlers is null) return;
        foreach (EventHandler<HomeAssistantStateChangedEvent> handler in handlers.GetInvocationList().Cast<EventHandler<HomeAssistantStateChangedEvent>>())
        {
            try { handler(this, change); }
            catch (Exception ex) { logger.Warn($"Home Assistant state callback failed ({ex.GetType().Name})."); }
        }
    }

    private static HomeAssistantState? ReadNullableState(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return ParseState(value);
    }

    private static HomeAssistantState ParseState(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("attributes", out JsonElement attributes) || attributes.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("last_changed", out JsonElement changed) ||
            !value.TryGetProperty("last_updated", out JsonElement updated))
            throw new InvalidDataException("Home Assistant state has invalid structure.");
        try
        {
            return new HomeAssistantState(
                ReadRequiredString(value, "entity_id"), ReadRequiredString(value, "state"),
                attributes.Clone(), changed.GetDateTimeOffset(), updated.GetDateTimeOffset());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Home Assistant state has invalid field values.", ex);
        }
    }

    private static int ReadId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt32(out int value) || value <= 0)
            throw new InvalidDataException("Home Assistant message has invalid id.");
        return value;
    }

    private static string ReadType(JsonElement root) => ReadRequiredString(root, "type");

    private static string ReadRequiredString(JsonElement root, string name) =>
        ReadOptionalString(root, name) ?? throw new InvalidDataException($"Home Assistant message is missing {name}.");

    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static JsonDocument ParseMessage(byte[] bytes)
    {
        try
        {
            JsonDocument message = JsonDocument.Parse(bytes);
            if (message.RootElement.ValueKind == JsonValueKind.Object) return message;
            message.Dispose();
            throw new InvalidDataException("Home Assistant message must be a JSON object.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Home Assistant sent invalid JSON.");
        }
    }

    private static async Task<byte[]> ReadMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult part = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (part.MessageType == WebSocketMessageType.Close)
                throw new IOException("Home Assistant closed the WebSocket connection.");
            if (part.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Home Assistant sent a non-text WebSocket message.");
            if (message.Length + part.Count > MaxMessageBytes)
                throw new InvalidDataException("Home Assistant WebSocket message exceeded the size limit.");
            message.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return message.ToArray();
        }
    }

    private void CancelPending(int id, CancellationToken cancellationToken)
    {
        PendingRequest? pending;
        lock (_sync) _pending.Remove(id, out pending);
        pending?.Completion.TrySetCanceled(cancellationToken);
    }

    private void FailPending(Exception error)
    {
        PendingRequest[] requests;
        lock (_sync)
        {
            requests = _pending.Values.ToArray();
            _pending.Clear();
        }
        foreach (PendingRequest request in requests)
            request.Completion.TrySetException(error);
    }

    private sealed class PendingRequest(bool subscription)
    {
        public bool Subscription { get; } = subscription;
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
