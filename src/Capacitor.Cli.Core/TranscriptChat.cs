using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core;

/// A vendor's say over how its stored events read in the chat: drop one, or rewrite the envelope.
public interface IChatDisplayRules {
    AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope);

    /// Most receipts are visible user turns. Vendors may also record hidden command receipts;
    /// injected prompts and background notifications must never acknowledge submitted input.
    string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed) =>
        displayed is { Kind: AcpEventKind.UserMessage } user ? user.Text : null;
}

/// The chat's view of a transcript: the leaf projection, the envelope mapping, one vendor's rules.
public sealed class TranscriptChatProjection(ITranscriptProjection projection, IChatDisplayRules rules) : IChatTranscriptProjection {
    public TranscriptContext CreateContext(string sessionId, string? agentId) => projection.CreateContext(sessionId, agentId);

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) =>
        ProjectWithInputs(line, lineNumber, receivedAt, context).Envelopes;

    public ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var result = projection.Project(line, lineNumber, receivedAt, context);
        if (result.Events.Count == 0) return new([], []);
        var shown = new List<AcpEventEnvelope>(result.Events.Count);
        var submitted = new List<string>();
        foreach (var evt in result.Events)
            foreach (var envelope in TranscriptEnvelopes.From(evt)) {
                var kept = rules.Filter(evt, envelope);
                if (kept is { } visible) shown.Add(visible);
                if (rules.SubmittedInput(evt, envelope, kept) is { Length: > 0 } text) submitted.Add(text);
            }
        return new(shown, submitted);
    }
}

/// The one registration site in Core: a vendor's chat rules live under Harness/&lt;Vendor&gt;/ and
/// are paired with the leaf's projection here, nowhere else.
public static class TranscriptChat {
    public static readonly IChatTranscriptProjection Journal = new EnvelopeJournalProjection();

    public static TranscriptChatProjection? For(string vendor) =>
        TranscriptProjection.For(vendor) is not { } projection ? null
        : vendor.ToLowerInvariant() switch {
            "claude" => new TranscriptChatProjection(projection, ClaudeChatRules.Instance),
            "codex"  => new TranscriptChatProjection(projection, CodexChatRules.Instance),
            _        => null,
        };
}
