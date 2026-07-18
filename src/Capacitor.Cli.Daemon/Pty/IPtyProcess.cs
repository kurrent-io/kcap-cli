namespace Capacitor.Cli.Daemon.Pty;

public interface IPtyProcess : IAsyncDisposable
{
    int  Pid       { get; }
    bool HasExited { get; }
    int? ExitCode  { get; }

    /// <summary>The start-identity token captured NATIVELY, inside the spawn call, immediately
    /// after the child exists (the capture-binding rule) — <c>null</c> when this runtime never
    /// captures one this way (Windows; ACP runtimes have no PTY at all), in which case the caller
    /// falls back to a legacy post-hoc <c>ProcessStartToken.ForPid</c> re-capture. On Unix this is
    /// NEVER null: it's either a real token or <c>""</c> (capture attempted and failed — an
    /// identity-unavailable record), and the caller must NOT re-capture in that case (that would
    /// defeat the whole point of capturing pre-reap).</summary>
    string? StartIdentity => null;

    IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default);
    Task WriteAsync(string input);
    Task WriteAsync(byte[] data);
    void Resize(ushort cols, ushort rows);
    void SendInterrupt();
    Task TerminateAsync(TimeSpan? timeout = null);
    Task WaitForExitAsync(TimeSpan? timeout = null);
}
