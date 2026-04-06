namespace ClaudeCode.Core.State;

/// <summary>
/// Observable state store for <see cref="AppState"/>.
/// Implements a Zustand-like pattern: state is immutable; mutations are applied
/// atomically via <see cref="Update"/>, and all registered listeners are notified
/// after each successful transition.
/// </summary>
/// <remarks>
/// Thread safety: all reads and writes to <see cref="_state"/> and <see cref="_listeners"/>
/// are guarded by <see cref="_lock"/>. Listener callbacks are invoked outside the lock
/// to prevent deadlocks if a listener calls back into the store.
/// </remarks>
public sealed class AppStateStore
{
    private AppState _state;
    private readonly object _lock = new();
    private readonly List<Action> _listeners = [];

    /// <summary>
    /// Initialises the store with an optional starting state.
    /// When <paramref name="initialState"/> is <see langword="null"/> a default
    /// <see cref="AppState"/> is used.
    /// </summary>
    /// <param name="initialState">The initial application state, or <see langword="null"/> for defaults.</param>
    public AppStateStore(AppState? initialState = null)
    {
        _state = initialState ?? new AppState();
    }

    /// <summary>Returns a consistent snapshot of the current application state.</summary>
    public AppState GetState()
    {
        lock (_lock) return _state;
    }

    /// <summary>
    /// Applies <paramref name="updater"/> to the current state, replaces it with the
    /// returned value, and notifies all subscribers — unless the updater returns the
    /// same reference (identity equality), in which case no notification is emitted.
    /// </summary>
    /// <param name="updater">
    /// A pure function that receives the current state and returns the next state.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="updater"/> is <see langword="null"/>.</exception>
    public void Update(Func<AppState, AppState> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);

        List<Action> snapshot;
        lock (_lock)
        {
            var prev = _state;
            var next = updater(prev);
            if (ReferenceEquals(prev, next)) return;
            _state = next;
            snapshot = [.. _listeners];
        }

        foreach (var listener in snapshot)
            listener();
    }

    /// <summary>
    /// Registers <paramref name="listener"/> to be called after every successful
    /// state transition. Dispose the returned handle to unsubscribe.
    /// </summary>
    /// <param name="listener">The callback to invoke on state changes. Must not be <see langword="null"/>.</param>
    /// <returns>A disposable subscription handle. Disposing it removes the listener.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    public IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        lock (_lock) _listeners.Add(listener);
        return new Subscription(() => { lock (_lock) _listeners.Remove(listener); });
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Guard against double-dispose: only invoke the callback once.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                onDispose();
        }
    }
}
