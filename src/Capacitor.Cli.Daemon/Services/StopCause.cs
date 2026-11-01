namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Why the daemon is stopping, as named by whoever asked first, for the line it exits with. A stop
/// that sets off others — a signal arriving during a local stop — keeps the cause that started it.
/// Registered as a singleton.
/// </summary>
public sealed class StopCause {
    string? _reason;

    public string? Reason => Volatile.Read(ref _reason);

    public void Note(string reason) => Interlocked.CompareExchange(ref _reason, reason, null);
}
