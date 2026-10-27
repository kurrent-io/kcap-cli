using System.Globalization;

namespace Capacitor.Cli.Core;

/// Reads the daemon's ACP envelope journal, one envelope per line.
public sealed class EnvelopeJournalProjection : IChatTranscriptProjection {
    sealed class StatelessContext : TranscriptContext;

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new StatelessContext();

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) =>
        EnvelopeJournalFormat.TryRead(line, out var envelope)
            ? [envelope]
            // No line number in the message: the reader logs one failure per distinct message, so a
            // per-line message would log a corrupt journal once per line.
            : throw new FormatException($"not a v{EnvelopeJournalFormat.SupportedContractVersion} envelope");

    /// The runtime journals its subagents' start and end as it sees them, the records the server writes the same rows from.
    public ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var envelopes = Project(line, lineNumber, receivedAt, context);
        return new(envelopes,
            [.. envelopes.Where(e => e.Kind == AcpEventKind.UserMessage && e.Text is not null).Select(e => e.Text!)],
            [.. envelopes.SelectMany(e => Runs(e, receivedAt))]);
    }

    static IEnumerable<RunSignal> Runs(AcpEventEnvelope envelope, DateTimeOffset receivedAt) => envelope switch {
        { Kind: AcpEventKind.SubagentStarted, AgentId: { Length: > 0 } agentId } =>
            TranscriptChat.SubagentStart(agentId, envelope.AgentType, At(envelope, receivedAt), rules: null),
        { Kind: AcpEventKind.SubagentCompleted, AgentId: { Length: > 0 } agentId } =>
            [new RunSignal.Finished(null, agentId, null, At(envelope, receivedAt))],
        _ => [],
    };

    static DateTimeOffset At(AcpEventEnvelope envelope, DateTimeOffset receivedAt) =>
        envelope.TimestampIso is { } iso && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at : receivedAt;
}
