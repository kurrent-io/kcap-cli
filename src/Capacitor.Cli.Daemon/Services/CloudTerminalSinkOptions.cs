namespace Capacitor.Cli.Daemon.Services;

internal sealed record CloudTerminalSinkOptions {
    // Past the ring's size a replay is cheaper than delivering the backlog.
    public long     BacklogBudgetBytes  { get; init; } = TerminalOutputBuffer.MaxBytes;
    public TimeSpan RetryDelay          { get; init; } = TimeSpan.FromMilliseconds(500);
    public int      FailingSendAttempts { get; init; } = 5;
    public TimeSpan DrainBound          { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan CancellationGrace   { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan WarningInterval     { get; init; } = TimeSpan.FromMinutes(1);
}
