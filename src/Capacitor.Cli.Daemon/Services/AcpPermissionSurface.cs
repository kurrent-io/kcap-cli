using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Presents an ACP permission request on the desktop app's local surface (the
/// <see cref="PermissionPromptBroker"/>, which the app subscribes to over local control IPC) IN
/// ADDITION to the server's web UI, and lets whichever surface answers first settle it — the loser
/// is dismissed. Wraps the server-facing <c>requestInteraction</c> delegate the ACP interaction
/// bridge already calls, so the bridge itself is unchanged.
///
/// <para>Only a permission carries a desktop card; an elicitation (or any other kind) goes straight
/// to the server. The desktop card answers allow/deny (its Claude/Codex shape); this maps that to
/// one of the agent's own offered options by kind — an allow to an allow option, a deny to a reject
/// option — so <c>AcpInteractionBridge.MapPermissionDecision</c> resolves the id exactly as a
/// server-side option pick would. A deny with no reject option, or an allow with no allow option,
/// falls through to that method's fail-closed cancelled.</para>
///
/// <para>When the desktop answers first, the server await is cancelled; the server's own web card is
/// not withdrawn (that needs a server-side channel this daemon does not have), so it lingers until
/// dismissed there, and a late answer to it finds no pending daemon entry and is dropped — the agent
/// has already been answered.</para>
/// </summary>
internal sealed class AcpPermissionSurface(
        PermissionPromptBroker                                                        broker,
        string                                                                        vendor,
        Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>  requestServer,
        TimeProvider?                                                                 timeProvider = null) {

    readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AcpInteractionDecision> RequestAsync(AcpInteractionRequest request, CancellationToken ct) {
        if (request.Kind != "permission")
            return await requestServer(request, ct).ConfigureAwait(false);

        var localId = Guid.NewGuid().ToString("N");
        var pending = BuildPending(localId, request);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var serverTask = requestServer(request, linked.Token);
        var localTask  = broker.Register(pending);

        var winner = await Task.WhenAny(serverTask, localTask).ConfigureAwait(false);

        if (winner == serverTask) {
            AcpInteractionDecision decision;
            try {
                decision = await serverTask.ConfigureAwait(false);
            } catch {
                // The server request's own failure — clear the desktop card and let the bridge's
                // catch turn the throw into a well-formed cancelled.
                broker.TrySettle(localId, PermissionSettlements.DenyDecision, PermissionSettlements.Deny, PermissionSettlements.SourceServer);
                throw;
            }

            // Server (web UI) answered first — dismiss the desktop card with the matching outcome.
            var brokerOutcome = IsAffirmative(decision.Outcome) ? PermissionSettlements.Allow : PermissionSettlements.Deny;
            broker.TrySettle(localId, new PermissionDecision(brokerOutcome, null, null), brokerOutcome, PermissionSettlements.SourceServer);
            return decision;
        }

        // Desktop app answered first — stop waiting on the server, map allow/deny to an offered option.
        var settlement = await localTask.ConfigureAwait(false);
        linked.Cancel();
        Observe(serverTask);

        return MapSettlement(settlement, request.Options ?? []);
    }

    PermissionPendingDto BuildPending(string requestId, AcpInteractionRequest request) =>
        new(RequestId: requestId,
            AgentId:    request.AgentId,
            SessionId:  request.AcpSessionId,
            Vendor:     vendor,
            ToolName:   request.ToolName ?? "",
            ToolInput:  request.ToolInput,
            // The agent's per-option choices stay on the web card; the desktop card is allow/deny,
            // so it needs no suggestions payload.
            Suggestions:        null,
            ToolInputOmitted:   false,
            SuggestionsOmitted: true,
            RequestedAt:        _time.GetUtcNow().ToString("o"),
            ToolUseId:          request.ToolCallId);

    static AcpInteractionDecision MapSettlement(PermissionSettlement settlement, IReadOnlyList<AcpInteractionOption> options) {
        if (settlement.Outcome == PermissionSettlements.Allow && PickAllow(options, IsTrue(settlement.Decision.ApplyPermissions)) is { } allow)
            return new AcpInteractionDecision(allow.Kind ?? "allow", allow.OptionId, allow.Label, null, null, null);

        // A deny (or a withdrawal, or an allow with no allow option to point at): the daemon's
        // DenyResult picks the least-privilege reject itself, and falls through to cancelled when
        // the agent offered no reject.
        return new AcpInteractionDecision("deny", null, null, null, null, null);
    }

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
        return preferAlways ? always ?? once ?? other : once ?? other ?? always;
    }

    static bool IsAffirmative(string outcome) =>
        outcome is "allow" or "allow_once" or "allow_always" or "answered";

    static bool IsTrue(JsonElement? value) => value is { ValueKind: JsonValueKind.True };

    static void Observe(Task task) => _ = task.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
}
