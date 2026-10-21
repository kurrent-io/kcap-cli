using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Harness.Claude;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// The approval policy at a terminal-hosted Codex's permission requests, which its hook bounces through the
/// daemon's bridge. Rules apply in full. The judge declares no turn set: the rollout's turns can be named only by
/// the hash of the line as the watcher redacted it, which the daemon does not compute, so its consultation is
/// windowless and declares this bridge's refusals.
/// </summary>
internal static class CodexPtyPolicySeam {
    const string Vendor = "codex";

    internal static async Task<ClaudeHostedPolicyResult?> EvaluateAsync(
            ClaudeHostedPermissionCall call, PolicySnapshot snapshot, TimeProvider time, PolicyJudgeGateway? judge, TimeSpan judgeBudget,
            ILogger logger, PermissionRefusalLedger refusals, CancellationToken ct) {
        var action     = CodexHookActions.Normalize(call.ToolName, call.ToolInput, call.Cwd);
        var evaluation = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);

        switch (evaluation.Outcome) {
            case PolicyOutcome.Allow: return Result(PolicyOutcome.Allow, "allow", "allow", null);
            case PolicyOutcome.Deny:  return Result(PolicyOutcome.Deny,  "deny",  "deny",  null);
            case PolicyOutcome.Ask:   return Result(PolicyOutcome.Ask,   "ask",   "parked", null);
        }

        if (judge is null || !snapshot.JudgeEnabled) return null;

        PolicyJudgeResult judged;
        try {
            var wire = PolicyWire.ToWire(action);
            judged = await judge.ConsultAsync(budgetMs => new PolicyJudgeRequestV1(
                    call.SessionId, call.AgentId, Vendor, PolicySeams.HostedCodexPermission, snapshot.Id, PolicyEngine.Version, wire,
                    null, refusals.Declare(call.SessionId), PolicyWire.ToUpload(call.SessionId, snapshot), budgetMs),
                judgeBudget, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            logger.LogDebug(ex, "Hosted Codex policy judge consultation threw for agent {AgentId}; passing through", call.AgentId);
            judged = PolicyJudgeResult.PassThrough(PolicyJudgeResult.Error);
        }

        return judged.Outcome switch {
            PolicyOutcome.Allow => Result(PolicyOutcome.Allow, "allow", "allow", judged),
            PolicyOutcome.Deny  => Result(PolicyOutcome.Deny,  "deny",  "deny",  judged),
            PolicyOutcome.Ask   => Result(PolicyOutcome.Ask,   "ask",   "parked", judged),
            _                   => Result(PolicyOutcome.None,  "pass_through", "pass_through", judged),
        };

        ClaudeHostedPolicyResult Result(PolicyOutcome outcome, string requested, string effective, PolicyJudgeResult? j) =>
            new(outcome, PolicyWire.Decision(
                sessionId: call.SessionId, agentId: call.AgentId, vendor: Vendor, seam: PolicySeams.HostedCodexPermission,
                snapshot: snapshot, mode: EvaluationMode.Full, requestedOutcome: requested, effectiveOutcome: effective,
                action: PolicyWire.ToWire(action), matchedRules: PolicyWire.ToWire(evaluation.MatchedRules), time: time,
                failureClass: j?.FailureClass, judge: j?.Consultation));
    }

    /// <summary>A deny of a call in a run whose judge is on, kept for the judge's next consultation in that session.</summary>
    internal static void RecordRefusal(PermissionRefusalLedger refusals, ClaudeHostedPermissionCall call) {
        CanonicalAction? action;
        try {
            action = CodexHookActions.Normalize(call.ToolName, call.ToolInput, call.Cwd);
        } catch {
            action = null;
        }

        // Codex's permission prompt names no tool id, so each refusal gets its own and is declared unverified, not lost.
        refusals.Record(call.SessionId, call.ToolUseId ?? $"codex-pty-{Guid.NewGuid():N}", approvalId: null, call.ToolName, action, promptId: null);
    }
}
