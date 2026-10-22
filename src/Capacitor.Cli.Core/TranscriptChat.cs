using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core;

/// A vendor's say over how its stored events read in the chat: drop one, or rewrite the envelope.
public interface IChatDisplayRules {
    AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope);

    /// Most receipts are visible user turns. Vendors may also record hidden command receipts;
    /// injected prompts and background notifications must never acknowledge submitted input.
    string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed) =>
        displayed is { Kind: AcpEventKind.UserMessage } user ? user.Text : null;

    /// Possible literal user text before ambiguous wrapper cleanup. These are not receipts until
    /// the caller proves a matching send; trusted meta, sidechain and peer records must yield none.
    IReadOnlyList<string> LiteralInputs(CanonicalEvent evt, AcpEventEnvelope raw) => [];

    /// Subagent facts read off the raw envelope, whatever Filter decides for it.
    IReadOnlyList<RunSignal> Runs(CanonicalEvent evt, AcpEventEnvelope raw) => [];

    /// True when the vendor's own records start its subagent rows, so a start record only names one of them.
    bool StartsSubagentRows => false;
}

/// The chat's view of a transcript: the leaf projection, the envelope mapping, one vendor's rules.
public sealed class TranscriptChatProjection(ITranscriptProjection projection, IChatDisplayRules rules) : IChatTranscriptProjection {
    public TranscriptContext CreateContext(string sessionId, string? agentId) => projection.CreateContext(sessionId, agentId);

    public IReadOnlyList<AcpEventEnvelope> Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) =>
        ProjectWithInputs(line, lineNumber, receivedAt, context).Envelopes;

    public ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var result = projection.Project(line, lineNumber, receivedAt, context);
        if (result.Events.Count == 0) return new([], [], []);
        return ChatProjectionResult.Combine(result.Events.Select(evt => TranscriptChat.Project(evt, rules)).ToArray());
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

    /// The chat's view of one subagent's own transcript, in which nothing is hidden for being a sidechain.
    public static TranscriptChatProjection? ForSubagent(string vendor) =>
        TranscriptProjection.For(vendor) is { } projection && SubagentRulesFor(vendor) is { } rules
            ? new TranscriptChatProjection(projection, rules)
            : null;

    public static IChatDisplayRules? SubagentRulesFor(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeChatRules.Subagent,
        "codex"  => CodexChatRules.Subagent,
        _        => RulesFor(vendor),
    };

    public static IChatDisplayRules? RulesFor(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeChatRules.Instance,
        "codex"  => CodexChatRules.Instance,
        _        => null,
    };

    /// The rows for one canonical event, wherever it came from: the envelope mapping, then the
    /// vendor's rules when it has any. Without rules every envelope shows, and a visible user
    /// message is the submitted input.
    public static ChatProjectionResult Project(CanonicalEvent evt, IChatDisplayRules? rules) {
        // Written by the server from the vendor's subagent-stop hook: it says the subagent ended,
        // never how, and carries no call id.
        if (evt.Payload is SubagentCompleted { AgentId.Length: > 0 } completed)
            return new([], [], [new RunSignal.Finished(null, completed.AgentId, null, evt.Timestamp)]);
        // Written from the subagent-start hook, which names the agent but not its call.
        if (evt.Payload is SubagentStarted started)
            return new([], [], started.AgentId is { Length: > 0 } agentId ? SubagentStart(agentId, started.AgentType, evt.Timestamp, rules) : []);
        var envelopes = TranscriptEnvelopes.From(evt);
        if (envelopes.Count == 0) return new([], [], []);
        var parts = new List<ChatProjectionResult>(envelopes.Count);
        foreach (var envelope in envelopes) {
            var kept = rules is null ? envelope : rules.Filter(evt, envelope);
            var text = rules is null
                ? kept is { Kind: AcpEventKind.UserMessage } user ? user.Text : null
                : rules.SubmittedInput(evt, envelope, kept);
            var literal = envelope.Kind == AcpEventKind.UserMessage ? rules?.LiteralInputs(evt, envelope) ?? [] : [];
            if (kept == envelope && literal.All(candidate => candidate == envelope.Text)) literal = [];
            parts.Add(new(kept is { } visible ? [visible] : [], text is { Length: > 0 } ? [text] : [], rules?.Runs(evt, envelope) ?? []) {
                OriginalInput = literal.Count > 0 ? envelope : null,
                LiteralInputs = literal,
            });
        }
        return ChatProjectionResult.Combine(parts);
    }

    /// A subagent's start record. Where the vendor's own records start no row, it starts one keyed by the agent, so the
    /// agent's completion finds it; otherwise it only names the row the vendor's call started.
    public static IReadOnlyList<RunSignal> SubagentStart(string agentId, string? agentType, DateTimeOffset at, IChatDisplayRules? rules) {
        if (rules?.StartsSubagentRows == true) return [new RunSignal.Identified(null, agentId)];

        // Keyed by the start too: an id can start a second run, and the record's own time keeps a replay one row.
        var key = $"agent:{agentId}:{at.UtcTicks}";
        return [new RunSignal.Started(key, agentType is { Length: > 0 } type ? type : "agent", "", at), new RunSignal.Identified(key, agentId)];
    }
}
