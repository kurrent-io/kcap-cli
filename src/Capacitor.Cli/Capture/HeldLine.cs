namespace Capacitor.Cli.Capture;

internal sealed record HeldLine {
    public required string SessionId { get; init; }
    public string? AgentId { get; init; }
    public required int LineNumber { get; init; }
    public required string LineSha256 { get; init; }
    public required string Reason { get; init; }
    public required int Attempts { get; init; }
    public required DateTimeOffset NextAttemptAt { get; init; }
}
