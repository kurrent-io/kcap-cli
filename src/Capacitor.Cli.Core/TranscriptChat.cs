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
        foreach (var evt in result.Events) {
            var projected = TranscriptChat.Project(evt, rules);
            shown.AddRange(projected.Envelopes);
            submitted.AddRange(projected.SubmittedInputs);
        }
        return new(shown, submitted);
    }
}

/// The one registration site in Core: a vendor's chat rules live under Harness/&lt;Vendor&gt;/ and
/// are paired with the leaf's projection here, nowhere else.
public static class TranscriptChat {
    public static readonly IChatTranscriptProjection Journal = new EnvelopeJournalProjection();

    public static TranscriptChatProjection? For(string vendor) =>
        TranscriptProjection.For(vendor) is { } projection && RulesFor(vendor) is { } rules
            ? new TranscriptChatProjection(projection, rules)
            : null;

    public static IChatDisplayRules? RulesFor(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeChatRules.Instance,
        "codex"  => CodexChatRules.Instance,
        _        => null,
    };

    /// The rows for one canonical event, wherever it came from: the envelope mapping, then the
    /// vendor's rules when it has any. Without rules every envelope shows, and a visible user
    /// message is the submitted input.
    public static ChatProjectionResult Project(CanonicalEvent evt, IChatDisplayRules? rules) {
        var envelopes = TranscriptEnvelopes.From(evt);
        if (envelopes.Count == 0) return new([], []);
        var shown = new List<AcpEventEnvelope>(envelopes.Count);
        var submitted = new List<string>();
        foreach (var envelope in envelopes) {
            var kept = rules is null ? envelope : rules.Filter(evt, envelope);
            if (kept is { } visible) shown.Add(visible);
            var text = rules is null
                ? kept is { Kind: AcpEventKind.UserMessage } user ? user.Text : null
                : rules.SubmittedInput(evt, envelope, kept);
            if (text is { Length: > 0 }) submitted.Add(text);
        }
        return new(shown, submitted);
    }
}
