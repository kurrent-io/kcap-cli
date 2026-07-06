// test/Capacitor.Cli.Tests.Unit/Acp/AcpInteractionBridgeTests.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// AI-686: <see cref="AcpInteractionBridge"/> parses an inbound <c>session/request_permission</c>
/// (spec-derived shape, NOT probe-confirmed — see <c>docs/acp-probe-findings.md</c>) or capability-
/// gated <c>elicitation/create</c> server request, forwards it to an injected
/// "ask the server" delegate (standing in for <see cref="Capacitor.Cli.Daemon.Services.ServerConnection.RequestAcpInteractionAsync"/>),
/// and maps the returned <see cref="AcpInteractionDecision"/> back to the ACP JSON-RPC result
/// shape. Unit-tested against the delegate directly — no real SignalR connection involved.
/// </summary>
public class AcpInteractionBridgeTests {
    const string AgentId      = "agent-1";
    const string AcpSessionId = "fc2e09cf-f4b0-4463-9dc1-bda11268896b";

    static JsonElement PermissionRequestParams(string[] optionIds) {
        var optionsJson = string.Join(",", optionIds.Select(id => $$"""{"optionId":"{{id}}","name":"{{id}}","kind":"allow_once"}"""));
        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[{{optionsJson}}]}""";
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    /// <summary>
    /// Round-7 spec-review Finding 5 (test consistency): the "selected" case must supply a REAL,
    /// matching <see cref="AcpInteractionDecision.SelectedOptionId"/> — asserting `selected(thatOptionId)`
    /// — never a null <c>SelectedOptionId</c> that happens to map to the first offered option. An
    /// earlier draft of this test resolved with `new AcpInteractionDecision("allow", null, ...)` and
    /// still asserted `optionId == "allow-once"` (the first offered option), which contradicted this
    /// same file's own fail-closed tests below (`RequestPermission_AffirmativeOutcomeButNoSelectedOptionId_MapsToCancelled_NeverFirstOption`,
    /// `RequestPermission_SingleOptionOffered_NoSelectedOptionId_StillMapsToCancelled`) — a null
    /// <c>SelectedOptionId</c> must map to <c>cancelled</c>, NEVER <c>selected</c>/first-option. This
    /// test now supplies an explicit, resolvable `SelectedOptionId: "allow-once"` so it proves the
    /// GENUINE "selected" path (an id that matches one of the offered options) without relying on
    /// the removed first-option fallback.
    /// </summary>
    [Test]
    public async Task RequestPermission_SelectedOutcome_ReturnsSelectedResultWithMatchingOptionId() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "allow-once", "Allow", null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-once");
    }

    [Test]
    public async Task RequestPermission_DenyOutcome_ReturnsCancelledResult() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("deny", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    [Test]
    public async Task RequestPermission_CancelOutcome_ReturnsCancelledResult() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    [Test]
    public async Task RequestPermission_ServerCallThrows_ReturnsCancelledResultDefensively() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => throw new InvalidOperationException("connection dropped"),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 3(b): connection-closed / runtime-disposing / CT-cancelled while this
    /// interaction is pending — the SAME path <c>AcpHostedAgentRuntime.DisposeAsync</c> triggers by
    /// cancelling its <c>_cts</c>, which flows through <c>AcpConnection</c>'s read loop into this
    /// bridge's <c>ct</c> parameter and ultimately into <c>PendingAcpInteractionRegistry.AwaitDecisionAsync</c>'s
    /// own cancellation registration (Task B2), which throws <see cref="OperationCanceledException"/>.
    /// PRE-FIX, this exception type was excluded from the bridge's catch clause and propagated
    /// uncaught, letting <c>AcpConnection.HandleServerRequestAsync</c>'s generic catch-all convert
    /// it to a JSON-RPC "Internal error" (code -32603) instead of a well-formed ACP <c>cancelled</c>
    /// outcome. This test proves the bridge itself now produces the well-formed shape.
    /// </summary>
    [Test]
    public async Task RequestPermission_ConnectionCancelled_ReturnsCancelledResultNotUnhandledException() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromException<AcpInteractionDecision>(new OperationCanceledException("connection closing")),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    [Test]
    public async Task RequestPermission_MissingParams_ReturnsCancelledResultWithoutCallingServer() {
        var called = false;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => { called = true; return Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)); },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", Params: null);

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(called).IsFalse();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Fresh-review finding (this revision, closing the last gap in spec-review Finding 2): an
    /// earlier draft of this fix left a "no SelectedOptionId at all → fall back to the first
    /// offered option" defensive path for a RECOGNIZED AFFIRMATIVE outcome — e.g. a UI/decision-submit
    /// path that only knows "the user said yes" without echoing back a specific chosen option. That
    /// is the SAME class of silent-grant bug spec-review Finding 2 targets: an ACP options request
    /// with two-or-more offered options (here, "allow-once" and "deny") must NEVER resolve to
    /// `options[0]` just because a caller forgot (or was unable) to supply which option was chosen.
    /// This test proves the FIXED behavior: `allow_always` (a recognized affirmative outcome) with
    /// `SelectedOptionId: null` maps to `cancelled`, NEVER `selected`. There is no code path left in
    /// `MapPermissionDecision` that can produce a `selected` result without an explicit, resolvable
    /// `SelectedOptionId` matching one of the offered options — see the next-but-one test for the
    /// unresolvable-id half of the same fail-closed guarantee.
    /// </summary>
    [Test]
    public async Task RequestPermission_AffirmativeOutcomeButNoSelectedOptionId_MapsToCancelled_NeverFirstOption() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow_always", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        // No SelectedOptionId was supplied — must be cancelled, NEVER "selected"/"allow-once".
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(outcome.TryGetProperty("optionId", out _)).IsFalse();
    }

    /// <summary>
    /// Same fail-closed guarantee as above, for a single-option permission prompt specifically —
    /// proves the fix does not special-case "only one option was offered" as an implicit "assume
    /// that one." A caller MUST echo back the single option's id explicitly; omitting it still maps
    /// to cancelled even when there is only one option that COULD have been meant.
    /// </summary>
    [Test]
    public async Task RequestPermission_SingleOptionOffered_NoSelectedOptionId_StillMapsToCancelled() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 6: proves resolution is by <see cref="AcpInteractionDecision.SelectedOptionId"/>,
    /// NEVER by re-matching <see cref="AcpInteractionDecision.SelectedOptionLabel"/> — duplicate
    /// labels across two DIFFERENT offered options must resolve to the option whose
    /// <c>optionId</c> was actually selected, not "whichever option happens to have this label
    /// first" (the old label-matching behavior this finding replaces).
    /// </summary>
    [Test]
    public async Task RequestPermission_DuplicateLabels_ResolvesByOptionIdNotFirstLabelMatch() {
        var bridge = new AcpInteractionBridge(
            // Both offered options are labelled "Allow" — only OptionId disambiguates which one
            // the human actually picked. Reordered relative to the wire order below on purpose.
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "allow-second", "Allow", null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","toolCall":{"toolCallId":"call-1","title":"Run ls"},"options":[{"optionId":"allow-first","name":"Allow","kind":"allow_once"},{"optionId":"allow-second","name":"Allow","kind":"allow_always"},{"optionId":"deny","name":"Deny","kind":"reject_once"}]}""";
        var request = new AcpRequest(1, "session/request_permission", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        // Must be "allow-second" (matched by id) — a label-based (or first-match) mapper would
        // have wrongly returned "allow-first", since that's the first option labelled "Allow".
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("allow-second");
    }

    /// <summary>
    /// Spec-review Finding 6: an unresolvable <see cref="AcpInteractionDecision.SelectedOptionId"/>
    /// (doesn't match any offered option's <c>optionId</c>) is treated as CANCELLED, not a silent
    /// grant via a first-option fallback — an id that was explicitly supplied but doesn't match
    /// anything offered indicates a correlation bug or a stale/replayed decision, and granting an
    /// unrelated option in that case would be worse than failing safe.
    /// </summary>
    [Test]
    public async Task RequestPermission_UnresolvableSelectedOptionId_TreatedAsCancelled() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", "does-not-exist", "Allow", null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 2 (the security-critical fail-safe half): ANY outcome string that is
    /// neither a recognized affirmative outcome (<c>allow</c>/<c>allow_once</c>/<c>allow_always</c>/
    /// <c>answered</c>) NOR a recognized negative outcome (<c>deny</c>/<c>cancel</c>) must map to
    /// <c>cancelled</c> — NEVER fall through to <c>selected</c>/<c>options[0]</c>. Before this fix,
    /// <c>MapPermissionDecision</c> only special-cased the literal strings <c>"deny"</c>/<c>"cancel"</c>
    /// and treated every other string (including a typo'd <c>"cancelled"</c>, or any future/unknown
    /// outcome) as "select the first option" — i.e. an unrecognized outcome silently GRANTED
    /// permission. This test uses the exact typo (<c>"cancelled"</c> instead of the canonical
    /// <c>"cancel"</c>, per Task A2's Interfaces note) that a server-side regression would produce,
    /// proving the daemon-side mapper is fail-safe even if the server ever sends a string outside
    /// the documented vocabulary.
    /// </summary>
    [Test]
    public async Task RequestPermission_UnrecognizedOutcome_MapsToCancelled_NeverFallsThroughToSelected() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancelled", null, null, null, null, null)), // NOT the canonical "cancel"
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "session/request_permission", PermissionRequestParams(["allow-once", "deny"]));

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
        await Assert.That(outcome.TryGetProperty("optionId", out _)).IsFalse(); // never selects an option for an unmapped outcome
    }

    [Test]
    public async Task UnrecognizedMethod_ReturnsNullResultSoConnectionSendsMethodNotFound() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("allow", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var request = new AcpRequest(1, "fs/read_text_file", Params: JsonDocument.Parse("{}").RootElement.Clone());

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task ElicitationCreate_NeverAdvertisedButHandledDefensivelyIfSent() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => {
                // Spec-review Finding 6: resolution is by SelectedOptionId ("yes"), not by label —
                // SelectedOptionLabel is passed too but is display-only from this point forward.
                return Task.FromResult(new AcpInteractionDecision("answered", "yes", "Yes", 0, null, null));
            },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","message":"Proceed?","options":[{"optionId":"yes","name":"Yes","kind":"allow_once"},{"optionId":"no","name":"No","kind":"reject_once"}]}""";
        var request = new AcpRequest(1, "elicitation/create", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("selected");
        await Assert.That(outcome.GetProperty("optionId").GetString()).IsEqualTo("yes");
    }

    /// <summary>
    /// Spec-review Finding 1 (JSON-Schema elicitation): a schema-shaped <c>elicitation/create</c>
    /// (<c>requestedSchema</c> present, no <c>options</c> at all) is forwarded to the server
    /// verbatim via <see cref="AcpInteractionRequest.RequestedSchema"/> — this bridge does zero
    /// schema validation/rendering of its own (no confirmed Cursor shape exists to render against;
    /// the generic-card fallback lives server-side, Task A4/A6). This test also proves the request
    /// never throws even though it has no options to offer a human — <see cref="MapPermissionDecision"/>'s
    /// existing <c>options.Count == 0 → cancelled</c> branch (spec-review Finding 2) already handles
    /// "no options were ever offered" safely, so a schema-only elicitation that somehow resolves
    /// with an affirmative outcome still degrades to a well-formed <c>cancelled</c> result rather
    /// than an unhandled exception or a bogus <c>selected</c> with no <c>optionId</c> to report.
    /// </summary>
    [Test]
    public async Task ElicitationCreate_SchemaShaped_ForwardsRequestedSchemaVerbatim_NeverThrows() {
        AcpInteractionRequest? captured = null;
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => {
                captured = req;

                return Task.FromResult(new AcpInteractionDecision("answered", null, null, null, "free text answer", null));
            },
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$$$$"""{"sessionId":"{{{{{AcpSessionId}}}}}","message":"Describe the config","requestedSchema":{"type":"object","properties":{"name":{"type":"string"}}}}""";
        var request = new AcpRequest(1, "elicitation/create", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.Value.RequestedSchema).IsNotNull();
        await Assert.That(captured.Value.RequestedSchema!.Value.GetProperty("type").GetString()).IsEqualTo("object");
        await Assert.That(captured.Value.Options).IsEmpty(); // no flat options offered for a schema-shaped elicitation
        // No options were offered, so even an "answered" decision safely degrades to cancelled
        // (MapPermissionDecision's options.Count == 0 branch, spec-review Finding 2) — never throws.
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }

    /// <summary>
    /// Spec-review Finding 1: a malformed <c>requestedSchema</c> value that still parses as valid
    /// JSON (the whole <c>elicitation/create</c> params object is syntactically valid, so
    /// <see cref="ElicitationCreateParams"/> deserializes successfully) never causes this bridge to
    /// throw — it is forwarded to the server exactly as received, since schema hygiene/rejection is
    /// entirely a server-side concern (Task A1's <c>CapAcpSchema</c>), not this bridge's.
    /// </summary>
    [Test]
    public async Task ElicitationCreate_SchemaIsNotAnObject_StillForwardsWithoutThrowing() {
        var bridge = new AcpInteractionBridge(
            requestInteraction: (req, ct) => Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null)),
            agentId: AgentId,
            logger: NullLogger.Instance);

        var json = $$"""{"sessionId":"{{AcpSessionId}}","message":"Proceed?","requestedSchema":"not-an-object"}""";
        var request = new AcpRequest(1, "elicitation/create", JsonDocument.Parse(json).RootElement.Clone());

        var result = await bridge.HandleAsync(request, AcpSessionId, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        var outcome = result!.Value.GetProperty("outcome");
        await Assert.That(outcome.GetProperty("outcome").GetString()).IsEqualTo("cancelled");
    }
}
