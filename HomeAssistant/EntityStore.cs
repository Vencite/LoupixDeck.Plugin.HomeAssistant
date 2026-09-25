using System.Collections.Concurrent;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.HomeAssistant.HomeAssistant;

/// <summary>Thread-safe, read-only cache of Home Assistant entity states.</summary>
public sealed class EntityStore(HomeAssistantClient client, IPluginLogger logger) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _synchronizationGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private ConcurrentDictionary<string, HomeAssistantState> _states = new(StringComparer.Ordinal);
    private Dictionary<string, DateTimeOffset> _removedAt = new(StringComparer.Ordinal);
    private readonly List<HomeAssistantStateChangedEvent> _buffer = [];
    private Task? _resyncTask;
    private long _generation;
    private bool _attached;
    private bool _buffering;
    private bool _initialized;
    private bool _disposed;

    /// <summary>Raised for an accepted live change. A full snapshot installation raises Synchronized instead.</summary>
    public event EventHandler<HomeAssistantStateChangedEvent>? EntityChanged;

    public event EventHandler? Synchronized;

    public bool IsInitialized
    {
        get { lock (_sync) return _initialized; }
    }

    public int Count => Volatile.Read(ref _states).Count;

    public bool TryGet(string entityId, out HomeAssistantState? state) =>
        Volatile.Read(ref _states).TryGetValue(entityId, out state);

    public IReadOnlyList<HomeAssistantState> GetSnapshot() => Volatile.Read(ref _states).Values.ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _synchronizationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        bool synchronized;
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_initialized) return;
                if (!_attached)
                {
                    client.StateChanged += OnStateChanged;
                    client.ConnectionRestored += OnConnectionRestored;
                    _attached = true;
                }
            }
            synchronized = await SynchronizeFromHomeAssistantAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _synchronizationGate.Release(); }
        if (synchronized) Notify(Synchronized);
    }

    private async Task<bool> SynchronizeFromHomeAssistantAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            long generation;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_initialized) return false;
                if (!_buffering) BeginBuffering();
                generation = _generation;
            }

            try
            {
                await client.SubscribeStateChangesAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<HomeAssistantState> snapshot = await client.GetStatesAsync(cancellationToken).ConfigureAwait(false);
                var replacement = new ConcurrentDictionary<string, HomeAssistantState>(StringComparer.Ordinal);
                foreach (HomeAssistantState state in snapshot)
                    replacement[state.EntityId] = state;
                var removedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

                lock (_sync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (generation != _generation) continue; // A newer WebSocket session owns the next snapshot.
                    foreach (HomeAssistantStateChangedEvent change in _buffer)
                        ApplyChange(replacement, removedAt, change);
                    _removedAt = removedAt;
                    Volatile.Write(ref _states, replacement);
                    _buffer.Clear();
                    _buffering = false;
                    _initialized = true;
                }
                return true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested && HasNewGeneration(generation))
            {
                // A reconnect invalidated this request; retry against the new session.
            }
            catch
            {
                lock (_sync)
                {
                    if (generation == _generation)
                    {
                        _buffer.Clear();
                        _buffering = false;
                        _initialized = false;
                    }
                }
                throw;
            }
        }
    }

    private bool HasNewGeneration(long generation)
    {
        lock (_sync) return generation != _generation;
    }

    // Caller holds _sync. Published reads see either the old cache or an empty cache until resync finishes.
    private void BeginBuffering()
    {
        _generation++;
        _buffer.Clear();
        _removedAt.Clear();
        Volatile.Write(ref _states, new ConcurrentDictionary<string, HomeAssistantState>(StringComparer.Ordinal));
        _initialized = false;
        _buffering = true;
    }

    private void OnStateChanged(object? sender, HomeAssistantStateChangedEvent change)
    {
        bool applied;
        lock (_sync)
        {
            if (_disposed) return;
            if (_buffering)
            {
                _buffer.Add(change);
                return;
            }
            if (!_initialized) return;
            applied = ApplyChange(_states, _removedAt, change);
        }
        if (applied) Notify(EntityChanged, change);
    }

    private void OnConnectionRestored(object? sender, EventArgs args)
    {
        lock (_sync)
        {
            if (_disposed) return;
            BeginBuffering(); // Synchronous: events cannot slip between restoration and buffering.
            if (_resyncTask is null)
                _resyncTask = Task.Run(ResynchronizeAsync);
        }
    }

    private async Task ResynchronizeAsync()
    {
        while (true)
        {
            long generation;
            lock (_sync) generation = _generation;
            try
            {
                await _synchronizationGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                bool synchronized;
                try { synchronized = await SynchronizeFromHomeAssistantAsync(_stop.Token).ConfigureAwait(false); }
                finally { _synchronizationGate.Release(); }
                if (synchronized) Notify(Synchronized);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { logger.Warn($"Home Assistant entity resync failed ({ex.GetType().Name})."); }

            lock (_sync)
            {
                if (!_disposed && generation != _generation) continue;
                _resyncTask = null;
                return;
            }
        }
    }

    // Caller holds _sync, or is building an unpublished replacement dictionary.
    private static bool ApplyChange(ConcurrentDictionary<string, HomeAssistantState> states,
        Dictionary<string, DateTimeOffset> removedAt, HomeAssistantStateChangedEvent change)
    {
        string id = change.EntityId;
        if (change.NewState is { } next)
        {
            if (removedAt.TryGetValue(id, out DateTimeOffset removed) && next.LastUpdated <= removed)
                return false;
            if (states.TryGetValue(id, out HomeAssistantState? current) && next.LastUpdated <= current.LastUpdated)
                return false;
            states[id] = next;
            removedAt.Remove(id);
            return true;
        }
        if (change.OldState is null ||
            (states.TryGetValue(id, out HomeAssistantState? existing) && change.TimeFired <= existing.LastUpdated) ||
            (removedAt.TryGetValue(id, out DateTimeOffset previous) && change.TimeFired <= previous))
            return false;

        removedAt[id] = change.TimeFired;
        return states.TryRemove(id, out _);
    }

    private void Notify(EventHandler? handlers)
    {
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { logger.Warn($"Home Assistant store callback failed ({ex.GetType().Name})."); }
        }
    }

    private void Notify(EventHandler<HomeAssistantStateChangedEvent>? handlers, HomeAssistantStateChangedEvent change)
    {
        if (handlers is null) return;
        foreach (EventHandler<HomeAssistantStateChangedEvent> handler in handlers.GetInvocationList().Cast<EventHandler<HomeAssistantStateChangedEvent>>())
        {
            try { handler(this, change); }
            catch (Exception ex) { logger.Warn($"Home Assistant store callback failed ({ex.GetType().Name})."); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? resync;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_attached)
            {
                client.StateChanged -= OnStateChanged;
                client.ConnectionRestored -= OnConnectionRestored;
            }
            _buffer.Clear();
            _initialized = false;
            _buffering = false;
            Volatile.Write(ref _states, new ConcurrentDictionary<string, HomeAssistantState>(StringComparer.Ordinal));
            resync = _resyncTask;
        }
        _stop.Cancel();
        if (resync is not null) await resync.ConfigureAwait(false);
        await _synchronizationGate.WaitAsync().ConfigureAwait(false);
        _synchronizationGate.Release();
        _synchronizationGate.Dispose();
        _stop.Dispose();
    }
}
