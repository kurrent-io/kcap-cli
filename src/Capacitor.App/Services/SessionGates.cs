namespace Capacitor.App.Services;

/// One async gate per session id, so that checking a condition and acting on it run as a single
/// unit: without it a newer operation interleaves between the two and the older one acts on a
/// decision that no longer holds. Gates are refcounted — one exists only while a caller holds or
/// awaits it — so a long-lived app keeps none for the sessions it has finished with.
public sealed class SessionGates {
    readonly Dictionary<string, Gate> _gates = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    sealed class Gate {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Holders;
    }

    /// Waits for the session's gate; disposing the result leaves it. Never call this while holding
    /// the caller's own state lock: the wait lasts as long as the operation ahead of it.
    public async Task<IDisposable> EnterAsync(string sessionId) {
        Gate gate;
        lock (_lock) {
            if (!_gates.TryGetValue(sessionId, out gate!)) {
                gate = new Gate();
                _gates[sessionId] = gate;
            }
            gate.Holders++;
        }
        try {
            await gate.Semaphore.WaitAsync().ConfigureAwait(false);
        } catch {
            Leave(sessionId, gate, entered: false);
            throw;
        }
        return new Exit(this, sessionId, gate);
    }

    void Leave(string sessionId, Gate gate, bool entered) {
        if (entered) gate.Semaphore.Release();
        SemaphoreSlim? spent = null;
        lock (_lock) {
            if (--gate.Holders == 0 && _gates.TryGetValue(sessionId, out var current) && ReferenceEquals(current, gate)) {
                _gates.Remove(sessionId);
                spent = gate.Semaphore;
            }
        }
        spent?.Dispose();
    }

    sealed class Exit(SessionGates gates, string sessionId, Gate gate) : IDisposable {
        int _left;
        public void Dispose() {
            if (Interlocked.Exchange(ref _left, 1) == 0) gates.Leave(sessionId, gate, entered: true);
        }
    }
}
