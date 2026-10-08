using Capacitor.Cli.Core.Policy;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Every call a human refused through one interaction bridge, per ACP session: the refusals the
/// judge is told about. The bridge relays every prompt of its runtime and delivers every answer, and
/// it is built with the runtime whose <c>session/new</c> opened the session, so what it holds spans
/// the run. Anything it could not describe makes the declaration incomplete rather than shorter,
/// because the judge must not allow while a refusal may be missing from the list.
/// </summary>
internal sealed class AcpRefusalLedger {
    const int MaxRefusals  = 32;
    const int MaxReference = 128;
    const int MaxTool      = 256;
    const int MaxTarget    = 1024;

    sealed class Session {
        public readonly List<PolicyJudgeDeclaredRefusalV1> Entries = [];
        public bool Incomplete;
    }

    readonly Lock _gate = new();
    readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    /// <param name="action">Null when the call could not be normalized: the refusal happened but
    /// cannot be named, which costs the declaration its completeness.</param>
    public void Record(string sessionId, string? toolCallId, string? tool, CanonicalAction? action) {
        lock (_gate) {
            var session = For(sessionId);
            if (toolCallId is not { Length: > 0 and <= MaxReference } || action is null) {
                session.Incomplete = true;
                return;
            }

            // An unnamed refusal is still declared, but the judge cannot match it reliably.
            var name = tool is { Length: > 0 } t ? t : "unknown";
            if (tool is not { Length: > 0 }) session.Incomplete = true;

            var target = PolicyJudgeTarget.Of(action);
            if (name.Length > MaxTool) { name = name[..MaxTool]; session.Incomplete = true; }
            if (target.Length > MaxTarget) { target = target[..MaxTarget]; session.Incomplete = true; }

            session.Entries.Add(new(toolCallId, name, target, PromptId: null));
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
                return new(true, PolicyJudgeRefusalsV1.SourceBridge, []);

            var entries = session.Entries.ToArray();
            Array.Reverse(entries);
            return new(!session.Incomplete, PolicyJudgeRefusalsV1.SourceBridge, entries);
        }
    }

    Session For(string sessionId) {
        if (!_sessions.TryGetValue(sessionId, out var session)) _sessions[sessionId] = session = new();
        return session;
    }
}
