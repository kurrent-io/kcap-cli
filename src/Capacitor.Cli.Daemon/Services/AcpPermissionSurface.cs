using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Presents an ACP permission on the desktop app's local surface (the
/// <see cref="PermissionPromptBroker"/>) alongside the server's web card and lets whichever answers
/// first settle it; the loser is dismissed. Only a permission raises a desktop card — an elicitation
/// (or any other kind) goes straight to the server.
///
/// <para>The desktop card answers allow/deny; that maps to one of the agent's own offered options by
/// kind so <c>AcpInteractionBridge.MapPermissionDecision</c> resolves the id exactly as a server-side
/// pick would. A generic allow never selects an <c>allow_always</c> option — a one-time allow must
/// not silently become a standing grant, so an allow with no once-scoped option to point at fails
/// closed to a deny.</para>
///
/// <para>The server request id, once minted, is paired with the local card via
/// <see cref="PermissionPromptBroker.TryCorrelate"/> so a client that sees both lanes coalesces them
/// into one card. When the desktop answers first the same decision is submitted back to the server so
/// its history records that allow/deny rather than the cancel a bare abandonment would leave; the
/// server keeps the first writer, so a web answer that already landed still wins. Every settled
/// request is written to the <see cref="PermissionDecisionLog"/>. A tool payload too large to ride a
/// control frame is not presented locally at all — it would poison every subscription — and falls
/// back to a server-only request.</para>
/// </summary>
internal sealed class AcpPermissionSurface(
        PermissionPromptBroker                                                                       broker,
        string                                                                                       vendor,
        Func<AcpInteractionRequest, Action<string>?, CancellationToken, Task<AcpInteractionDecision>> requestServer,
        Func<string, string, AcpInteractionDecision, Task>?                                           resolveServer = null,
        PermissionDecisionLog?                                                                        decisionLog = null,
        TimeProvider?                                                                                 timeProvider = null) {

    readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AcpInteractionDecision> RequestAsync(AcpInteractionRequest request, CancellationToken ct) {
        if (request.Kind != "permission")
            return await requestServer(request, null, ct).ConfigureAwait(false);

        var localId = Guid.NewGuid().ToString("N");
        var pending = BuildPending(localId, request);
        if (pending is null)
            // Over-cap: a frame the codec would reject, replayed on every subscription forever.
            return await requestServer(request, null, ct).ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serverId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Register before the server leg so the server id can correlate onto a live entry.
        var localTask  = broker.Register(pending);
        var serverTask = requestServer(request, id => { serverId.TrySetResult(id); broker.TryCorrelate(localId, id); }, linked.Token);

        var winner = await Task.WhenAny(serverTask, localTask).ConfigureAwait(false);

        if (winner == serverTask) {
            AcpInteractionDecision decision;
            try {
                decision = await serverTask.ConfigureAwait(false);
            } catch {
                broker.TrySettle(localId, PermissionSettlements.DenyDecision, PermissionSettlements.Deny, PermissionSettlements.SourceServer);
                Record(request, PermissionSettlements.Deny, PermissionSettlements.SourceServer);
                throw;
            }

            var brokerOutcome = IsAffirmative(decision.Outcome) ? PermissionSettlements.Allow : PermissionSettlements.Deny;
            broker.TrySettle(localId, new PermissionDecision(brokerOutcome, null, null), brokerOutcome, PermissionSettlements.SourceServer);
            Record(request, brokerOutcome, PermissionSettlements.SourceServer);
            return decision;
        }

        var settlement = await localTask.ConfigureAwait(false);
        var mapped = MapSettlement(settlement, request.Options ?? []);
        await ResolveServer(request, serverId, settlement, mapped).ConfigureAwait(false);
        linked.Cancel();
        Observe(serverTask);
        Record(request, settlement.Outcome, settlement.Source);
        return mapped;
    }

    // The desktop answered first: mirror that decision onto the still-open server interaction so its
    // history is the allow/deny the agent received, not the cancel the cancelled server await would
    // otherwise leave. A withdrawal (agent gone) has nothing to record, and a server id that never
    // arrived (nothing to answer) is skipped. Best-effort — a failure leaves the abandonment path,
    // fired by the cancel below, to resolve the server as it did before.
    async Task ResolveServer(AcpInteractionRequest request, TaskCompletionSource<string> serverId, PermissionSettlement settlement, AcpInteractionDecision mapped) {
        if (resolveServer is null || settlement.Outcome == PermissionSettlements.Withdrawn || !serverId.Task.IsCompletedSuccessfully)
            return;
        try { await resolveServer(request.AcpSessionId, serverId.Task.Result, mapped).ConfigureAwait(false); }
        catch { /* the cancel's abandonment path still resolves the server */ }
    }

    PermissionPendingDto? BuildPending(string requestId, AcpInteractionRequest request) =>
        // The agent's per-option choices stay on the web card; the desktop card is allow/deny, so it
        // carries no suggestions payload. The shared builder bounds every caller-controlled value.
        LocalPermissionBridge.BuildPending(
            requestId, request.AgentId, request.AcpSessionId, vendor, request.ToolName ?? "",
            request.ToolInput, suggestions: null, _time.GetUtcNow().ToString("o"), request.ToolCallId);

    void Record(AcpInteractionRequest request, string outcome, string source) =>
        decisionLog?.Record(new PermissionDecisionRecord(
            _time.GetUtcNow().ToString("O"), request.AgentId, request.AcpSessionId, vendor,
            request.ToolName ?? "", outcome, source));

    static AcpInteractionDecision MapSettlement(PermissionSettlement settlement, IReadOnlyList<AcpInteractionOption> options) {
        var preferAlways = settlement.Decision.ApplyPermissions is { } apply && apply.IsTrue;
        if (settlement.Outcome == PermissionSettlements.Allow && PickAllow(options, preferAlways) is { } allow)
            return new AcpInteractionDecision(allow.Kind ?? "allow", allow.OptionId, allow.Label, null, null, null);

        // A deny (or a withdrawal, or an allow with no once-scoped option to point at): the daemon's
        // DenyResult picks the least-privilege reject itself, and falls through to cancelled when
        // the agent offered no reject.
        return new AcpInteractionDecision("deny", null, null, null, null, null);
    }

    /// A standing grant is offered only when the desktop explicitly asked for one (preferAlways); a
    /// generic allow never resolves to <c>allow_always</c> — it would grant more than was shown.
    static AcpInteractionOption? PickAllow(IReadOnlyList<AcpInteractionOption> options, bool preferAlways) {
        AcpInteractionOption? once = null, always = null, other = null;
        foreach (var o in options) {
            switch (o.Kind) {
                case "allow_once"   when once   is null: once   = o; break;
                case "allow_always" when always is null: always = o; break;
                default:
                    if (other is null && (o.Kind is null || o.Kind.StartsWith("allow", StringComparison.Ordinal))) other = o;
                    break;
            }
        }
        return preferAlways ? always ?? once ?? other : once ?? other;
    }

    static bool IsAffirmative(string outcome) =>
        outcome is "allow" or "allow_once" or "allow_always" or "answered";

    static void Observe(Task task) => _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
}
