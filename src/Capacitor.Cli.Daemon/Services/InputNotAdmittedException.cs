namespace Capacitor.Cli.Daemon.Services;

/// A runtime declined to queue or write the text: its pending-turn queue is full or it is terminal.
internal sealed class InputNotAdmittedException(string message) : InvalidOperationException(message);
