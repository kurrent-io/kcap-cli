namespace Capacitor.Cli.Daemon.Services;

/// One consumer of an agent's PTY output. The fan-out calls TryEnqueue (non-blocking);
/// each sink drains itself on its own loop so one slow sink can't stall the others.
internal interface ITerminalSink {
    void TryEnqueue(byte[] chunk);
    bool Detached { get; }
}
