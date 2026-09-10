namespace Capacitor.Cli.Core;

/// What the chat tab needs from any transcript reader: a per-file context and a line-to-envelopes step.
public interface IChatTranscriptProjection {
    TranscriptContext CreateContext(string sessionId, string? agentId);
    IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context);
}
