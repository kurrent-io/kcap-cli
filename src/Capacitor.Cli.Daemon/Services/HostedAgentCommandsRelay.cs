using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Buffers the most recent slash-command list a runtime has learned and hands it to the
/// orchestrator's callback. The orchestrator attaches the callback after the runtime starts, but an
/// ACP handshake or a Codex skills/list produces the list DURING start — so attaching a callback
/// flushes the buffered list immediately, closing the window between start and attach.</summary>
sealed class HostedAgentCommandsRelay {
    readonly Lock                                       _lock = new();
    IReadOnlyList<HostedAgentCommand>?                  _last;
    Action<IReadOnlyList<HostedAgentCommand>>?          _callback;

    public Action<IReadOnlyList<HostedAgentCommand>>? Callback {
        get { lock (_lock) return _callback; }
        set {
            IReadOnlyList<HostedAgentCommand>? pending;
            lock (_lock) {
                _callback = value;
                pending   = _last;
            }

            if (value is not null && pending is not null) value(pending);
        }
    }

    public void Publish(IReadOnlyList<HostedAgentCommand> commands) {
        Action<IReadOnlyList<HostedAgentCommand>>? callback;
        lock (_lock) {
            _last    = commands;
            callback = _callback;
        }

        callback?.Invoke(commands);
    }
}
