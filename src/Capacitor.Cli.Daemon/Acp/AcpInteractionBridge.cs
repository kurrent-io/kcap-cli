// src/Capacitor.Cli.Daemon/Acp/AcpInteractionBridge.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Acp;

/// <summary>
/// Bridges ACP server→client interaction requests (<c>session/request_permission</c>, and a
/// capability-gated, defensive <c>elicitation/create</c>) to the Capacitor server's
/// <c>AcpRequestInteraction</c> hub method (AI-686), and maps the returned decision back to the
/// ACP JSON-RPC result shape. Wired as (a closure over) <see cref="AcpConnection.OnServerRequest"/>
/// by <see cref="Capacitor.Cli.Daemon.Services.AcpHostedAgentRuntime"/> (Task B4).
///
/// <paramref name="requestInteraction"/> is injected as a plain delegate — matching the shape of
/// <see cref="Capacitor.Cli.Daemon.Services.ServerConnection.RequestAcpInteractionAsync"/> — rather
/// than taking a concrete <c>ServerConnection</c> dependency, so this class is unit-testable
/// without a real SignalR connection (see <c>AcpInteractionBridgeTests</c>).
///
/// Defensive-by-construction: every code path that can fail (missing/malformed params, the
/// server call throwing, a decision that doesn't map to any offered option) returns a
/// <c>"cancelled"</c>/safest-available-option result rather than propagating an exception —
/// <see cref="AcpConnection.HandleServerRequestAsync"/> already guarantees exactly one response
/// frame is always written for a given request id, but this bridge additionally guarantees that
/// response is always a well-formed ACP outcome, never a generic JSON-RPC "Internal error" that
/// would tell the agent nothing about WHY the permission was denied.
/// </summary>
internal sealed class AcpInteractionBridge(
        Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
        string                                                                       agentId,
        ILogger                                                                      logger
    ) {
    /// <summary>
    /// Handles one inbound <see cref="AcpRequest"/>. Returns <see langword="null"/> for any method
    /// this bridge doesn't recognize (letting <see cref="AcpConnection.HandleServerRequestAsync"/>'s
    /// existing default-decline posture answer with a JSON-RPC "Method not found" error, unchanged
    /// from AI-684's behavior for every method except the two this bridge now claims).
    /// </summary>
    public async Task<JsonElement?> HandleAsync(AcpRequest request, string acpSessionId, CancellationToken ct) {
        return request.Method switch {
            "session/request_permission" => await HandlePermissionAsync(request, acpSessionId, ct).ConfigureAwait(false),
            "elicitation/create"         => await HandleElicitationAsync(request, acpSessionId, ct).ConfigureAwait(false),
            _                            => null
        };
    }

    async Task<JsonElement?> HandlePermissionAsync(AcpRequest request, string acpSessionId, CancellationToken ct) {
        SessionRequestPermissionParams parsed;

        try {
            if (request.Params is not { } p)
                return CancelledResult();

            parsed = p.Deserialize(CapacitorJsonContext.Default.SessionRequestPermissionParams)
                ?? throw new JsonException("null params");
        } catch (JsonException ex) {
            logger.LogDebug(ex, "ACP: malformed session/request_permission params for agent {AgentId}", agentId);

            return CancelledResult();
        }

        var interactionRequest = new AcpInteractionRequest(
            AgentId: agentId,
            AcpSessionId: acpSessionId,
            Kind: "permission",
            ToolName: TryGetToolTitle(parsed.ToolCall),
            ToolInput: parsed.ToolCall,
            ToolCallId: TryGetToolCallId(parsed.ToolCall),
            Prompt: null,
            // Spec-review Finding 6: carry OptionId through to the server-facing DTO so a UI
            // decision can round-trip it back — Options: o.Name (Label) is now display-only.
            Options: parsed.Options.Select(o => new AcpInteractionOption(o.OptionId, o.Name, null, o.Kind)).ToArray(),
            IsMultiSelect: false
        );

        AcpInteractionDecision decision;

        try {
            decision = await requestInteraction(interactionRequest, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Spec-review Finding 3(b): connection-closed / runtime-disposing / CT-cancelled while
            // this interaction is pending (AcpHostedAgentRuntime.DisposeAsync cancels its _cts,
            // which is the SAME token flowing through AcpConnection's read loop → HandleServerRequestAsync
            // → this bridge → RequestAcpInteractionAsync → PendingAcpInteractionRegistry.AwaitDecisionAsync,
            // whose own ct.Register callback removes the pending entry and TrySetCanceled()s it —
            // see Task B2). PRE-FIX, this exception type was explicitly excluded from the catch
            // below and propagated uncaught, so AcpConnection.HandleServerRequestAsync's own
            // catch-all converted it to a generic JSON-RPC "Internal error" (-32603) instead of the
            // well-formed ACP `cancelled` outcome every OTHER failure path in this bridge produces.
            // Best-effort: WriteServerResponseAsync may itself fail if the wire is already torn
            // down (AcpConnection's own catch around the write handles that silently) — this
            // bridge's only job is to make the ATTEMPTED response well-formed.
            logger.LogDebug("ACP: session/request_permission cancelled (connection closing) for agent {AgentId}; defaulting to cancelled", agentId);

            return CancelledResult();
        } catch (Exception ex) {
            logger.LogDebug(ex, "ACP: RequestAcpInteractionAsync threw for agent {AgentId}; defaulting to cancelled", agentId);

            return CancelledResult();
        }

        return MapPermissionDecision(decision, parsed.Options);
    }

    async Task<JsonElement?> HandleElicitationAsync(AcpRequest request, string acpSessionId, CancellationToken ct) {
        // Never advertised in `initialize` (see AcpHostedAgentRuntime.StartAsync's minimal
        // ClientCapabilities, unchanged by this plan) — handled defensively in case a real agent
        // sends it unprompted, per R3's open question on whether Cursor uses a vendor-specific
        // elicitation shape at all.
        ElicitationCreateParams parsed;

        try {
            if (request.Params is not { } p)
                return CancelledResult();

            parsed = p.Deserialize(CapacitorJsonContext.Default.ElicitationCreateParams)
                ?? throw new JsonException("null params");
        } catch (JsonException ex) {
            logger.LogDebug(ex, "ACP: malformed elicitation/create params for agent {AgentId}", agentId);

            return CancelledResult();
        }

        var options = parsed.Options ?? [];

        var interactionRequest = new AcpInteractionRequest(
            AgentId: agentId,
            AcpSessionId: acpSessionId,
            Kind: "elicitation",
            ToolName: null,
            ToolInput: null,
            ToolCallId: null,
            Prompt: parsed.Message,
            // Spec-review Finding 6: same OptionId carry-through as the permission path above.
            Options: options.Select(o => new AcpInteractionOption(o.OptionId, o.Name, null, o.Kind)).ToArray(),
            IsMultiSelect: false,
            // Spec-review Finding 1: forward RequestedSchema verbatim — this bridge never validates,
            // re-serializes, or renders it; the server (Task A1/A3) applies the actual 32KB/depth-8
            // caps and decides the generic-card fallback. Forwarding an oversized/malformed schema
            // here is safe: JsonElement.Deserialize already succeeded (it's syntactically valid JSON
            // by the time we have a JsonElement at all — a genuinely malformed JSON payload for the
            // whole params object was already caught by the JsonException handler above), and this
            // bridge does zero further inspection of RequestedSchema's shape.
            RequestedSchema: parsed.RequestedSchema
        );

        AcpInteractionDecision decision;

        try {
            decision = await requestInteraction(interactionRequest, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Spec-review Finding 3(b) — same disconnect handling as HandlePermissionAsync above.
            logger.LogDebug("ACP: elicitation/create cancelled (connection closing) for agent {AgentId}; defaulting to cancelled", agentId);

            return CancelledResult();
        } catch (Exception ex) {
            logger.LogDebug(ex, "ACP: RequestAcpInteractionAsync threw for elicitation on agent {AgentId}; defaulting to cancelled", agentId);

            return CancelledResult();
        }

        return MapPermissionDecision(decision, options);
    }

    /// <summary>
    /// Maps a resolved <see cref="AcpInteractionDecision"/> to the ACP outcome result shape.
    ///
    /// <b>Spec-review Finding 2 (security-critical, fail-safe-by-default):</b> this method uses
    /// an EXPLICIT ALLOWLIST of recognized affirmative outcome strings (<c>"allow"</c>/
    /// <c>"allow_once"</c>/<c>"allow_always"</c>/<c>"answered"</c> — the daemon does not reference
    /// the server-only <c>InterruptOutcomes</c> type per this plan's AOT/trim Global Constraints, so
    /// these are the literal string values of the SAME canonical vocabulary Task A2 documents, kept
    /// in sync by the <c>AffirmativeOutcomes</c> constant below) rather than a denylist of recognized
    /// negative outcomes with an "everything else selects" fallthrough. The PRIOR shape
    /// (<c>decision.Outcome is "deny" or "cancel" ? cancelled : selected</c>) meant ANY unrecognized
    /// outcome string — a typo (the canonical negative outcome is <c>"cancel"</c>, NOT
    /// <c>"cancelled"</c> — see Task A2's Interfaces note for exactly this drift happening once
    /// already in this plan), a future outcome vocabulary addition the daemon hasn't been updated
    /// for, or a malformed/corrupted decision — fell through to <c>selected</c>/<c>options[0]</c>,
    /// i.e. GRANTED permission for an outcome nobody explicitly asked to grant. Fail-safe now means:
    /// not on the allowlist → always <see cref="CancelledResult"/>, full stop, regardless of how many
    /// options were offered.
    ///
    /// <b>Fresh-review finding (this revision): a null <see cref="AcpInteractionDecision.SelectedOptionId"/>
    /// used to STILL fall back to the first offered option even for a recognized affirmative
    /// outcome</b> — an earlier draft of this fix treated "no id supplied" as "assume the human meant
    /// the first option," which is exactly the same class of bug spec-review Finding 2 exists to
    /// close: an ACP options request with two-or-more offered options (e.g. "Allow once" / "Deny")
    /// resolved via a decision-submit path that (for whatever reason — a bug, a stale client, a
    /// future code path that forgets to thread the id) omits <see cref="AcpInteractionDecision.SelectedOptionId"/>
    /// would silently grant `options[0]` — often "Allow" — regardless of what the human actually
    /// intended, or even whether the human ever saw the request. This method now FAILS CLOSED for
    /// that case too: for an ACP interaction that offered ANY options, an affirmative outcome with
    /// a null/unknown/unresolvable <see cref="AcpInteractionDecision.SelectedOptionId"/> ALWAYS maps
    /// to <c>cancelled</c> — there is NO first-option fallback anywhere in this method, full stop.
    /// The only way to get a <c>selected</c> result is an explicit, resolvable <c>SelectedOptionId</c>
    /// matching one of the offered <see cref="PermissionOptionDto.OptionId"/>s.
    ///
    /// For a recognized affirmative outcome (spec-review Finding 6): selects the option whose
    /// <see cref="PermissionOptionDto.OptionId"/> matches <see cref="AcpInteractionDecision.SelectedOptionId"/> —
    /// NEVER by re-matching <see cref="PermissionOptionDto.Name"/>/<see cref="AcpInteractionDecision.SelectedOptionLabel"/>,
    /// since duplicate or reordered option labels (which the ACP wire shape does not forbid) would
    /// otherwise resolve to the wrong option. Two cases, both fail-closed: a
    /// <see cref="AcpInteractionDecision.SelectedOptionId"/> that matches an offered
    /// <see cref="PermissionOptionDto.OptionId"/> → that option, <c>selected</c>; ANYTHING else — no
    /// id supplied at all (<see langword="null"/>), or an id that matches NONE of the offered
    /// options — → <c>cancelled</c> (an absent or unresolvable id, whether from a decision-submit
    /// path that forgot to echo one back or a correlation bug/stale/replayed decision, must never
    /// silently grant an arbitrary option instead).
    /// <c>"deny"</c>/<c>"cancel"</c>, no options offered at all, or ANY other outcome string not on
    /// the allowlist above, map to <c>cancelled</c>.
    /// </summary>
    static readonly HashSet<string> AffirmativeOutcomes = ["allow", "allow_once", "allow_always", "answered"];

    static JsonElement MapPermissionDecision(AcpInteractionDecision decision, IReadOnlyList<PermissionOptionDto> options) {
        // Fail-safe allowlist: only a RECOGNIZED affirmative outcome can ever produce "selected".
        // Everything else — deny, cancel, an unrecognized/typo'd string, or no options offered —
        // maps to cancelled. There is deliberately no "default: selected" path anywhere below.
        if (options.Count == 0 || !AffirmativeOutcomes.Contains(decision.Outcome))
            return CancelledResult()!.Value;

        // Fresh-review fix: resolve by OptionId ONLY, and FAIL CLOSED with no first-option
        // fallback for either "no id supplied" or "id supplied but unresolvable" — both map to
        // cancelled. There is deliberately no `options[0]` anywhere in this method:
        //   1. No SelectedOptionId supplied at all (null) → cancelled. An affirmative outcome with
        //      no specific option chosen is NOT treated as "assume the first option" — that was
        //      precisely the bug this fix closes (see the doc comment above).
        //   2. SelectedOptionId supplied and it matches an offered option's OptionId → that option.
        //   3. SelectedOptionId supplied but does NOT match any offered OptionId → treat as
        //      cancelled. An id that was explicitly set but doesn't resolve indicates a correlation
        //      bug or a stale/replayed decision — silently granting an unrelated option in that
        //      case would be worse than failing safe.
        // Cases 1 and 3 are DELIBERATELY THE SAME "cancelled" outcome, handled by the same
        // fallthrough below — there is no `options[0]`/first-option branch anywhere in this method.
        if (decision.SelectedOptionId is not { } optionId)
            return CancelledResult()!.Value;

        var matched = options.FirstOrDefault(o => o.OptionId == optionId);

        return matched is not null ? SelectedResult(matched) : CancelledResult()!.Value;
    }

    static JsonElement SelectedResult(PermissionOptionDto chosen) =>
        JsonSerializer.SerializeToElement(
            new PermissionOutcomeResult(new PermissionOutcomeDto("selected", chosen.OptionId)),
            CapacitorJsonContext.Default.PermissionOutcomeResult);

    static JsonElement? CancelledResult() =>
        JsonSerializer.SerializeToElement(
            new PermissionOutcomeResult(new PermissionOutcomeDto("cancelled")),
            CapacitorJsonContext.Default.PermissionOutcomeResult);

    static string? TryGetToolTitle(JsonElement toolCall) =>
        toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

    static string? TryGetToolCallId(JsonElement toolCall) =>
        toolCall.ValueKind == JsonValueKind.Object && toolCall.TryGetProperty("toolCallId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
