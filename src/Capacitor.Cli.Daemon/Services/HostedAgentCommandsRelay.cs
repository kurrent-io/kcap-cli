using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Buffers the most recent slash-command list a runtime has learned and hands it to the
/// orchestrator's callback. The orchestrator attaches the callback after the runtime starts, but an
/// ACP handshake or a Codex skills/list produces the list DURING start — so attaching a callback
/// flushes the buffered list immediately, closing the window between start and attach.</summary>
sealed class HostedAgentCommandsRelay {
    readonly Lock                                       _lock = new();
    IReadOnlyList<HostedAgentCommand>?                  _last;
    long                                                _version;
    Action<IReadOnlyList<HostedAgentCommand>>?          _callback;

    public Action<IReadOnlyList<HostedAgentCommand>>? Callback {
        get { lock (_lock) return _callback; }
        set {
            IReadOnlyList<HostedAgentCommand>? pending;
            long                               version;
            lock (_lock) {
                _callback = value;
                pending   = _last;
                version   = _version;
            }

            if (value is not null && pending is not null) Deliver(value, pending, version);
        }
    }

    public void Publish(IReadOnlyList<HostedAgentCommand> commands) {
        Action<IReadOnlyList<HostedAgentCommand>>? callback;
        long                                       version;
        lock (_lock) {
            _last    = commands;
            version  = ++_version;
            callback = _callback;
        }

        if (callback is not null) Deliver(callback, commands, version);
    }

    // Callbacks are invoked OUTSIDE the lock (they run arbitrary work — a server report), so a slow
    // attach-time flush could otherwise land after a newer Publish and overwrite it. Deliver only when
    // this list is still the newest one seen, so a stale snapshot can never win the last write; the
    // newest may be delivered more than once, which is harmless (the same list, set idempotently).
    void Deliver(Action<IReadOnlyList<HostedAgentCommand>> callback, IReadOnlyList<HostedAgentCommand> commands, long version) {
        lock (_lock) {
            if (version != _version) return;
        }

        callback(commands);
    }
}
