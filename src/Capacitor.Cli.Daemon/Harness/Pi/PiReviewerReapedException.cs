namespace Capacitor.Cli.Daemon.Harness.Pi;

/// <summary>A reviewer launch that one of the runtime's guards ended before the launch returned. The
/// message is the guard's coded reason, which the orchestrator forwards as the launch failure.</summary>
internal sealed class PiReviewerReapedException(string message, Exception? inner)
    : InvalidOperationException(message, inner);
