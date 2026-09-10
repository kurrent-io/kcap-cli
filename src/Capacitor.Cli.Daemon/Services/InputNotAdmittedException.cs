namespace Capacitor.Cli.Daemon.Services;

/// A runtime declined to queue or write the text: its pending-turn queue is full or it is terminal.
/// Both callers of the delivery core map this to the `queue_full` reason.
internal sealed class InputNotAdmittedException(string message) : InvalidOperationException(message);
