using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Policy;

namespace Capacitor.Cli.Harness.Codex;

/// <summary>
/// The approval policy at the Codex hook. <c>PreToolUse</c> runs rules alone and answers only a deny, the one
/// answer Codex honours there, so a deny rule binds a call Codex would run unasked. <c>PermissionRequest</c>
/// runs rules, then the judge on a call no rule decided. Fail-open throughout: an unreadable payload or policy,
/// or a judge that does not answer, leaves Codex's own handling exactly as it is without the seam.
/// </summary>
internal sealed class CodexPolicySeam(ConfigRoot config, TimeProvider time, PolicyJudgeGateway? judge = null) {
    const string Vendor        = "codex";
    const string DefaultReason = "kcap approval policy";

    /// <summary>The judge's share of the hook, the transcript read included, inside its 5s ceiling.</summary>
    internal static readonly TimeSpan JudgeBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long a consultation waits for the watcher to acknowledge the newest human line.</summary>
    static readonly TimeSpan CursorWait = TimeSpan.FromSeconds(1);

    sealed record Fields(string? ToolName, JsonElement? ToolInput, string? Cwd, string? CallId, string? TranscriptPath);

    public async Task HandlePreToolUseAsync(JsonObject payload, string sessionId, TextWriter stdout) {
        try {
            var fields   = Read(payload);
            var snapshot = LoadSnapshot(sessionId, fields.Cwd);
            if (snapshot.IsEmpty) return;

            var action = CodexHookActions.Normalize(fields.ToolName, fields.ToolInput, fields.Cwd);
            var eval   = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);

            // A matched rule is recorded; this hook fires on every call, so one no rule matched is not.
            if (eval.Outcome != PolicyOutcome.Deny) {
                if (eval.Outcome != PolicyOutcome.None)
                    await Emit(sessionId, PolicySeams.CodexPreToolUse, snapshot, action, eval, fields.CallId,
                        eval.Outcome == PolicyOutcome.Allow ? "allow" : "ask", "pass_through", null);
                return;
            }

            stdout.Write(new JsonObject {
                ["hookSpecificOutput"] = new JsonObject {
                    ["hookEventName"]            = "PreToolUse",
                    ["permissionDecision"]       = "deny",
                    ["permissionDecisionReason"] = ReasonOf(eval),
                },
            }.ToJsonString());
            await Emit(sessionId, PolicySeams.CodexPreToolUse, snapshot, action, eval, fields.CallId, "deny", "deny", null);
        } catch {
            // Nothing written: Codex runs the call as it would without the seam.
        }
    }

    /// <returns>Answered when this wrote Codex's decision; otherwise nothing was written.</returns>
    public async Task<SeamAnswer> HandlePermissionRequestAsync(JsonObject payload, string sessionId, TextWriter stdout, TimeSpan judgeBudget) {
        try {
            var fields   = Read(payload);
            var snapshot = LoadSnapshot(sessionId, fields.Cwd);
            if (snapshot.IsEmpty) return SeamAnswer.NotAnswered;

            var action = CodexHookActions.Normalize(fields.ToolName, fields.ToolInput, fields.Cwd);
            var eval   = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);
            var judged = eval.Outcome == PolicyOutcome.None && snapshot.JudgeEnabled && judge is not null
                ? await ConsultJudgeAsync(sessionId, snapshot, action, fields, judgeBudget)
                : null;
            var outcome = judged?.Outcome is { } j and not PolicyOutcome.None ? j : eval.Outcome;
            var reason  = judged is { Outcome: not PolicyOutcome.None }
                ? judged.Rationale is { Length: > 0 } r ? $"kcap policy judge: {r}" : "kcap policy judge"
                : ReasonOf(eval);

            switch (outcome) {
                case PolicyOutcome.Allow:
                    stdout.Write(Decision("allow", null));
                    await Emit(sessionId, PolicySeams.CodexPermissionRequest, snapshot, action, eval, fields.CallId, "allow", "allow", judged);
                    return SeamAnswer.Answered;
                case PolicyOutcome.Deny:
                    stdout.Write(Decision("deny", reason));
                    await Emit(sessionId, PolicySeams.CodexPermissionRequest, snapshot, action, eval, fields.CallId, "deny", "deny", judged);
                    return SeamAnswer.Answered;
                case PolicyOutcome.Ask:
                    await Emit(sessionId, PolicySeams.CodexPermissionRequest, snapshot, action, eval, fields.CallId, "ask", "prompt_stands", judged);
                    return SeamAnswer.NotAnswered;
                default:
                    if (judged is not null)
                        await Emit(sessionId, PolicySeams.CodexPermissionRequest, snapshot, action, eval, fields.CallId, "pass_through", "pass_through", judged);
                    return SeamAnswer.NotAnswered;
            }
        } catch {
            return SeamAnswer.NotAnswered;
        }
    }

    /// <summary>Codex records no decline a user gives, so the refusals are declared incomplete and the server
    /// clamps any judge allow to ask: on this lane the judge can deny or ask, never allow.</summary>
    async Task<PolicyJudgeResult?> ConsultJudgeAsync(string sessionId, PolicySnapshot snapshot, CanonicalAction action, Fields fields, TimeSpan budget) {
        try {
            var started = time.GetTimestamp();
            var turns   = fields.TranscriptPath is { Length: > 0 } path
                ? (await CodexJudgeDeclarationReader.ReadAsync(path, sessionId, new WatchAckCursor(config), time,
                    budget < CursorWait ? budget / 2 : CursorWait, CancellationToken.None)).Turns
                : null;
            var inline  = new HookSpool(config, time).IsPolicySnapshotDelivered(sessionId, snapshot.Id)
                ? null
                : PolicyWire.ToUpload(sessionId, snapshot);
            var wire    = PolicyWire.ToWire(action);

            return await judge!.ConsultAsync(budgetMs => new PolicyJudgeRequestV1(
                    sessionId, AgentId: null, Vendor, PolicySeams.CodexPermissionRequest, snapshot.Id, PolicyEngine.Version, wire,
                    turns, new PolicyJudgeRefusalsV1(false, PolicyJudgeRefusalsV1.SourceTranscript, []), inline, budgetMs),
                budget - time.GetElapsedTime(started));
        } catch {
            return PolicyJudgeResult.PassThrough(PolicyJudgeResult.Error);
        }
    }

    internal static string Decision(string behavior, string? message) {
        var decision = new JsonObject { ["behavior"] = behavior };
        if (message is not null) decision["message"] = message;
        return new JsonObject {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        }.ToJsonString();
    }

    PolicySnapshot LoadSnapshot(string sessionId, string? cwd) =>
        new PolicySnapshotStore(config).LoadOrBuild(sessionId, cwd is null ? null : GitRepository.FindRoot(cwd));

    static string ReasonOf(PolicyEvaluation eval) =>
        (eval.MatchedRules.Count > 0 ? eval.MatchedRules[0].Reason : null) ?? DefaultReason;

    static Fields Read(JsonObject body) {
        JsonElement? input = null;
        try {
            if (body["tool_input"] is { } node) input = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
        } catch { input = null; }
        return new(Str(body, "tool_name"), input, Str(body, "cwd"), Str(body, "tool_use_id"), Str(body, "transcript_path"));
    }

    static string? Str(JsonObject body, string property) =>
        body[property] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    Task Emit(string sessionId, string seam, PolicySnapshot snapshot, CanonicalAction action, PolicyEvaluation eval, string? callId,
              string requested, string effective, PolicyJudgeResult? judged) =>
        new PolicyDecisionEmitter(config, time).EmitAsync(PolicyWire.Decision(
            sessionId: sessionId, agentId: null, vendor: Vendor, seam: seam, snapshot: snapshot, mode: EvaluationMode.Full,
            requestedOutcome: requested, effectiveOutcome: effective, action: PolicyWire.ToWire(action),
            matchedRules: PolicyWire.ToWire(eval.MatchedRules), time: time, failureClass: judged?.FailureClass,
            correlationId: callId, correlationAmbiguous: callId is null, judge: judged?.Consultation), snapshot);
}
