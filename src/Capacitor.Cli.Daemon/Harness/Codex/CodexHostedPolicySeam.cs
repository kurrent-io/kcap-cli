using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Policy;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// The approval policy at a hosted Codex app-server's approval requests: rules first, then the judge on a call no
/// rule decided, before anything reaches a card. Fail-open toward the human: anything it cannot decide or does
/// not finish is forwarded to the card unchanged, whose own failures still decline.
/// </summary>
internal sealed class CodexHostedPolicySeam(
        PolicySnapshot snapshot, PolicyJudgeGateway? judge, Action<PolicyDecisionEventV1> emit, PermissionRefusalLedger refusals,
        Func<IAcpPersistenceProbe?> probe, string agentId, TimeProvider time, ILogger logger, string? workingDirectory = null) {
    const string Vendor = "codex";

    /// <summary>The agent is already waiting on an approval, so the judge gets the server's full ceiling.</summary>
    internal static readonly TimeSpan JudgeBudget = TimeSpan.FromSeconds(5);

    /// <summary>The share of that budget spent waiting for the declared turns to be stored.</summary>
    static readonly TimeSpan PersistWait = TimeSpan.FromSeconds(2);

    public CodexPatchPaths Paths { get; } = new();
    public CodexEnvelopeTurns Turns { get; } = new();

    /// <summary>The runtime started this thread itself, so every refusal in it passes through here.</summary>
    public void MarkBridgedSinceStart(string threadId) {
        if (SessionIds.Canonical(threadId) is { } sessionId) refusals.MarkBridgedSinceStart(sessionId);
    }

    public async Task<CodexHostedVerdict> EvaluateAsync(string method, JsonElement request, CancellationToken ct) {
        try {
            if (Action(method, request) is not { } action) return CodexHostedVerdict.Forward;
            var sessionId  = SessionIds.Canonical(request.Str("threadId"));
            if (sessionId is null) return CodexHostedVerdict.Forward;

            var evaluation = PolicyEngine.Evaluate(snapshot, action, EvaluationMode.Full);
            switch (evaluation.Outcome) {
                case PolicyOutcome.Allow:
                    Emit(sessionId, action, evaluation, "allow", "allow", null);
                    return CodexHostedVerdict.Accept;
                case PolicyOutcome.Deny:
                    RecordRefusal(sessionId, method, request, action);
                    Emit(sessionId, action, evaluation, "deny", "deny", null);
                    return CodexHostedVerdict.Decline;
                case PolicyOutcome.Ask:
                    Emit(sessionId, action, evaluation, "ask", "parked", null);
                    return CodexHostedVerdict.Forward;
            }

            if (judge is null || !snapshot.JudgeEnabled) return CodexHostedVerdict.Forward;

            var judged = await ConsultAsync(sessionId, action, ct).ConfigureAwait(false);
            switch (judged.Outcome) {
                case PolicyOutcome.Allow:
                    Emit(sessionId, action, evaluation, "allow", "allow", judged);
                    return CodexHostedVerdict.Accept;
                case PolicyOutcome.Deny:
                    RecordRefusal(sessionId, method, request, action);
                    Emit(sessionId, action, evaluation, "deny", "deny", judged);
                    return CodexHostedVerdict.Decline;
                case PolicyOutcome.Ask:
                    Emit(sessionId, action, evaluation, "ask", "parked", judged);
                    return CodexHostedVerdict.Forward;
                default:
                    Emit(sessionId, action, evaluation, "pass_through", "pass_through", judged);
                    return CodexHostedVerdict.Forward;
            }
        } catch (Exception ex) {
            logger.LogDebug(ex, "Hosted Codex policy evaluation threw for agent {AgentId}; forwarding to the card", agentId);
            return CodexHostedVerdict.Forward;
        }
    }

    /// <summary>A decline the user gave at the card, kept for the judge's next consultation in that session.</summary>
    public void RecordUserDecline(string method, JsonElement request) {
        try {
            if (SessionIds.Canonical(request.Str("threadId")) is { } sessionId)
                RecordRefusal(sessionId, method, request, Action(method, request));
        } catch (Exception ex) {
            logger.LogDebug(ex, "Recording a hosted Codex refusal for agent {AgentId} failed", agentId);
        }
    }

    /// <summary>Null for a request this seam does not govern, or a file change whose paths are not yet known or cannot
    /// all be placed: those reach the card unevaluated, so a path rule can never be skipped by notification order or
    /// passed by the part of an edit it could place. A file-change approval names no directory, so its paths resolve
    /// against the one the agent runs in.</summary>
    CanonicalAction? Action(string method, JsonElement request) {
        var cwd = request.Str("cwd") ?? workingDirectory;
        switch (ItemType(method)) {
            // Only a plain local command: a stdin or managed-network approval, or one bound for another execution
            // environment, asks for more than its command text says.
            case "commandExecution" when request.Str("kind") is null or "command" && !HasValue(request, "networkApprovalContext")
                                      && !HasValue(request, "environmentId") && request.Str("command") is { Length: > 0 } command: {
                using var input = JsonDocument.Parse(new JsonObject { ["command"] = command }.ToJsonString());
                return CodexHookActions.Normalize("Bash", input.RootElement.Clone(), cwd);
            }
            // A grant root asks for writes under it for the rest of the session, beyond this edit's paths.
            case "fileChange" when !HasValue(request, "grantRoot") && request.Str("itemId") is { } itemId && Paths.TryPaths(itemId, out var paths): {
                var resolved = paths.Select(p => LexicalPaths.TryResolve(cwd, p)).ToArray();
                if (resolved.Length == 0 || resolved.Any(p => p is null)) return null;
                return new CanonicalAction { Kind = ActionKind.FileEdit, Vendor = Vendor, Cwd = cwd, RawToolName = "apply_patch", Paths = [.. resolved!] };
            }
            default:
                return null;
        }
    }

    static bool HasValue(JsonElement request, string property) =>
        request.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null;

    async Task<PolicyJudgeResult> ConsultAsync(string sessionId, CanonicalAction action, CancellationToken ct) {
        try {
            var started = time.GetTimestamp();
            var turns   = await DeclaredTurnsAsync(ct).ConfigureAwait(false);
            var wire    = PolicyWire.ToWire(action);
            var inline  = PolicyWire.ToUpload(sessionId, snapshot);

            return await judge!.ConsultAsync(budgetMs => new PolicyJudgeRequestV1(
                    sessionId, agentId, Vendor, PolicySeams.HostedCodexApproval, snapshot.Id, PolicyEngine.Version, wire,
                    turns, refusals.Declare(sessionId), inline, budgetMs),
                JudgeBudget - time.GetElapsedTime(started), ct).ConfigureAwait(false);
        } catch (Exception ex) {
            logger.LogDebug(ex, "Hosted Codex policy judge consultation threw for agent {AgentId}; passing through", agentId);
            return PolicyJudgeResult.PassThrough(PolicyJudgeResult.Error);
        }
    }

    /// <summary>Only the stored turns back from the newest are declared: one the server has not stored would fail the
    /// evidence read, and skipping it would let an older prompt stand in for the one the approval belongs to. With the
    /// newest unstored the consultation is windowless.</summary>
    async Task<PolicyJudgeTurnSetV1?> DeclaredTurnsAsync(CancellationToken ct) {
        var recent = Turns.Recent();
        if (recent.Count == 0 || probe() is not { } persistence) return null;

        var stored = await persistence.AwaitPersistedAsync([.. recent.Select(t => t.ItemId)], PersistWait, ct).ConfigureAwait(false);
        var declared = recent.Reverse().TakeWhile(t => stored.ContainsKey(t.ItemId)).Reverse()
            .Select(t => new PolicyJudgeDeclaredTurnV1(AcpItemTurnId.For(t.ItemId).ToString(), t.TurnId))
            .ToArray();

        if (declared.Length == 0) logger.LogDebug("Hosted Codex judge for agent {AgentId}: turns_not_persisted", agentId);
        return declared.Length == 0 ? null : new PolicyJudgeTurnSetV1(declared, null);
    }

    void RecordRefusal(string sessionId, string method, JsonElement request, CanonicalAction? action) =>
        refusals.Record(sessionId, request.Str("itemId"), request.Str("approvalId"), ItemType(method), action, request.Str("turnId"));

    void Emit(string sessionId, CanonicalAction action, PolicyEvaluation evaluation, string requested, string effective, PolicyJudgeResult? judged) =>
        emit(PolicyWire.Decision(
            sessionId: sessionId, agentId: agentId, vendor: Vendor, seam: PolicySeams.HostedCodexApproval, snapshot: snapshot,
            mode: EvaluationMode.Full, requestedOutcome: requested, effectiveOutcome: effective, action: PolicyWire.ToWire(action),
            matchedRules: PolicyWire.ToWire(evaluation.MatchedRules), time: time, failureClass: judged?.FailureClass,
            judge: judged?.Consultation));

    static string ItemType(string method) {
        var parts = method.Split('/');
        return parts.Length >= 2 ? parts[^2] : method;
    }
}
