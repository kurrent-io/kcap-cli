namespace Capacitor.Cli.Daemon.Harness.Claude;

using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// The Claude half of the hosted permission seam: maps the vendor's tool call onto the policy
/// vocabulary, evaluates it against the launch-bound snapshot, consults the judge for a call no rule
/// decided, and builds the decision event. Null when nothing decided and nothing was consulted —
/// that request belongs to the human lane and nothing is recorded against it.
/// </summary>
internal static class ClaudeHostedPolicySeam {
    /// <summary>The agent is already blocked on a prompt a human may take minutes to answer, so the
    /// judge gets the server's full ceiling rather than a hook's share.</summary>
    internal static readonly TimeSpan JudgeBudget = TimeSpan.FromSeconds(5);

    /// <param name="judgeState">Where the transcript read keeps its per-session cursor; null reads
    /// the transcript from the top on every consultation.</param>
    internal static async Task<ClaudeHostedPolicyResult?> EvaluateAsync(
            ClaudeHostedPermissionCall call, PolicySnapshot snapshot, TimeProvider time,
            PolicyJudgeGateway? judge = null, ConfigRoot? judgeState = null, TimeSpan? judgeBudget = null,
            CancellationToken ct = default) {
        var action     = ClaudeActionNormalizer.Normalize(call.ToolName, call.ToolInput, call.Cwd);
        var evaluation = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);

        // An ask raises the prompt it would have asked for anyway, so what it effects is a park.
        switch (evaluation.Outcome) {
            case PolicyOutcome.Allow: return Result(PolicyOutcome.Allow, "allow", "allow", null);
            case PolicyOutcome.Deny:  return Result(PolicyOutcome.Deny,  "deny",  "deny",  null);
            case PolicyOutcome.Ask:   return Result(PolicyOutcome.Ask,   "ask",   "parked", null);
        }

        if (judge is null || !snapshot.JudgeEnabled) return null;

        var judged = await ConsultAsync(call, snapshot, action, time, judge, judgeState, judgeBudget ?? JudgeBudget, ct);
        return judged.Outcome switch {
            PolicyOutcome.Allow => Result(PolicyOutcome.Allow, "allow", "allow", judged),
            PolicyOutcome.Deny  => Result(PolicyOutcome.Deny,  "deny",  "deny",  judged),
            PolicyOutcome.Ask   => Result(PolicyOutcome.Ask,   "ask",   "parked", judged),
            _ => Result(PolicyOutcome.None, "pass_through", "pass_through", judged),
        };

        // One evaluation per raised prompt, so there is nothing to correlate a decision against and
        // nothing ambiguous about which call it answers.
        ClaudeHostedPolicyResult Result(PolicyOutcome outcome, string requested, string effective, PolicyJudgeResult? j) =>
            new(outcome, PolicyWire.Decision(
                sessionId: call.SessionId, agentId: call.AgentId, vendor: "claude", seam: PolicySeams.HostedClaudePermission,
                snapshot: snapshot, mode: EvaluationMode.Full, requestedOutcome: requested, effectiveOutcome: effective,
                action: PolicyWire.ToWire(action), matchedRules: PolicyWire.ToWire(evaluation.MatchedRules), time: time,
                failureClass: j?.FailureClass, judge: j?.Consultation));
    }

    /// <summary>No snapshot travels inline: the orchestrator staged it under the agent id before the
    /// launch, and the server resolves it from there.</summary>
    static async Task<PolicyJudgeResult> ConsultAsync(
            ClaudeHostedPermissionCall call, PolicySnapshot snapshot, CanonicalAction action, TimeProvider time,
            PolicyJudgeGateway judge, ConfigRoot? judgeState, TimeSpan budget, CancellationToken ct) {
        try {
            var started      = time.GetTimestamp();
            var declarations = ClaudeJudgeDeclarationReader.Read(call.TranscriptPath, call.ToolUseId, call.Cwd,
                judgeState?.Path("policy", "judge", $"{PolicySnapshotStore.Sanitize(call.SessionId)}.json"));
            var wire         = PolicyWire.ToWire(action);

            return await judge.ConsultAsync(budgetMs => new PolicyJudgeRequestV1(
                    call.SessionId, call.AgentId, "claude", PolicySeams.HostedClaudePermission, snapshot.Id,
                    PolicyEngine.Version, wire, declarations.Turns, declarations.Refusals, Snapshot: null, budgetMs),
                budget - time.GetElapsedTime(started), ct);
        } catch {
            return PolicyJudgeResult.PassThrough(PolicyJudgeResult.Error);
        }
    }
}
