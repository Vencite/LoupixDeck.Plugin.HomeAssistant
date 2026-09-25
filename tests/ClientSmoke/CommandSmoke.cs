using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using LoupixDeck.Plugin.HomeAssistant;
using LoupixDeck.PluginSdk;

/// <summary>
/// Checks the command layer against a synthetic Home Assistant server: dynamic entity submenus, the
/// per-entity service commands, the generic service escape hatch and the touch-button rendering.
/// </summary>
internal static class CommandSmoke
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = new ControlServer(listener, timeout.Token);
        _ = server.RunAsync();

        var host = new RecordingHost();
        host.Settings.Set("url", $"http://127.0.0.1:{port}");
        host.Settings.Set("accessToken", "token");

        var plugin = new HomeAssistantPlugin();
        plugin.Initialize(host);

        IMenuContributor menu = plugin;
        await UntilAsync(async () =>
        {
            IReadOnlyList<MenuNode> nodes = await menu.GetMenuNodes(ButtonTargets.TouchButton);
            return nodes.Any(node => node.Children.Any(area => area.Children.Any(child => child.Name == "Lights")));
        }, timeout.Token);

        // The dynamic menu mirrors the controllable domains of the cache, with the entity id baked
        // into the shared parameter of a stable command.
        IReadOnlyList<MenuNode> roots = await menu.GetMenuNodes(ButtonTargets.TouchButton);
        Check(server.RegistryRequests == 3, "opening the picker uses cached registry metadata only");
        MenuNode group = roots.Single(node => node.Name == "Home Assistant");
        Check(group.Children.All(child => child.Name != "Not connected"), "connected menu");
        Check(group.Children.Any(child => child.Name == "Office") && group.Children.Any(child => child.Name == "Garden"),
            "entities are grouped by Home Assistant area");
        IReadOnlyList<MenuNode> domains = group.Children.SelectMany(area => area.Children).ToArray();
        MenuNode sensor = domains.Single(child => child.Name == "Sensors").Children.Single();
        Check(sensor.Children.Single().CommandName == "HomeAssistant.ShowEntity", "sensor offers a display-only action");

        MenuNode office = group.Children.Single(child => child.Name == "Office").Children
            .Single(child => child.Name == "Lights").Children.Single(child => child.Name == "Office Light");
        Check(group.Children.Single(child => child.Name == "Office").Children
            .Single(child => child.Name == "Switches").Children.Single().Name == "Kettle",
            "device area is used when the entity has no direct area");
        Check(!domains.SelectMany(domain => domain.Children).Any(entity => entity.Name == "Hidden"),
            "hidden registry entities are excluded from the picker");
        Check(office.Children.Count == 3, "light offers three actions");
        Check(office.Children.Any(child => child.Name == "Office Light · Toggle" && child.CommandName == "HomeAssistant.ToggleEntity"),
            "light toggle leaf");
        Check(office.Children.All(child => child.Name.Contains("Office Light", StringComparison.OrdinalIgnoreCase)),
            "searchable action names include the entity name");
        Check(office.Children.All(child => child.Parameters["EntityId"] == "light.office"), "leaf bakes the entity id");

        MenuNode movie = domains.Single(child => child.Name == "Scenes").Children.Single(child => child.Name == "Movie Night");
        Check(movie.Children.Single().CommandName == "HomeAssistant.ActivateScene", "scene has its own action");
        Check(domains.Single(child => child.Name == "Buttons").Children.Single().Children.Single().CommandName == "HomeAssistant.PressButton",
            "button has its own action");

        // Execution forwards the entity id to the matching service call.
        List<IPluginCommand> commands = plugin.GetCommands().ToList();
        Check(commands.Count == 8, "eight commands registered");
        IPluginCommand toggle = commands.Single(command => command.Descriptor.CommandName == "HomeAssistant.ToggleEntity");
        IPluginCommand call = commands.Single(command => command.Descriptor.CommandName == "HomeAssistant.CallService");
        IPluginCommand show = commands.Single(command => command.Descriptor.CommandName == "HomeAssistant.ShowEntity");
        Check(((IDisplayImageCommand)show).UpdateInterval == TimeSpan.FromSeconds(1), "state display has a one-second polling fallback");
        int callsBeforeShow = server.Calls.Count;
        await show.Execute(Context(host, "sensor.temp"));
        Check(server.Calls.Count == callsBeforeShow, "sensor display never calls a service");

        await toggle.Execute(Context(host, "light.office"));
        await server.WaitForCallsAsync(1, timeout.Token);
        Check(server.Calls[0] == new ServiceCall("homeassistant", "toggle", "light.office"), "toggle call");

        await call.Execute(Context(host, "script", "turn_on", "script.goodnight"));
        await server.WaitForCallsAsync(2, timeout.Token);
        Check(server.Calls[1] == new ServiceCall("script", "turn_on", "script.goodnight"), "generic service call");

        int before = server.Calls.Count;
        await toggle.Execute(Context(host, "Not An Id"));
        Check(server.Calls.Count == before, "invalid entity is not sent");
        Check(host.Recorder.Warnings.Any(warning => warning.Contains("entity_id")), "invalid entity is reported");

        // Trailing display parameters do not change execution: only the entity id is forwarded.
        await toggle.Execute(Context(host, "light.office", "False", "Custom", "mdi:lightbulb"));
        await server.WaitForCallsAsync(before + 1, timeout.Token);
        Check(server.Calls[before] == new ServiceCall("homeassistant", "toggle", "light.office"), "override parameters are execution-neutral");

        await call.Execute(Context(host, "light", "turn on"));
        Check(server.Calls.Count == before + 1, "invalid service is not sent");

        // Rendering reads the cached state and stays synchronous.
        var activeCanvas = new RecordingCanvas();
        bool rendered = ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office"), activeCanvas);
        Check(rendered && activeCanvas.TextDraws == 2 && activeCanvas.Symbols.Contains("lightbulb-on"), "active light renders its domain icon");

        var inactiveCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "switch.kettle"), inactiveCanvas);
        Check(inactiveCanvas.Symbols.Contains("star"), "registry icon overrides the switch domain icon");
        var unavailableCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "switch.unavailable"), unavailableCanvas);
        var unknownStateCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "switch.unknown"), unknownStateCanvas);
        Check(unavailableCanvas.Texts[0].Color != inactiveCanvas.Texts[0].Color &&
              unknownStateCanvas.Texts[0].Color != inactiveCanvas.Texts[0].Color &&
              unknownStateCanvas.Texts[0].Color != unavailableCanvas.Texts[0].Color,
            "unavailable, unknown and off have distinct visual treatments");
        CheckLayout(unavailableCanvas, "unavailable entity layout");
        CheckLayout(unknownStateCanvas, "unknown entity layout");

        var sensorCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)show).RenderImage(Context(host, "sensor.temp"), sensorCanvas);
        Check(sensorCanvas.Symbols.Contains("eye"), "sensor domain icon is resolved");
        Check(sensorCanvas.Texts[0].Text == "21 °C", "sensor state includes its unit");

        var unknownCanvas = new RecordingCanvas();
        Check(((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.ghost"), unknownCanvas) &&
              unknownCanvas.TextDraws == 1, "unknown entity renders a placeholder");

        var emptyCanvas = new RecordingCanvas();
        Check(!((IDisplayImageCommand)toggle).RenderImage(Context(host), emptyCanvas), "missing parameter does not render");

        // Per-button display overrides live in the trailing parameters; legacy one-parameter
        // bindings render identically to before.
        var labelCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "True", "Biuro"), labelCanvas);
        Check(labelCanvas.Texts[1].Text.StartsWith("Biuro", StringComparison.Ordinal), "custom label wins over friendly name");
        Check(labelCanvas.SymbolSize > activeCanvas.SymbolSize, "short label gives the icon more room");

        var sizedCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "True", "Biuro",
            "auto", "16", "18"), sizedCanvas);
        Check(sizedCanvas.Texts[0].FontSize == 16 && sizedCanvas.Texts[1].FontSize == 18,
            "per-button state and label sizes are applied");
        CheckLayout(sizedCanvas, "custom text sizes keep the layout disjoint");
        var sizedLongCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.long_name", "True", "auto",
            "auto", "18", "18"), sizedLongCanvas);
        CheckLayout(sizedLongCanvas, "large text on a two-line label keeps the layout disjoint");

        var iconCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "True", "", "mdi:lightbulb"), iconCanvas);
        Check(iconCanvas.Symbols.Contains("lightbulb"), "custom mdi icon is drawn");
        Check(iconCanvas.FilledCircles == 0 && iconCanvas.OutlinedCircles == 0, "icon replaces the circle indicator");

        var iconOnlyCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "True", "auto",
            "mdi:lightbulb", "11", "13"), iconOnlyCanvas);
        Check(iconOnlyCanvas.Symbols.Contains("lightbulb") && iconOnlyCanvas.Texts[1].Text != "mdi:lightbulb",
            "icon-only override survives host removal of blank label parameter");
        var compactIconCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "True", "mdi:lightbulb"), compactIconCanvas);
        Check(compactIconCanvas.Symbols.Contains("lightbulb"), "legacy icon-only override survives a missing label");

        var entityIconCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.desk"), entityIconCanvas);
        Check(entityIconCanvas.Symbols.Contains("lightbulb-on"), "unsupported HA icon falls back without a dashed placeholder");
        var customEntityIconCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.custom"), customEntityIconCanvas);
        Check(customEntityIconCanvas.Symbols.Contains("flash"), "supported HA icon attribute is used");

        var hiddenCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "False"), hiddenCanvas);
        Check(hiddenCanvas.Symbols.Count == 0 && hiddenCanvas.FilledCircles == 0 && hiddenCanvas.OutlinedCircles == 0,
            "hidden icon removes the indicator zone");
        Check(hiddenCanvas.Texts[0].Top < activeCanvas.Texts[0].Top, "hidden icon moves text up");

        var shorthandCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office", "mdi:flash"), shorthandCanvas);
        Check(shorthandCanvas.Symbols.Contains("flash"), "lone icon value is read as the icon");

        // Layout zones never overlap: indicator, state text and the friendly name each own
        // a disjoint vertical band, even for a long friendly name.
        CheckLayout(activeCanvas, "short layout zones are disjoint");
        var longCanvas = new RecordingCanvas();
        Check(((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.long_name"), longCanvas),
            "long friendly name renders");
        Check(longCanvas.TextDraws == 2, "long friendly name keeps the state text");
        CheckLayout(longCanvas, "long layout zones are disjoint");
        string friendlyName = longCanvas.Texts[1].Text;
        Check(friendlyName.Split('\n').Length <= 2, "friendly name wraps to at most two lines");
        Check(friendlyName.Contains('…'), "long friendly name is ellipsized instead of shrinking the font");

        // A live change is pushed to the entity commands, and the redraw reads the updated cache.
        await server.PublishStateChangeAsync(timeout.Token);
        await UntilAsync(() => Task.FromResult(host.Refreshes.Count > 0), timeout.Token);
        foreach (string name in new[]
                 {
                     "HomeAssistant.ToggleEntity", "HomeAssistant.TurnOnEntity", "HomeAssistant.TurnOffEntity",
                     "HomeAssistant.ActivateScene", "HomeAssistant.RunScript", "HomeAssistant.PressButton",
                     "HomeAssistant.ShowEntity"
                 })
            Check(host.Refreshes.Contains(name), $"entity change refreshes {name}");

        // The first event refreshes immediately instead of waiting for a coalescing window.
        Check(host.Refreshes.Count >= 7, "first event refreshes without delay");

        // A burst folds into a bounded number of refreshes: the first event refreshes
        // immediately and the rest fold into one trailing pass — never one per event. Six events
        // would mean 36 queue entries without coalescing; three back-to-back events all land
        // inside one debounce window, so the burst costs a single pass here.
        await Task.Delay(400, timeout.Token);
        int burstBase = host.Refreshes.Count;
        await server.PublishBurstAsync(timeout.Token);
        await Task.Delay(1000, timeout.Token);
        int burstRefreshes = host.Refreshes.Count - burstBase;
        Check(burstRefreshes == 7, $"burst coalesces into one pass (got {burstRefreshes})");

        var changedCanvas = new RecordingCanvas();
        ((IDisplayImageCommand)toggle).RenderImage(Context(host, "light.office"), changedCanvas);
        Check(changedCanvas.Symbols.Contains("lightbulb-off"),
            "rendering reflects the pushed state");

        plugin.Shutdown();
        Console.WriteLine("Home Assistant command smoke check passed.");
    }

    private static CommandContext Context(RecordingHost host, params string[] parameters) => new()
    {
        Parameters = parameters,
        Target = ButtonTargets.TouchButton,
        Host = host
    };

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition()) await Task.Delay(20, cancellationToken);
    }

    private sealed record ServiceCall(string Domain, string Service, string? EntityId);

    private sealed class ControlServer(HttpListener listener, CancellationToken cancellationToken)
    {
        private int _registryRequests;
        public int RegistryRequests => Volatile.Read(ref _registryRequests);
        private readonly List<ServiceCall> _calls = [];
        private readonly TaskCompletionSource<int> _subscription = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WebSocket? _socket;

        public IReadOnlyList<ServiceCall> Calls
        {
            get { lock (_calls) return _calls.ToArray(); }
        }

        /// <summary>Pushes a live <c>state_changed</c> event to the subscribed plugin.</summary>
        public async Task PublishStateChangeAsync(CancellationToken token) =>
            await PublishChangeAsync("2024-01-02T00:00:00+00:00", "off", token);

        /// <summary>Pushes several events back to back to exercise the trailing refresh coalescing.</summary>
        public async Task PublishBurstAsync(CancellationToken token)
        {
            for (int i = 1; i <= 3; i++)
                await PublishChangeAsync($"2024-01-02T00:00:0{i}+00:00", i % 2 == 0 ? "on" : "off", token);
        }

        private async Task PublishChangeAsync(string updated, string value, CancellationToken token)
        {
            int id = await _subscription.Task.WaitAsync(token);
            WebSocket socket = _socket ?? throw new InvalidOperationException("No subscribed socket.");
            await SendAsync(socket, new
            {
                id, type = "event",
                @event = new
                {
                    event_type = "state_changed",
                    time_fired = updated,
                    data = new
                    {
                        entity_id = "light.office",
                        old_state = State("light.office", "on", "Office Light"),
                        new_state = State("light.office", value, "Office Light", null, updated)
                    }
                }
            });
        }

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

        public async Task WaitForCallsAsync(int count, CancellationToken token)
        {
            while (true)
            {
                lock (_calls) if (_calls.Count >= count) return;
                await Task.Delay(20, token);
            }
        }

        private async Task HandleAsync(WebSocket socket)
        {
            _socket = socket;
            try
            {
                await SendAsync(socket, new { type = "auth_required" });
                using (JsonDocument auth = await ReadAsync(socket))
                {
                    if (auth.RootElement.GetProperty("type").GetString() != "auth") return;
                }
                await SendAsync(socket, new { type = "auth_ok" });

                while (!cancellationToken.IsCancellationRequested)
                {
                    using JsonDocument message = await ReadAsync(socket);
                    int id = message.RootElement.GetProperty("id").GetInt32();
                    switch (message.RootElement.GetProperty("type").GetString())
                    {
                        case "subscribe_events":
                            _subscription.TrySetResult(id);
                            await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                            break;
                        case "get_states":
                            await SendAsync(socket, new { id, type = "result", success = true, result = States() });
                            break;
                        case "config/entity_registry/list":
                            Interlocked.Increment(ref _registryRequests);
                            await SendAsync(socket, new { id, type = "result", success = true, result = new object[]
                            {
                                new { entity_id = "light.office", area_id = "office", device_id = (string?)null,
                                    name = "Office Light", icon = (string?)null, hidden_by = (string?)null,
                                    disabled_by = (string?)null, entity_category = (string?)null },
                                new { entity_id = "sensor.temp", area_id = "garden", device_id = (string?)null,
                                    name = "Temperature", icon = (string?)null, hidden_by = (string?)null,
                                    disabled_by = (string?)null, entity_category = (string?)null },
                                new { entity_id = "switch.kettle", area_id = (string?)null, device_id = "kettle-device",
                                    name = "Kettle", icon = "mdi:star", hidden_by = (string?)null,
                                    disabled_by = (string?)null, entity_category = (string?)null },
                                new { entity_id = "switch.hidden", area_id = "office", device_id = (string?)null,
                                    name = "Hidden", icon = (string?)null, hidden_by = "user",
                                    disabled_by = (string?)null, entity_category = (string?)null }
                            } });
                            break;
                        case "config/device_registry/list":
                            Interlocked.Increment(ref _registryRequests);
                            await SendAsync(socket, new { id, type = "result", success = true,
                                result = new[] { new { id = "kettle-device", area_id = "office" } } });
                            break;
                        case "config/area_registry/list":
                            Interlocked.Increment(ref _registryRequests);
                            await SendAsync(socket, new { id, type = "result", success = true,
                                result = new[] { new { area_id = "office", name = "Office" },
                                    new { area_id = "garden", name = "Garden" } } });
                            break;
                        case "call_service":
                            string domain = message.RootElement.GetProperty("domain").GetString() ?? "";
                            string service = message.RootElement.GetProperty("service").GetString() ?? "";
                            string? entityId = message.RootElement.TryGetProperty("target", out JsonElement target) &&
                                               target.TryGetProperty("entity_id", out JsonElement entity)
                                ? entity.GetString()
                                : null;
                            lock (_calls) _calls.Add(new ServiceCall(domain, service, entityId));
                            await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null });
                            break;
                        default:
                            await SendAsync(socket, new
                            {
                                id, type = "result", success = false,
                                error = new { code = "unsupported", message = "unsupported" }
                            });
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // A closed socket is a normal end of the synthetic session.
            }
        }

        private static object[] States() =>
        [
            State("light.office", "on", "Office Light"),
            State("light.desk", "on", "Desk", "mdi:desk-lamp"),
            State("light.custom", "on", "Custom", "mdi:flash"),
            State("light.long_name", "on", "Bardzo Długa Nazwa Encji Testowej Do Sprawdzenia"),
            State("switch.kettle", "off", "Kettle"),
            State("switch.unavailable", "unavailable", "Unavailable Switch"),
            State("switch.unknown", "unknown", "Unknown Switch"),
            State("switch.hidden", "off", "Hidden"),
            State("scene.movie", "on", "Movie Night"),
            State("script.goodnight", "off", "Goodnight"),
            State("button.doorbell", "unknown", "Doorbell"),
            State("sensor.temp", "21", "Temperature", null, unit: "°C")
        ];

        private static object State(string entityId, string value, string friendlyName) =>
            State(entityId, value, friendlyName, null, "2024-01-01T00:00:00+00:00");

        private static object State(string entityId, string value, string friendlyName, string? icon,
            string updated = "2024-01-01T00:00:00+00:00", string? unit = null)
        {
            var attributes = new Dictionary<string, object> { ["friendly_name"] = friendlyName };
            if (icon is not null) attributes["icon"] = icon;
            if (unit is not null) attributes["unit_of_measurement"] = unit;
            return new
            {
                entity_id = entityId, state = value,
                attributes,
                last_changed = updated,
                last_updated = updated
            };
        }

        private async Task SendAsync(WebSocket socket, object message) =>
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true,
                cancellationToken);

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

    private sealed class RecordingHost : IPluginHost
    {
        private readonly RecordingLogger _logger = new();
        private readonly ConcurrentQueue<string> _refreshes = new();

        public IPluginLogger Logger => _logger;
        public RecordingLogger Recorder => _logger;
        public IReadOnlyList<string> Refreshes => _refreshes.ToArray();
        public IPluginSettings Settings { get; } = new FakeSettings();
        public string CurrentLanguage => "en";
        public string Tr(string english) => english;
        public FolderGridInfo FolderGrid => new(5, 3, 14);
        public DeviceInfo? ActiveDevice => null;
        public bool IsInExclusiveMode => false;
        public void RequestButtonRefresh(string commandName) => _refreshes.Enqueue(commandName);
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

    private sealed class RecordingLogger : IPluginLogger
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IEnumerable<string> Warnings => _warnings;

        public void Info(string message) { }
        public void Warn(string message) => _warnings.Enqueue(message);
        public void Error(string message, Exception? exception = null) => _warnings.Enqueue(message);
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

    /// <summary>Counts the drawing primitives the renderer uses without rasterizing anything.</summary>
    private sealed class RecordingCanvas : IRenderCanvas
    {
        public int Width => 90;
        public int Height => 90;
        public int TextDraws => Texts.Count;
        public int FilledCircles { get; private set; }
        public int OutlinedCircles { get; private set; }
        public List<TextBox> Texts { get; } = [];
        public List<string> Symbols { get; } = [];
        public int SymbolSize { get; private set; }
        public int IndicatorTop { get; private set; }
        public int IndicatorBottom { get; private set; }
        public bool HasIndicator { get; private set; }

        public void DrawText(string text, int x, int y, int width, int height, PluginColor color, float fontSize,
            bool bold = false, bool italic = false, bool centered = true, bool outlined = false,
            PluginColor outlineColor = default) => Texts.Add(new TextBox(text, y, height, fontSize, color));

        public void DrawText(string text, int x, int y, int width, int height, PluginColor color, float fontSize,
            TextHAlign hAlign, TextVAlign vAlign, bool bold = false, bool italic = false, bool outlined = false,
            PluginColor outlineColor = default) => Texts.Add(new TextBox(text, y, height, fontSize, color));

        public void FillCircle(int centerX, int centerY, int radius, PluginColor color)
        {
            FilledCircles++;
            TrackIndicator(centerY, radius);
        }

        public void DrawCircle(int centerX, int centerY, int radius, int strokeWidth, PluginColor color)
        {
            OutlinedCircles++;
            TrackIndicator(centerY, radius + strokeWidth);
        }

        private void TrackIndicator(int centerY, int extent)
        {
            HasIndicator = true;
            IndicatorTop = centerY - extent;
            IndicatorBottom = centerY + extent;
        }

        public void Clear(PluginColor color) { }
        public void FillRectangle(int x, int y, int width, int height, PluginColor color) { }
        public void DrawRectangle(int x, int y, int width, int height, int strokeWidth, PluginColor color) { }
        public void FillRoundedRectangle(int x, int y, int width, int height, int radius, PluginColor color) { }
        public void DrawRoundedRectangle(int x, int y, int width, int height, int radius, int strokeWidth, PluginColor color) { }
        public void FillEllipse(int x, int y, int width, int height, PluginColor color) { }
        public void DrawEllipse(int x, int y, int width, int height, int strokeWidth, PluginColor color) { }
        public void DrawArc(int x, int y, int width, int height, float startAngle, float sweepAngle, int strokeWidth, PluginColor color) { }
        public void FillArc(int x, int y, int width, int height, float startAngle, float sweepAngle, PluginColor color) { }
        public void DrawLine(int x1, int y1, int x2, int y2, int strokeWidth, PluginColor color) { }
        public float MeasureText(string text, float fontSize, bool bold = false, bool italic = false) =>
            text.Sum(character => character == ' ' ? fontSize * 0.3f : fontSize * 0.6f);
        public void DrawSymbol(string symbolId, int x, int y, int width, int height, PluginColor tint)
        {
            Symbols.Add(symbolId);
            SymbolSize = width;
            HasIndicator = true;
            IndicatorTop = y;
            IndicatorBottom = y + height;
        }
        public void DrawSymbol(string symbolId, int x, int y, int width, int height, SymbolStyle style) { }
        public void DrawImage(byte[] imageBytes, int x, int y, int width, int height) { }
        public void DrawImage(byte[] imageBytes, int x, int y, int width, int height, byte opacity, PluginColor tint = default) { }
        public void PushTransform() { }
        public void PopTransform() { }
        public void Translate(float dx, float dy) { }
        public void Rotate(float degrees) { }
        public void Scale(float sx, float sy) { }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception($"Failed: {name}");
    }

    private static void CheckLayout(RecordingCanvas canvas, string name)
    {
        Check(canvas.HasIndicator, $"{name} (indicator)");
        Check(canvas.Texts.Count == 2, $"{name} (state and name texts)");
        Check(canvas.IndicatorBottom <= canvas.Texts[0].Top, $"{name} (indicator above state)");
        Check(canvas.Texts[0].Top + canvas.Texts[0].Height <= canvas.Texts[1].Top,
            $"{name} (state above name)");
        Check(canvas.Texts[1].Top + canvas.Texts[1].Height <= canvas.Height,
            $"{name} (name inside the button)");
    }

    private sealed record TextBox(string Text, int Top, int Height, float FontSize, PluginColor Color);
}
