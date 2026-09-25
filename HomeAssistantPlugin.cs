using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using LoupixDeck.Plugin.HomeAssistant.Commands;
using LoupixDeck.Plugin.HomeAssistant.HomeAssistant;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant;

/// <summary>
/// Entry point of the Home Assistant plugin. Owns the plugin lifecycle, the connection
/// settings page and the single active <see cref="HomeAssistantClient"/> plus its
/// <see cref="EntityStore"/>.
/// </summary>
public sealed class HomeAssistantPlugin : LoupixPlugin, IPluginSettingsPage, IMenuContributor
{
    private const string UrlSettingKey = "url";
    private const string TokenSettingKey = "accessToken";
    private const string DefaultUrl = "http://homeassistant.local:8123";

    private static readonly TimeSpan InitialRetryMinDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan InitialRetryMaxDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TestConnectionTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EntityRefreshDebounce = TimeSpan.FromMilliseconds(150);

    private static readonly string[] EntityCommandNames =
    [
        ToggleEntityCommand.Name,
        TurnOnEntityCommand.Name,
        TurnOffEntityCommand.Name,
        ActivateSceneCommand.Name,
        RunScriptCommand.Name,
        PressButtonCommand.Name,
        ShowEntityCommand.Name
    ];

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly CancellationTokenSource _lifetime = new();

    private IPluginHost? _host;
    private HomeAssistantClient? _client;
    private EntityStore? _store;
    private CancellationTokenSource? _session;
    private HomeAssistantConnectionSettings? _desired;
    private Task _lifecycle = Task.CompletedTask;
    private long _generation;
    private long _refreshCoalesced;
    private CancellationTokenSource? _refreshDebounce;
    private bool _shuttingDown;

    private IReadOnlyList<PluginSettingAction>? _settingsActions;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "homeassistant",
        Name = "Home Assistant",
        Version = new Version(0, 1, 0),
        SdkVersion = SdkInfo.Version,
        Author = "Vencite",
        Description = "Control Home Assistant entities directly from LoupixDeck."
    };

    // ───────── IPluginSettingsPage ─────────

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new PluginSettingDescriptor
        {
            Key = UrlSettingKey,
            Label = "Home Assistant URL",
            Kind = PluginSettingKind.Text,
            DefaultValue = DefaultUrl,
            Description = "Base URL of your Home Assistant instance, for example http://homeassistant.local:8123"
        },
        new PluginSettingDescriptor
        {
            Key = TokenSettingKey,
            Label = "Long-Lived Access Token",
            Kind = PluginSettingKind.Password,
            DefaultValue = string.Empty,
            Description = "Home Assistant long-lived access token (Profile → Security → Long-lived access tokens)."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions => _settingsActions ??=
    [
        new PluginSettingAction { Label = "Test Connection", Invoke = TestConnectionAsync }
    ];

    /// <summary>
    /// The host has already written the form values into <see cref="IPluginSettings"/> and saved
    /// them, so this only has to schedule the cancellable async reconfiguration.
    /// </summary>
    public void OnSettingsSaved() =>
        ScheduleSession(ReadConnectionSettings(), "Settings changed; reconnecting.");

    // ───────── lifecycle ─────────

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        ScheduleSession(ReadConnectionSettings(), null);
    }

    /// <summary>
    /// The SDK contract is synchronous. All owned work is cancelled cooperatively and then awaited
    /// by <see cref="ShutdownCoreAsync"/>, so the host never gets an exception out of shutting a
    /// lifecycle down — neither a cancellation nor an <see cref="AggregateException"/>.
    /// </summary>
    public override void Shutdown()
    {
        try
        {
            ShutdownCoreAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Last-resort guard: a failing shutdown must not escape into the host.
            _host?.Logger.Warn($"Home Assistant shutdown failed ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Owns the shutdown order: stop accepting sessions, cancel the lifetime and session, await the
    /// owned lifecycle, stop the published runtime and only then dispose the cancellation sources.
    /// The wait is bounded, and nothing is disposed while the lifecycle may still be running.
    /// </summary>
    private async Task ShutdownCoreAsync()
    {
        CancellationTokenSource? session;
        Task lifecycle;
        lock (_stateSync)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;
            session = _session;
            lifecycle = _lifecycle;
        }

        // Cancelled outside the lock: cancellation callbacks may run synchronously.
        _lifetime.Cancel();
        session?.Cancel();

        using var budget = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            await lifecycle.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while shutting down, unless our own budget is what elapsed.
            if (budget.IsCancellationRequested)
            {
                // The lifecycle still owns the cancellation sources and the runtime, so it is left
                // untouched: no concurrent cleanup, no disposal of resources still in use, and a
                // late publish stays rejected because the plugin is already marked as shutting down.
                _host?.Logger.Warn("Home Assistant connection lifecycle did not stop within the shutdown timeout.");
                return;
            }
        }
        catch (Exception ex)
        {
            // An owned lifecycle that faulted while shutting down is still a stopped lifecycle.
            _host?.Logger.Warn($"Home Assistant connection lifecycle ended with an error ({ex.GetType().Name}).");
        }

        await StopRuntimeAsync().ConfigureAwait(false);

        session?.Dispose();
        _lifetime.Dispose();
        _host?.Logger.Info("Home Assistant runtime stopped.");
    }

    public override IEnumerable<IPluginCommand> GetCommands()
    {
        var access = new HomeAssistantCommandAccess(GetClient, FindEntity);
        return
        [
            new ToggleEntityCommand(access),
            new TurnOnEntityCommand(access),
            new TurnOffEntityCommand(access),
            new ActivateSceneCommand(access),
            new RunScriptCommand(access),
            new PressButtonCommand(access),
            new ShowEntityCommand(access),
            new CallServiceCommand(access)
        ];
    }

    public override IReadOnlyList<CommandGroupDescriptor> GetCommandGroups() =>
    [
        new CommandGroupDescriptor
        {
            Group = "Home Assistant",
            Description = "Home Assistant control",
            Section = CommandGroupSection.Plugins
        }
    ];

    // ───────── IMenuContributor — dynamic entity submenus ─────────

    /// <summary>
    /// Builds the entity submenus from the current cache. Reads only the in-memory snapshot, so the
    /// host's menu timeout is never at risk; a disconnected plugin shows a placeholder instead.
    /// </summary>
    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        EntityStore? store;
        lock (_stateSync) store = _store;

        IReadOnlyList<MenuNode> children = store is null || !store.IsInitialized
            ? [new MenuNode { Name = "Not connected" }]
            : EntityMenu.Build(store.GetSnapshot());

        return Task.FromResult<IReadOnlyList<MenuNode>>(
            [new MenuNode { Name = "Home Assistant", Children = children }]);
    }

    // ───────── runtime access ─────────

    private HomeAssistantClient? GetClient()
    {
        lock (_stateSync) return _client;
    }

    private HomeAssistantState? FindEntity(string entityId)
    {
        EntityStore? store;
        lock (_stateSync) store = _store;
        return store is not null && store.TryGet(entityId, out HomeAssistantState? state) ? state : null;
    }

    /// <summary>
    /// Pushes a refresh to the entity commands after an accepted live change. The first event of a
    /// burst refreshes immediately and the rest fold into one trailing refresh, because the host
    /// repaints every button of a command and a busy Home Assistant emits events far faster than a
    /// button needs to be redrawn; the poll interval remains the safety net.
    /// </summary>
    private void OnEntityChanged(object? sender, HomeAssistantStateChangedEvent change)
    {
        if (_shuttingDown) return;
        if (Interlocked.Increment(ref _refreshCoalesced) == 1) RefreshEntityButtons();
        ArmTrailingRefresh();
    }

    /// <summary>Refreshes after a full snapshot install, so a reconnect never leaves stale buttons
    /// until the safety poll runs.</summary>
    private void OnStoreSynchronized(object? sender, EventArgs args)
    {
        if (_shuttingDown) return;
        Interlocked.Exchange(ref _refreshCoalesced, 0);
        CancelTrailingRefresh(Interlocked.Exchange(ref _refreshDebounce, null));
        RefreshEntityButtons();
    }

    private void ArmTrailingRefresh()
    {
        if (CancelTrailingRefresh(Interlocked.Exchange(ref _refreshDebounce, new CancellationTokenSource())))
            return;
        CancellationTokenSource? current = Volatile.Read(ref _refreshDebounce);
        if (current is null) return;
        _ = DebouncedRefreshAsync(current, current.Token);
    }

    /// <summary>Cancels and disposes a pending trailing refresh. Returns false when the new timer
    /// must still be armed because no newer event replaced it meanwhile.</summary>
    private bool CancelTrailingRefresh(CancellationTokenSource? previous)
    {
        if (previous is null) return false;
        try { previous.Cancel(); }
        catch (ObjectDisposedException) { }
        finally { previous.Dispose(); }
        return !ReferenceEquals(Volatile.Read(ref _refreshDebounce), previous);
    }

    private async Task DebouncedRefreshAsync(CancellationTokenSource owner, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(EntityRefreshDebounce, cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref _refreshCoalesced, 0) > 1) RefreshEntityButtons();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer event, a snapshot refresh or shutdown.
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _refreshDebounce, null, owner), owner))
                owner.Dispose();
        }
    }

    private void RefreshEntityButtons()
    {
        IPluginHost? host = _host;
        if (host is null) return;
        foreach (string commandName in EntityCommandNames)
            host.RequestButtonRefresh(commandName);
    }

    // ───────── settings ─────────

    private HomeAssistantConnectionSettings ReadConnectionSettings()
    {
        IPluginSettings? settings = _host?.Settings;
        string url = (settings?.Get(UrlSettingKey, DefaultUrl) ?? DefaultUrl).Trim();
        string token = (settings?.Get(TokenSettingKey, string.Empty) ?? string.Empty).Trim();
        return new HomeAssistantConnectionSettings(url, token);
    }

    /// <summary>
    /// Replaces the current session: the newest settings win, any pending attempt is cancelled and
    /// an unchanged configuration is left alone. Returns immediately; the work runs on the owned
    /// lifecycle task.
    /// </summary>
    private void ScheduleSession(HomeAssistantConnectionSettings settings, string? reason)
    {
        Task previous;
        lock (_stateSync)
        {
            if (_shuttingDown) return;
            if (_session is not null && settings == _desired) return;

            CancellationTokenSource? replaced = _session;
            replaced?.Cancel();
            CancellationTokenSource session = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            long generation = ++_generation;
            _session = session;
            _desired = settings;
            previous = _lifecycle;
            _lifecycle = RunSessionAsync(previous, replaced, session.Token, generation, settings);
        }

        if (reason is not null) _host?.Logger.Info(reason);
    }

    /// <summary>
    /// One reconfiguration step: stop the previous runtime, then connect and publish the new one.
    /// Steps are serialized, so an older save can never publish a runtime after a newer one.
    /// </summary>
    private async Task RunSessionAsync(Task previous, CancellationTokenSource? replaced, CancellationToken cancellationToken,
        long generation, HomeAssistantConnectionSettings settings)
    {
        bool published = false;
        try
        {
            await previous.ConfigureAwait(false);
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await StopRuntimeAsync().ConfigureAwait(false);
                if (IsSuperseded(generation, cancellationToken)) return;

                if (!settings.IsComplete)
                {
                    _host?.Logger.Info("Home Assistant is not configured.");
                    return;
                }

                published = await StartRuntimeAsync(settings, generation, cancellationToken).ConfigureAwait(false);
            }
            finally { _lifecycleGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer settings or by shutdown; nothing to report.
        }
        catch (Exception ex)
        {
            _host?.Logger.Error("Home Assistant connection lifecycle failed.", ex);
        }
        finally
        {
            lock (_stateSync)
            {
                // An attempt that did not publish a runtime leaves no active session, so saving the
                // same settings again is allowed to retry.
                if (_generation == generation && !published) _session = null;
            }

            // Safe here: the successor task is chained after this one, so nothing uses the
            // replaced token any more.
            replaced?.Dispose();
        }
    }

    /// <summary>
    /// Connects a client and initializes a store for it. The store is published only after its
    /// initialization succeeded and only while this attempt is still the current one. Returns
    /// whether a runtime was published.
    /// </summary>
    private async Task<bool> StartRuntimeAsync(HomeAssistantConnectionSettings settings, long generation,
        CancellationToken cancellationToken)
    {
        IPluginLogger logger = _host!.Logger;
        var client = new HomeAssistantClient(logger);

        try
        {
            await ConnectInitialAsync(client, settings, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (IsSuperseded(generation, cancellationToken))
        {
            await client.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        var store = new EntityStore(client, logger);
        store.EntityChanged += OnEntityChanged;
        store.Synchronized += OnStoreSynchronized;
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            store.EntityChanged -= OnEntityChanged;
            store.Synchronized -= OnStoreSynchronized;
            await store.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // The supersede check and the publish share one lock acquisition, so a shutdown that starts
        // in between cannot leave a runtime behind: either the runtime is published first and the
        // shutdown finds it, or the shutdown wins and the runtime is discarded here.
        bool superseded;
        lock (_stateSync)
        {
            superseded = IsSupersededLocked(generation, cancellationToken);
            if (!superseded)
            {
                _client = client;
                _store = store;
            }
        }

        if (superseded)
        {
            store.EntityChanged -= OnEntityChanged;
            store.Synchronized -= OnStoreSynchronized;
            await store.DisposeAsync().ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        logger.Info($"Entity store initialized with {store.Count} entities.");
        return true;
    }

    /// <summary>
    /// Establishes the very first connection. <see cref="HomeAssistantClient"/> reconnects only
    /// after a connection was established once, so transient startup failures are retried here with
    /// a bounded backoff. Authorization failures and unusable settings stop the retry instead.
    /// </summary>
    private async Task ConnectInitialAsync(HomeAssistantClient client, HomeAssistantConnectionSettings settings,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = InitialRetryMinDelay;
        while (true)
        {
            try
            {
                await client.ConnectAsync(settings.Url, settings.AccessToken, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (HomeAssistantAuthenticationException)
            {
                _host?.Logger.Warn("Home Assistant authentication failed. Check the access token in the plugin settings.");
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                _host?.Logger.Warn($"Initial connection to Home Assistant failed ({ex.GetType().Name}); retrying in {delay.TotalSeconds:0}s.");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(InitialRetryMaxDelay.Ticks, delay.Ticks * 2));
            }
        }
    }

    /// <summary>Tears the active runtime down. Safe to call repeatedly and for partial sessions.</summary>
    private async Task StopRuntimeAsync()
    {
        EntityStore? store;
        HomeAssistantClient? client;
        CancellationTokenSource? debounce;
        lock (_stateSync)
        {
            store = _store;
            client = _client;
            _store = null;
            _client = null;
        }

        debounce = Interlocked.Exchange(ref _refreshDebounce, null);
        if (debounce is not null)
        {
            try { debounce.Cancel(); }
            catch (ObjectDisposedException) { }
            finally { debounce.Dispose(); }
        }

        if (store is not null)
        {
            store.EntityChanged -= OnEntityChanged;
            store.Synchronized -= OnStoreSynchronized;
            try { await store.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _host?.Logger.Warn($"Home Assistant entity store cleanup failed ({ex.GetType().Name})."); }
        }

        if (client is not null)
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _host?.Logger.Warn($"Home Assistant client cleanup failed ({ex.GetType().Name})."); }
        }
    }

    private bool IsSuperseded(long generation, CancellationToken cancellationToken)
    {
        lock (_stateSync) return IsSupersededLocked(generation, cancellationToken);
    }

    // Caller holds _stateSync.
    private bool IsSupersededLocked(long generation, CancellationToken cancellationToken) =>
        _shuttingDown || generation != _generation || cancellationToken.IsCancellationRequested;

    private static bool IsTransient(Exception exception) =>
        exception is TimeoutException or IOException or SocketException or WebSocketException or HttpRequestException;

    // ───────── Test Connection ─────────

    /// <summary>
    /// A read-only connection check against the saved settings, using a temporary client so the
    /// active runtime is not restarted. Never throws: the host displays the returned string.
    /// </summary>
    private async Task<string> TestConnectionAsync()
    {
        if (_host is null) return "Plugin is not initialized.";

        HomeAssistantConnectionSettings settings = ReadConnectionSettings();
        if (string.IsNullOrWhiteSpace(settings.Url)) return "Home Assistant URL is missing.";
        if (string.IsNullOrWhiteSpace(settings.AccessToken)) return "Access token is missing.";

        IPluginLogger logger = _host.Logger;
        var client = new HomeAssistantClient(logger);
        using var timeout = new CancellationTokenSource(TestConnectionTimeout);
        try
        {
            await client.ConnectAsync(settings.Url, settings.AccessToken, timeout.Token).ConfigureAwait(false);
            IReadOnlyList<HomeAssistantState> states = await client.GetStatesAsync(timeout.Token).ConfigureAwait(false);
            return $"Connected successfully. Home Assistant returned {states.Count} entities.";
        }
        catch (HomeAssistantAuthenticationException) { return "Authentication failed."; }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return "Connection timed out."; }
        catch (TimeoutException) { return "Connection timed out."; }
        catch (ArgumentException) { return "Home Assistant URL is invalid."; }
        catch (Exception ex)
        {
            logger.Warn($"Home Assistant connection test failed ({ex.GetType().Name}).");
            return "Could not connect to Home Assistant.";
        }
        finally
        {
            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { logger.Warn($"Home Assistant connection test cleanup failed ({ex.GetType().Name})."); }
        }
    }
}
