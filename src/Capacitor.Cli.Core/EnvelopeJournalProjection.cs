namespace Capacitor.Cli.Core;

/// Journal lines are already envelopes: no vendor rules, no dedupe, one envelope per valid line.
public sealed class EnvelopeJournalProjection : IChatTranscriptProjection {
    sealed class StatelessContext : TranscriptContext;

    public TranscriptContext CreateContext(string sessionId, string? agentId) => new StatelessContext();

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) =>
        EnvelopeJournalFormat.TryRead(line, out var envelope)
            ? [envelope]
            : throw new FormatException($"line {lineNumber} is not a v{EnvelopeJournalFormat.SupportedContractVersion} envelope");
}
