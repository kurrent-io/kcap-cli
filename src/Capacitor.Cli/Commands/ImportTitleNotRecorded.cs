namespace Capacitor.Cli.Commands;

/// <summary>A harness-native title the server never accepted — the session stayed not-found past the
/// retry budget, or the post was refused/failed outright.</summary>
public sealed record ImportTitleNotRecorded(string SessionId, string? AgentId, string Reason)
    : ImportWarning(SessionId, AgentId) {
    public override string Message => $"Title not recorded for {SessionId}: {Reason}";
}
