using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Every call refused through one permission bridge, per session: the refusals the judge is told
/// about. Anything it could not describe makes the declaration incomplete rather than shorter, because
/// the judge must not allow while a refusal may be missing from the list. So does a session the bridge
/// did not see from its start: a resumed, rebound or adopted session may carry refusals from before,
/// and the ledger lives only as long as its daemon process.
/// </summary>
internal sealed class PermissionRefusalLedger {
    const int MaxRefusals  = 32;
    const int MaxReference = 128;
    const int MaxTool      = 256;
    const int MaxTarget    = 1024;

    sealed class Session {
        public readonly List<PolicyJudgeDeclaredRefusalV1> Entries = [];
        public readonly HashSet<string> Unkeyed = new(StringComparer.Ordinal);
        public bool BridgedSinceStart;
        public bool Incomplete;
    }

    readonly Lock _gate = new();
    readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    bool _lostRequest;

    /// <summary>A request this bridge failed on before it knew the session, which its hook then denied: any session
    /// may now hold a refusal no entry records.</summary>
    public void MarkAllIncomplete() {
        lock (_gate) _lostRequest = true;
    }

    /// <summary>A refusal in the session that could not be described, such as one no agent could be attributed for.</summary>
    public void MarkIncomplete(string sessionId) {
        lock (_gate) For(sessionId).Incomplete = true;
    }

    /// <summary>Called once, where the bridge starts the session's runtime itself — never on a resume.</summary>
    public void MarkBridgedSinceStart(string sessionId) {
        lock (_gate) For(sessionId).BridgedSinceStart = true;
    }

    /// <param name="approvalId">Tells apart two approvals one call raised; without it a second refusal of the
    /// same call cannot be told from a repeat of the first.</param>
    /// <param name="action">Null when the call could not be normalized: the refusal happened but
    /// cannot be named, which costs the declaration its completeness.</param>
    /// <param name="promptId">The turn the refused request belonged to, when the lane knows it.</param>
    public void Record(string sessionId, string? toolCallId, string? approvalId, string? tool, CanonicalAction? action, string? promptId) {
        lock (_gate) {
            var session = For(sessionId);
            if (toolCallId is not { Length: > 0 and <= MaxReference } || action is null) {
                session.Incomplete = true;
                return;
            }

            // Past MaxRefusals the declaration is incomplete anyway, so the repeat check needs no more ids than that.
            if (string.IsNullOrEmpty(approvalId) && session.Unkeyed.Count < MaxRefusals && !session.Unkeyed.Add(toolCallId))
                session.Incomplete = true;

            // An unnamed refusal is still declared, but the judge cannot match it reliably.
            var unnamed = string.IsNullOrWhiteSpace(tool);
            var name    = unnamed ? "unknown" : tool!;
            if (unnamed) session.Incomplete = true;

            var target = PolicyJudgeTarget.Of(action);
            if (name.Length > MaxTool) { name = name[..MaxTool]; session.Incomplete = true; }
            if (target.Length > MaxTarget) { target = target[..MaxTarget]; session.Incomplete = true; }

            session.Entries.Add(new(toolCallId, name, target, promptId));
            if (session.Entries.Count > MaxRefusals) {
                session.Entries.RemoveAt(0);
                session.Incomplete = true;
            }
        }
    }

    /// <summary>Newest first, as the judge expects.</summary>
    public PolicyJudgeRefusalsV1 Declare(string sessionId) {
        lock (_gate) {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return new(false, PolicyJudgeRefusalsV1.SourceBridge, []);

            var entries = session.Entries.ToArray();
            Array.Reverse(entries);
            return new(session.BridgedSinceStart && !session.Incomplete && !_lostRequest, PolicyJudgeRefusalsV1.SourceBridge, entries);
        }
    }

    Session For(string sessionId) {
        if (!_sessions.TryGetValue(sessionId, out var session)) _sessions[sessionId] = session = new();
        return session;
    }
}
