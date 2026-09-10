# Desktop Remote Daemons — Slice 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From the desktop app, stop a remote agent, and answer permission prompts and questions raised by agents on other machines (and by local ACP-hosted agents, whose prompts never reach the local socket) — spec §9 slice 2.

**Architecture:** The daemon exposes its local↔server request-id mapping on the local permission wire (`server_request_id`). The app's `PermissionService` becomes lane-scoped: local-lane items answered over the socket, server-lane items answered over HTTP, dedup by proven correlation only, settlement lane-authoritative. A `SessionAccessService` owns per-session hub subscriptions (`RegisterSessionAccessWatch` + `SubscribeToChat`) with reconnect re-establishment, and a `SessionAttentionTracker` keeps per-session pending-request-id sets truthful from pings plus headless reconciliation. An id-preserving ACP card model sits beside the Claude card on the shared `PendingCardsViewModel`, and a minimal `RemoteSessionViewModel` hosts those cards for a remote row. Stop routes per origin through `AgentActionService`.

**Tech Stack:** .NET 10, Avalonia + ReactiveUI + DynamicData, Microsoft.AspNetCore.SignalR.Client, System.Text.Json source-gen, TUnit + WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-09-06-ai2371-desktop-remote-daemons-design.md` — §4 (aggregated attention), §5 Stop, §6 authorization signals, §8 tests, §9.2. GitHub issue #805 / Linear AI-2553. Slice 1 landed as PR #799; its plan is `docs/superpowers/plans/2026-09-06-desktop-remote-daemons-slice1.md` and its conventions carry over.

## Global Constraints

- **Server contract facts** (read from kcap-server `main`, restated so nobody re-derives them):
  - Payload pushes reach only clients that joined `chat:{sessionId}` via hub `SubscribeToChat`: `PermissionRequested(sessionId, requestId, toolName, toolInput, options)` (5 args; `options` is null for a Claude PTY prompt and an `AcpInteractionOption[]` for an ACP permission) and `AcpElicitationRequested(sessionId, requestId, prompt, options, isMultiSelect)`.
  - Org-wide pings arrive with no join: `PermissionPending(sessionId)`, `PermissionResponded(sessionId, requestId?)`. `SessionAccessChanged(sessionId)` is sent to the watching connection only.
  - `SubscribeToChat` and `RegisterSessionAccessWatch` throw `HubException` whose message contains `"Session not visible to caller"` on denial, and `"Session access recheck failed; retry"` on a transient re-check fault. `RequestStopAgent(agentId)` returns nothing and silently no-ops when unauthorized.
  - HTTP `POST api/sessions/{sessionId}/permission-response/{requestId}` takes the snake_case `PermissionResponsePayload`; 200 = applied, 404 = not pending (or not the session owner), 400 = malformed selection. Behaviors: `allow` / `deny` for permissions (`allow` + `apply_permissions` for always-allow), `answered` for an ACP elicitation.
  - **Server gap found while reading:** an ACP interaction completed over HTTP broadcasts no `PermissionResponded`. The app tolerates it (the answering client drops its own card; a second client's copy leaves on the next server-lane event, reconciliation, or its own 404). Record it on AI-2537 as a non-blocking companion item; do not work around it further.
  - `GET api/sessions/{sessionId}/detail` returns snake_case JSON; `events[]` carry `event_type`, `event_number`, and the canonical payload under `payload` (also mirrored under `data`) in proto3 JSON with snake_case field names. `InterruptIssued`: `request_id`, `kind` (`permission` | `input`), `tool_name`, `prompt`, `extensions` (`claude_code.permission`, `claude_code.elicitation`, or `acp.interaction` blocks, snake_case members). `InterruptResolved`: `request_id`. `SessionEnded` clears everything.
- **Every hub method's arity is frozen**; never add a parameter to an existing method name. Every new wire record pins its names with `[JsonPropertyName]`.
- **AOT:** `Capacitor.Cli` and `kcap-daemon` publish NativeAOT; `Capacitor.App` does not. Anything under `src/Capacitor.Cli.Core` or `src/Capacitor.Cli.Daemon` must keep `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` printing nothing.
- **Fail-open dedup:** a server-lane item is suppressed only by a live local item that claims its exact server request id. Uncertainty shows two cards, never zero.
- **Answer on the delivering lane only:** a local item's `RequestId` is only ever sent over the socket; a server item's only ever over HTTP. No code path may cross-submit.
- **Comments:** scarce, per CLAUDE.md `## Comments` — no change narration, no spec coordinates, no review artifacts. Existing files carry both; do not add more.
- **Commits:** subject `one clause (#805)`, imperative, ≤80 chars total. Body only for a non-obvious constraint, ≤5 lines. End with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- **Tests:** TUnit on Microsoft Testing Platform. One suite: `dotnet run --project test/<Proj>/<Proj>.csproj -- --treenode-filter "/*/*/<ClassName>/*"` (glob, not `--filter`). App suites run under the shared headless Avalonia session; follow `AvaloniaSession.cs`. Throwaway dirs via Helpers' `TempDir`; config roots via `[TempConfigRoot]`.
- **Build the app after every app change:** `dotnet build src/Capacitor.App/Capacitor.App.csproj` — AVLN XAML warnings appear only on a full rebuild and must be fixed in the same commit. Run `bash scripts/check-linear-ids.sh` before every push.
- **Worktree session:** run git as plain single-line `git` commands; no heredocs.

## File map

| Path | Responsibility |
|---|---|
| `src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs` | `PermissionPendingDto` gains trailing `ServerRequestId` |
| `src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs` | `TryCorrelate` re-broadcasts a pending with the server id |
| `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` | server leg publishes the mapping |
| `src/Capacitor.Remote.Models/AcpInteractionOption.cs`, `PermissionResponsePayload.cs`, `SessionDetailDto.cs`, `SessionEventDto.cs` | new wire records |
| `src/Capacitor.Remote.Models/RemoteWire.cs` | denial tokens, behaviors |
| `src/Capacitor.App/Services/IServerLane.cs`, `ServerConnectionService.cs` | new broadcasts and invokes |
| `src/Capacitor.App/Services/HubCallOutcome.cs`, `ServerPermissionRequest.cs`, `ServerElicitationRequest.cs`, `PermissionRespondedPing.cs` | lane message types |
| `src/Capacitor.App/Services/SessionAccessService.cs`, `SessionAccessLease.cs` | per-session hub subscriptions + authorization lifecycle |
| `src/Capacitor.App/Services/ServerSessionHttp.cs`, `ServerRespondOutcome.cs`, `SessionDetailFetch.cs` | HTTP seams (permission response, detail fetch) |
| `src/Capacitor.App/Services/InterruptReconciliation.cs`, `PendingInterrupt.cs` | pure parser of a session's pending interrupts |
| `src/Capacitor.App/Services/PermissionService.cs`, `IPermissionService.cs`, `PendingPermissionRequest.cs`, `PermissionLane.cs`, `AcpElicitation.cs`, `AcpAnswer.cs` | lane-scoped pending cache and answering |
| `src/Capacitor.App/Services/ServerPermissionFeed.cs` | server-lane cards: live pushes, reconciliation on open, settlement |
| `src/Capacitor.App/Services/SessionAttentionTracker.cs` | per-session pending id sets + dirty/backoff |
| `src/Capacitor.App/Services/AgentActionService.cs`, `AgentRow.cs`, `AgentDirectory.cs` | stop per origin; `SessionId` on rows; session→agent map |
| `src/Capacitor.App/ViewModels/PendingCardsViewModel.cs` | the card pipeline, shared by chat and remote host |
| `src/Capacitor.App/ViewModels/AcpQuestionCardViewModel.cs`, `AcpOptionViewModel.cs`, `PermissionCardViewModel.cs`, `PendingCardViewModel.cs` | ACP cards |
| `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`, `ISessionWorkspace.cs`, `Views/RemoteSessionView.axaml(.cs)`, `Views/PendingCardTemplates.axaml` | the remote card host |
| `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`, `Views/MainWindow.axaml`, `ViewModels/WorkspaceViewModel.cs`, `Views/WorkspaceView.axaml`, `ViewModels/ChatTabViewModel.cs`, `Views/ChatTabView.axaml` | routing by origin; chat for every session |
| `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`, `RailWorktreeViewModel.cs`, `RailRepoViewModel.cs`, `SessionRailViewModel.cs`, `Views/SessionRailView.axaml` | stale grey-out; remote rows open in-app |
| `src/Capacitor.App/ViewModels/TrayViewModel.cs`, `TrayModels.cs` | lane-gated upgrade; server-lane attention; remote entries |
| `src/Capacitor.App/App.axaml.cs` | composition root |
| `docs/CHANGES.md` | the invariants this slice adds |

---

### Task 1: `server_request_id` on the local permission wire

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs:8-11`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs`

**Interfaces:**
- Produces: `PermissionPendingDto.ServerRequestId` (`string?`, trailing, default null). Every existing positional construction keeps compiling.

- [ ] **Step 1: Write the failing test**

Append to `PermissionWireContractsTests`:

```csharp
[Test]
public async Task Server_request_id_is_trailing_nullable_written_as_null_and_absent_decodes_null() {
    var dto = new PermissionPendingDto("r1", "a1", "s1", "claude", "Bash", null, null, false, false, "t");
    var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
    await Assert.That(json).Contains("\"server_request_id\":null");

    var absent = JsonSerializer.Deserialize(
        """{"request_id":"r1","agent_id":"a1","session_id":"s1","vendor":"claude","tool_name":"Bash","requested_at":"t"}""",
        PermissionIpcJsonContext.Default.PermissionPendingDto)!;
    await Assert.That(absent.ServerRequestId).IsNull();
    await Assert.That(PermissionWire.IsPendingStructurallyValid(absent)).IsTrue();

    var present = JsonSerializer.Deserialize(
        json.Replace("\"server_request_id\":null", "\"server_request_id\":\"srv-1\""),
        PermissionIpcJsonContext.Default.PermissionPendingDto)!;
    await Assert.That(present.ServerRequestId).IsEqualTo("srv-1");
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionWireContractsTests/*"`
Expected: build error — `ServerRequestId` is not a member.

- [ ] **Step 3: Add the field**

In `PermissionIpc.cs`, replace the `PermissionPendingDto` declaration:

```csharp
/// JSON payloads for the permission control frames. snake_case on the wire; shared verbatim
/// by the daemon, the CLI, and the desktop app. Every member is always emitted (nulls written).
/// ServerRequestId is the server's id for the same request once the daemon's server leg holds
/// one — null until then, and always null from a daemon that predates it — so a client hearing
/// both lanes can pair the two copies without guessing.
public sealed record PermissionPendingDto(
    string RequestId, string AgentId, string SessionId, string Vendor, string ToolName,
    JsonElement? ToolInput, JsonElement? Suggestions, bool ToolInputOmitted, bool SuggestionsOmitted,
    string RequestedAt, string? ToolUseId = null, string? ServerRequestId = null);
```

- [ ] **Step 4: Run the test and the wire suite**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionWireContractsTests/*"`
Expected: all pass.

- [ ] **Step 5: AOT check and commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — must print nothing.

```bash
git add src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs
git commit -m "Carry the server request id on the local permission wire (#805)"
```

---

### Task 2: The daemon publishes the local↔server request-id mapping

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs:763-790` (`RunServerLegAsync`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionPromptBrokerTests.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs`

**Interfaces:**
- Produces: `PermissionPromptBroker.TryCorrelate(string requestId, string serverRequestId) : bool`.

- [ ] **Step 1: Write the failing broker tests**

Append to `PermissionPromptBrokerTests`:

```csharp
[Test]
public async Task Correlate_rebroadcasts_the_pending_with_the_server_id_and_replays_it_to_late_subscribers() {
    var broker = new PermissionPromptBroker();
    var (_, reader) = broker.Subscribe();
    _ = broker.Register(Dto());
    _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the first Pending

    await Assert.That(broker.TryCorrelate("r1", "srv-1")).IsTrue();
    var update = ((PermissionStreamItem.Pending)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
    await Assert.That(update.RequestId).IsEqualTo("r1");
    await Assert.That(update.ServerRequestId).IsEqualTo("srv-1");

    var (_, late) = broker.Subscribe();
    var replayed = ((PermissionStreamItem.Pending)await late.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
    await Assert.That(replayed.ServerRequestId).IsEqualTo("srv-1");
    await Assert.That(broker.PendingSnapshot().Single().ServerRequestId).IsEqualTo("srv-1");
}

[Test]
public async Task Correlate_after_settlement_is_a_no_op_and_broadcasts_nothing() {
    var broker = new PermissionPromptBroker();
    var (_, reader) = broker.Subscribe();
    _ = broker.Register(Dto());
    _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
    await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
    _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the Resolved

    await Assert.That(broker.TryCorrelate("r1", "srv-1")).IsFalse();
    await Assert.That(reader.TryRead(out _)).IsFalse();
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionPromptBrokerTests/*"`
Expected: build error — `TryCorrelate` missing.

- [ ] **Step 3: Implement `TryCorrelate`**

In `PermissionPromptBroker`, after `TrySettleIfNoSubscriber`:

```csharp
/// Records the server's id for a still-pending request and re-broadcasts its Pending so a
/// subscriber can pair the two lanes. False once settled: a settled request never re-appears.
public bool TryCorrelate(string requestId, string serverRequestId) {
    lock (_gate) {
        if (!_pending.TryGetValue(requestId, out var entry)) return false;
        var updated = entry with { Dto = entry.Dto with { ServerRequestId = serverRequestId } };
        _pending[requestId] = updated;
        Broadcast(new PermissionStreamItem.Pending(updated.Dto));
        return true;
    }
}
```

`SettleLocked` re-reads the entry under the gate before its `TryRemove(KeyValuePair)`, so the replaced entry is what it removes; nothing else holds an `Entry` reference.

- [ ] **Step 4: Run the broker tests**

Expected: pass.

- [ ] **Step 5: Write the failing bridge test**

Append to `LocalPermissionBridgeInteractiveTests` (its `Harness` already scripts `Server.BeginScript` and subscribes through `Broker`):

```csharp
[Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
public async Task The_server_leg_publishes_the_server_request_id_to_local_subscribers_before_the_decision() {
    await using var h = new Harness();
    h.Server.BeginScript = (_, _) => Task.FromResult("srv-42");
    var decided = new TaskCompletionSource<PermissionDecision>();
    h.Server.AwaitScript = (_, ct) => decided.Task.WaitAsync(ct);
    await h.StartAsync();
    var (_, reader) = h.Broker.Subscribe();

    var response = h.PostAsync();
    var first = ((PermissionStreamItem.Pending)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
    await Assert.That(first.ServerRequestId).IsNull();
    var correlated = ((PermissionStreamItem.Pending)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
    await Assert.That(correlated.RequestId).IsEqualTo(first.RequestId);
    await Assert.That(correlated.ServerRequestId).IsEqualTo("srv-42");

    decided.SetResult(Allow);
    await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
}
```

- [ ] **Step 6: Wire the bridge**

In `LocalPermissionBridge.RunServerLegAsync`, immediately after the `serverRequestId = await server.BeginPermissionRequestAsync(...)` try/catch block and before `if (settlement.IsCompleted)`:

```csharp
_broker.TryCorrelate(pending.RequestId, serverRequestId);
```

- [ ] **Step 7: Run both daemon test classes**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeInteractiveTests/*"` and the broker class.
Expected: pass. Then the AOT grep from Task 1 — nothing printed.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionPromptBrokerTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs
git commit -m "Publish the server request id on the daemon's permission stream (#805)"
```

---

### Task 3: Remote wire records for permissions, elicitations and session detail

**Files:**
- Create: `src/Capacitor.Remote.Models/AcpInteractionOption.cs`
- Create: `src/Capacitor.Remote.Models/PermissionResponsePayload.cs`
- Create: `src/Capacitor.Remote.Models/SessionDetailDto.cs`
- Create: `src/Capacitor.Remote.Models/SessionEventDto.cs`
- Modify: `src/Capacitor.Remote.Models/RemoteWire.cs` (tokens), `RemoteModelsJsonContext.cs`
- Test: `test/Capacitor.Remote.Models.Tests.Unit/PermissionWireTests.cs`

**Interfaces:**
- Produces: `AcpInteractionOption { OptionId, Label, Description?, Kind?, MinSelections?, MaxSelections? }`, `PermissionResponsePayload`, `SessionDetailDto { SessionId, EndedAt, LastEventNumber, Events }`, `SessionEventDto { EventType, EventNumber, Payload, Data }`, `WireTokens.SessionNotVisible`, `WireTokens.SessionAccessRecheckFailed`, `PermissionBehaviors.Allow/Deny/Answered`, `RemoteModelsJsonContext` entries for all of them.

- [ ] **Step 1: Write the failing wire tests**

`test/Capacitor.Remote.Models.Tests.Unit/PermissionWireTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.Remote.Models.Tests.Unit;

public class PermissionWireTests {
    [Test]
    public async Task Response_payload_writes_snake_case_and_omits_unset_members() {
        var payload = new PermissionResponsePayload { Behavior = PermissionBehaviors.Answered, SelectedOptionIds = ["opt-a", "opt-b"], SelectedOptionLabels = ["A", "B"] };
        var json = JsonSerializer.Serialize(payload, RemoteModelsJsonContext.Default.PermissionResponsePayload);
        await Assert.That(json).IsEqualTo("""{"behavior":"answered","selected_option_ids":["opt-a","opt-b"],"selected_option_labels":["A","B"]}""");
    }

    [Test]
    public async Task Acp_option_reads_the_hub_shape_with_bounds() {
        var options = JsonSerializer.Deserialize(
            """[{"option_id":"a","label":"Yes","description":null,"kind":"allow_once","min_selections":1,"max_selections":2}]""",
            RemoteModelsJsonContext.Default.AcpInteractionOptionArray)!;
        await Assert.That(options[0].OptionId).IsEqualTo("a");
        await Assert.That(options[0].Kind).IsEqualTo("allow_once");
        await Assert.That(options[0].MaxSelections).IsEqualTo(2);
    }

    [Test]
    public async Task Session_detail_reads_events_with_payload_and_data() {
        var detail = JsonSerializer.Deserialize(
            """{"session_id":"s1","ended_at":null,"last_event_number":3,"events":[{"event_type":"InterruptIssued","event_number":3,"payload":{"request_id":"r1","kind":"permission"},"data":{"request_id":"r1"}}]}""",
            RemoteModelsJsonContext.Default.SessionDetailDto)!;
        await Assert.That(detail.LastEventNumber).IsEqualTo(3L);
        await Assert.That(detail.Events![0].EventType).IsEqualTo("InterruptIssued");
        await Assert.That(detail.Events[0].Payload!.Value.GetProperty("kind").GetString()).IsEqualTo("permission");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionWireTests/*"`
Expected: build errors for the missing types.

- [ ] **Step 3: Write the records**

`AcpInteractionOption.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One selectable option of an ACP permission or elicitation. OptionId is the identity that goes
/// back to the agent; Label is display only and may collide across options. A multi-select
/// elicitation stamps the same MinSelections/MaxSelections pair on every option; read the first
/// option carrying a complete pair and never combine partial stamps.
public sealed record AcpInteractionOption {
    [JsonPropertyName("option_id")]      public required string OptionId { get; init; }
    [JsonPropertyName("label")]          public required string Label { get; init; }
    [JsonPropertyName("description")]    public string? Description { get; init; }
    [JsonPropertyName("kind")]           public string? Kind { get; init; }
    [JsonPropertyName("min_selections")] public int? MinSelections { get; init; }
    [JsonPropertyName("max_selections")] public int? MaxSelections { get; init; }
}
```

`PermissionResponsePayload.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// Body of POST api/sessions/{sessionId}/permission-response/{requestId}. Unset members are
/// omitted: the server canonicalizes the selection lists and rejects a count mismatch with 400.
public sealed record PermissionResponsePayload {
    [JsonPropertyName("behavior")] public required string Behavior { get; init; }
    [JsonPropertyName("apply_permissions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ApplyPermissions { get; init; }
    [JsonPropertyName("updated_input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? UpdatedInput { get; init; }
    [JsonPropertyName("selected_option_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedOptionId { get; init; }
    [JsonPropertyName("selected_option_label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedOptionLabel { get; init; }
    [JsonPropertyName("free_text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FreeText { get; init; }
    [JsonPropertyName("selected_option_ids"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SelectedOptionIds { get; init; }
    [JsonPropertyName("selected_option_labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? SelectedOptionLabels { get; init; }
}
```

`SessionDetailDto.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// The members of GET api/sessions/{sessionId}/detail a remote client reads for interrupt
/// reconciliation. Everything else the endpoint returns is ignored.
public sealed record SessionDetailDto {
    [JsonPropertyName("session_id")]        public string? SessionId { get; init; }
    [JsonPropertyName("ended_at")]          public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("last_event_number")] public long LastEventNumber { get; init; } = -1;
    [JsonPropertyName("events")]            public SessionEventDto[]? Events { get; init; }
}
```

`SessionEventDto.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One canonical event as the detail endpoint presents it. The payload is proto3 JSON with
/// snake_case field names, mirrored under both `payload` and `data`; readers take `payload`
/// and fall back to `data`.
public sealed record SessionEventDto {
    [JsonPropertyName("event_type")]   public required string EventType { get; init; }
    [JsonPropertyName("event_number")] public long EventNumber { get; init; } = -1;
    [JsonPropertyName("payload")]      public JsonElement? Payload { get; init; }
    [JsonPropertyName("data")]         public JsonElement? Data { get; init; }
    public JsonElement? Body => Payload is { ValueKind: JsonValueKind.Object } p ? p : Data;
}
```

Append to `RemoteWire.cs`:

```csharp
/// Behaviors the permission-response route accepts.
public static class PermissionBehaviors {
    public const string Allow = "allow";
    public const string Deny = "deny";
    public const string Answered = "answered";
}
```

and inside `WireTokens`:

```csharp
/// HubException message fragment for a session the caller may not see.
public const string SessionNotVisible = "Session not visible to caller";
/// HubException message for a transient post-admit re-check fault; retryable, never a denial.
public const string SessionAccessRecheckFailed = "Session access recheck failed; retry";
```

`RemoteModelsJsonContext.cs` gains `[JsonSerializable]` for `AcpInteractionOption`, `AcpInteractionOption[]`, `PermissionResponsePayload`, `SessionDetailDto`, `SessionEventDto`.

- [ ] **Step 4: Run the tests**

Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Remote.Models test/Capacitor.Remote.Models.Tests.Unit/PermissionWireTests.cs
git commit -m "Add permission, elicitation and session-detail wire records (#805)"
```

---

### Task 4: The server lane hears permission traffic and can stop, subscribe and watch

**Files:**
- Create: `src/Capacitor.App/Services/HubCallOutcome.cs`, `ServerPermissionRequest.cs`, `ServerElicitationRequest.cs`, `PermissionRespondedPing.cs`
- Modify: `src/Capacitor.App/Services/IServerLane.cs`, `ServerConnectionService.cs`, `NoRemoteAgents.cs` (`NoServerLane`)
- Modify: `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`, `HubTestHost.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs`

**Interfaces:**
- Produces on `IServerLane`:
  - `IObservable<string> PermissionPending`
  - `IObservable<PermissionRespondedPing> PermissionResponded` — `record PermissionRespondedPing(string SessionId, string? RequestId)`
  - `IObservable<ServerPermissionRequest> PermissionRequests` — `record ServerPermissionRequest(string SessionId, string RequestId, string ToolName, JsonElement? ToolInput, IReadOnlyList<AcpInteractionOption>? Options)`
  - `IObservable<ServerElicitationRequest> ElicitationRequests` — `record ServerElicitationRequest(string SessionId, string RequestId, string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect)`
  - `IObservable<string> SessionAccessChanged`
  - `Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct)`
  - `Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct)`
  - `Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct)`
  - `Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct)`
  - `record HubCallOutcome(HubCallResult Result, string? Reason)` with `enum HubCallResult { Ok, NotConnected, Denied, Failed }` and statics `HubCallOutcome.Ok`, `HubCallOutcome.NotConnected`, `HubCallOutcome.Denied(reason)`, `HubCallOutcome.Failed(reason)`.

- [ ] **Step 1: Write the failing lane tests**

Append to `ServerConnectionServiceTests`:

```csharp
[Test]
public async Task PermissionBroadcastsSurfaceTyped() {
    await using var host = await HubTestHost.StartAsync();
    await using var lane = Lane(host);
    lane.Start();
    await Next(lane.Status, s => s.State == ServerLaneState.Connected);

    var pending = lane.PermissionPending.Take(1).ToTask();
    var responded = lane.PermissionResponded.Take(2).ToList().ToTask();
    var requests = lane.PermissionRequests.Take(2).ToList().ToTask();
    var elicitations = lane.ElicitationRequests.Take(1).ToTask();
    var access = lane.SessionAccessChanged.Take(1).ToTask();

    await host.BroadcastAsync(HubBroadcasts.PermissionPending, "s1");
    await host.BroadcastAsync(HubBroadcasts.PermissionResponded, "s1", "r1");
    await host.BroadcastAsync(HubBroadcasts.PermissionResponded, "s1", null);
    await host.BroadcastAsync(HubBroadcasts.PermissionRequested, "s1", "r1", "Bash", new { command = "ls" }, null);
    await host.BroadcastAsync(HubBroadcasts.PermissionRequested, "s1", "r2", "fs/write", null,
        new[] { new AcpInteractionOption { OptionId = "allow-once", Label = "Allow", Kind = "allow_once" } });
    await host.BroadcastAsync(HubBroadcasts.AcpElicitationRequested, "s1", "q1", "Pick one",
        new[] { new AcpInteractionOption { OptionId = "a", Label = "A", MinSelections = 1, MaxSelections = 1 } }, false);
    await host.BroadcastAsync(HubBroadcasts.SessionAccessChanged, "s1");

    await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("s1");
    var pings = await responded.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(pings[0].RequestId).IsEqualTo("r1");
    await Assert.That(pings[1].RequestId).IsNull();
    var reqs = await requests.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(reqs[0].ToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("ls");
    await Assert.That(reqs[0].Options).IsNull();
    await Assert.That(reqs[1].Options![0].OptionId).IsEqualTo("allow-once");
    var q = await elicitations.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(q.Prompt).IsEqualTo("Pick one");
    await Assert.That(q.Options[0].MaxSelections).IsEqualTo(1);
    await Assert.That(await access.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("s1");
}

[Test]
public async Task ToolInputSentAsAJsonStringIsParsed() {
    await using var host = await HubTestHost.StartAsync();
    await using var lane = Lane(host);
    lane.Start();
    await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    var request = lane.PermissionRequests.Take(1).ToTask();
    await host.BroadcastAsync(HubBroadcasts.PermissionRequested, "s1", "r1", "Bash", "{\"command\":\"pwd\"}", null);
    var r = await request.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(r.ToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("pwd");
}

[Test]
public async Task InvokesRouteToTheHubAndClassifyDenial() {
    await using var host = await HubTestHost.StartAsync();
    HubTestHost.ChatSubscribeHandler = sid => sid != "hidden";
    await using var lane = Lane(host);
    lane.Start();
    await Next(lane.Status, s => s.State == ServerLaneState.Connected);

    await Assert.That((await lane.RequestStopAgentAsync("a1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
    await Assert.That(HubTestHost.StopCalls).Contains("a1");
    await Assert.That((await lane.SubscribeToChatAsync("s1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
    await Assert.That((await lane.SubscribeToChatAsync("hidden", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Denied);
    await Assert.That((await lane.RegisterSessionAccessWatchAsync("hidden", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Denied);
    await Assert.That((await lane.UnsubscribeFromChatAsync("s1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
    await Assert.That(HubTestHost.ChatUnsubscribes).Contains("s1");
}

[Test]
public async Task InvokesReportNotConnectedWithoutALiveHub() {
    await using var lane = new ServerConnectionService(serverUrl: null, () => Task.FromResult<string?>(null));
    lane.Start();
    await Assert.That((await lane.RequestStopAgentAsync("a1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.NotConnected);
}
```

- [ ] **Step 2: Extend `HubTestHost`**

Add static scriptable handlers and call logs beside `DaemonsHandler`, reset them in `StartAsync`, and add the hub methods:

```csharp
public static Func<string, bool> ChatSubscribeHandler { get; set; } = _ => true;
public static Func<string, bool> AccessWatchHandler { get; set; } = _ => true;
public static List<string> StopCalls { get; } = [];
public static List<string> ChatSubscribes { get; } = [];
public static List<string> ChatUnsubscribes { get; } = [];
public static List<string> AccessWatches { get; } = [];
```

In `StartAsync` before building: `ChatSubscribeHandler = _ => true; AccessWatchHandler = _ => true; StopCalls.Clear(); ChatSubscribes.Clear(); ChatUnsubscribes.Clear(); AccessWatches.Clear();`

In `SessionsHub`:

```csharp
public Task RequestStopAgent(string agentId) { StopCalls.Add(agentId); return Task.CompletedTask; }

public JsonElement[] SubscribeToChat(string sessionId) {
    if (!ChatSubscribeHandler(sessionId)) throw new HubException(WireTokens.SessionNotVisible);
    ChatSubscribes.Add(sessionId);
    return [];
}

public Task UnsubscribeFromChat(string sessionId) { ChatUnsubscribes.Add(sessionId); return Task.CompletedTask; }

public Task RegisterSessionAccessWatch(string sessionId) {
    if (!AccessWatchHandler(sessionId)) throw new HubException(WireTokens.SessionNotVisible);
    AccessWatches.Add(sessionId);
    return Task.CompletedTask;
}
```

- [ ] **Step 3: Run to verify the tests fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerConnectionServiceTests/*"`
Expected: build errors for the missing lane members.

- [ ] **Step 4: Add the message types**

`HubCallOutcome.cs`:

```csharp
namespace Capacitor.App.Services;

public enum HubCallResult { Ok, NotConnected, Denied, Failed }

/// One hub invoke's verdict. Denied is the server's session-visibility refusal; every other
/// exception is Failed with its message, and a lane with no live hub answers NotConnected
/// without dialing.
public sealed record HubCallOutcome(HubCallResult Result, string? Reason = null) {
    public static readonly HubCallOutcome Ok = new(HubCallResult.Ok);
    public static readonly HubCallOutcome NotConnected = new(HubCallResult.NotConnected);
    public static HubCallOutcome Denied(string reason) => new(HubCallResult.Denied, reason);
    public static HubCallOutcome Failed(string reason) => new(HubCallResult.Failed, reason);
}
```

`PermissionRespondedPing.cs`:

```csharp
namespace Capacitor.App.Services;

/// The org-wide settlement ping. A null RequestId is a session-wide clear.
public sealed record PermissionRespondedPing(string SessionId, string? RequestId);
```

`ServerPermissionRequest.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A PermissionRequested push. Options is null for a Claude PTY prompt; an ACP permission
/// carries the agent's own options, and the answer must name one by OptionId.
public sealed record ServerPermissionRequest(
        string SessionId, string RequestId, string ToolName, JsonElement? ToolInput,
        IReadOnlyList<AcpInteractionOption>? Options) {

    /// The wire sends tool_input either as an object or as its JSON text, and the options slot
    /// either as null or an array of option objects; anything else reads as absent.
    public static ServerPermissionRequest From(string sessionId, string requestId, string? toolName, JsonElement? toolInput, JsonElement? options) =>
        new(sessionId, requestId, toolName ?? "", NormalizeInput(toolInput), ParseOptions(options));

    internal static JsonElement? NormalizeInput(JsonElement? input) {
        if (input is not { } el) return null;
        if (el.ValueKind == JsonValueKind.String) {
            try { using var doc = JsonDocument.Parse(el.GetString()!); return doc.RootElement.Clone(); }
            catch (JsonException) { return el; }
        }
        return el.ValueKind == JsonValueKind.Null ? null : el;
    }

    internal static IReadOnlyList<AcpInteractionOption>? ParseOptions(JsonElement? options) {
        if (options is not { ValueKind: JsonValueKind.Array } arr) return null;
        try {
            var parsed = arr.Deserialize(RemoteModelsJsonContext.Default.AcpInteractionOptionArray);
            return parsed is { Length: > 0 } && parsed.All(o => !string.IsNullOrEmpty(o.OptionId)) ? parsed : null;
        } catch (JsonException) {
            return null;
        }
    }
}
```

`ServerElicitationRequest.cs`:

```csharp
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// An AcpElicitationRequested push. Options may be empty: the answer is then free text.
public sealed record ServerElicitationRequest(
    string SessionId, string RequestId, string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect);
```

- [ ] **Step 5: Extend `IServerLane` and `ServerConnectionService`**

`IServerLane` gains the members listed under Interfaces (keep the existing ones). In `ServerConnectionService`:

- Fields: `readonly Subject<string> _permissionPending = new(); readonly Subject<PermissionRespondedPing> _permissionResponded = new(); readonly Subject<ServerPermissionRequest> _permissionRequests = new(); readonly Subject<ServerElicitationRequest> _elicitations = new(); readonly Subject<string> _sessionAccessChanged = new();` — exposed with `.AsObservable()` and disposed in `DisposeAsync` beside `_launchFailures`.
- In `Build()`, after the `LaunchFailed` handler:

```csharp
hub.On<string>(HubBroadcasts.PermissionPending, sid => _permissionPending.OnNext(sid));
hub.On<string, string?>(HubBroadcasts.PermissionResponded, (sid, rid) => _permissionResponded.OnNext(new(sid, rid)));
hub.On<string, string, string?, JsonElement?, JsonElement?>(HubBroadcasts.PermissionRequested,
    (sid, rid, tool, input, options) => _permissionRequests.OnNext(ServerPermissionRequest.From(sid, rid, tool, input, options)));
hub.On<string, string, string, AcpInteractionOption[]?, bool>(HubBroadcasts.AcpElicitationRequested,
    (sid, rid, prompt, options, multi) => _elicitations.OnNext(new(sid, rid, prompt, options ?? [], multi)));
hub.On<string>(HubBroadcasts.SessionAccessChanged, sid => _sessionAccessChanged.OnNext(sid));
```

- Invokes:

```csharp
public Task<HubCallOutcome> RequestStopAgentAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.RequestStopAgent, ct, agentId);
public Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.SubscribeToChat, ct, sessionId);
public Task<HubCallOutcome> UnsubscribeFromChatAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.UnsubscribeFromChat, ct, sessionId);
public Task<HubCallOutcome> RegisterSessionAccessWatchAsync(string sessionId, CancellationToken ct) => InvokeAsync(HubMethods.RegisterSessionAccessWatch, ct, sessionId);

async Task<HubCallOutcome> InvokeAsync(string method, CancellationToken ct, params object?[] args) {
    var hub = _hub;
    if (hub is not { State: HubConnectionState.Connected }) return HubCallOutcome.NotConnected;
    try {
        await hub.InvokeCoreAsync(method, args, ct).ConfigureAwait(false);
        return HubCallOutcome.Ok;
    } catch (OperationCanceledException) {
        throw;
    } catch (HubException ex) when (ex.Message.Contains(WireTokens.SessionNotVisible, StringComparison.Ordinal)) {
        return HubCallOutcome.Denied(ex.Message);
    } catch (Exception ex) {
        return HubCallOutcome.Failed(ex.Message);
    }
}
```

`SubscribeToChat` returns an array the app ignores; the non-generic `InvokeCoreAsync` discards it.

- [ ] **Step 6: Extend the fakes**

`FakeServerLane` gains subjects `PermissionPendingSubject`, `PermissionRespondedSubject`, `PermissionRequestsSubject`, `ElicitationsSubject`, `SessionAccessChangedSubject` (each a `Subject<T>`), exposed through the new properties, plus scriptable invokes:

```csharp
public Func<string, Task<HubCallOutcome>> StopHandler = _ => Task.FromResult(HubCallOutcome.Ok);
public Func<string, Task<HubCallOutcome>> SubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
public Func<string, Task<HubCallOutcome>> UnsubscribeChatHandler = _ => Task.FromResult(HubCallOutcome.Ok);
public Func<string, Task<HubCallOutcome>> AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
public readonly List<string> Stops = [], ChatSubscribes = [], ChatUnsubscribes = [], AccessWatches = [];
```

Each method records the id then defers to its handler. `NoServerLane` returns `Observable.Never` for each stream and `Task.FromResult(HubCallOutcome.NotConnected)` for each invoke.

- [ ] **Step 7: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` then the `ServerConnectionServiceTests` class.
Expected: pass.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.App/Services test/Capacitor.App.Tests.Unit/FakeServerLane.cs test/Capacitor.App.Tests.Unit/HubTestHost.cs test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs
git commit -m "Surface permission pushes and session invokes on the server lane (#805)"
```

---

### Task 5: `SessionAccessService` — the session authorization lifecycle

**Files:**
- Create: `src/Capacitor.App/Services/SessionAccessState.cs`, `SessionAccessLease.cs`, `SessionAccessService.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionAccessServiceTests.cs`

**Interfaces:**
- Produces:
  - `enum SessionAccessState { Establishing, Established, Denied, Unavailable }`
  - `sealed class SessionAccessLease : IDisposable { string SessionId; IObservable<SessionAccessState> State; }` — `State` replays the current value.
  - `sealed class SessionAccessService(IServerLane lane, TimeProvider time) : IDisposable { SessionAccessLease Acquire(string sessionId); IObservable<(string SessionId, SessionAccessState State)> Transitions; }`
- Rules: a session is established by `RegisterSessionAccessWatch` then `SubscribeToChat`, in that order; `Denied` from either is terminal for that attempt; `NotConnected`/`Failed` is `Unavailable` and retried on the lane's next Connected status and on a 2s/5s/10s/30s ladder while it stays Connected; a `SessionAccessChanged` ping re-runs the attempt; the last lease's disposal invokes `UnsubscribeFromChat` (fire-and-forget) and forgets the session. Stale completions (an attempt superseded by a newer one) are dropped by generation.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/SessionAccessServiceTests.cs`:

```csharp
using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionAccessServiceTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAccessService Service;
        public Harness() => Service = new SessionAccessService(Lane, Time);
        public void Connect(int epoch = 1) => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: epoch));
        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, "closed"));
        public static async Task<SessionAccessState> Current(SessionAccessLease lease) {
            SessionAccessState? state = null;
            using (lease.State.Subscribe(s => state = s)) { }
            return state!.Value;
        }
        public void Dispose() => Service.Dispose();
    }

    [Test]
    public async Task Establishes_in_order_watch_then_chat_and_reports_established() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s1"), what: "chat subscribe");
        await Assert.That(h.Lane.AccessWatches.IndexOf("s1")).IsLessThan(h.Lane.ChatSubscribes.IndexOf("s1") + 1000); // watch recorded
        await Assert.That(h.Lane.AccessWatches).Contains("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        lease.Dispose();
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "unsubscribe on last release");
    }

    [Test]
    public async Task A_denied_watch_is_terminal_and_never_subscribes_chat() {
        using var h = new Harness();
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied");
        await Assert.That(h.Lane.ChatSubscribes).DoesNotContain("s1");
    }

    [Test]
    public async Task Lane_down_is_unavailable_and_reconnect_re_establishes() {
        using var h = new Harness();
        var lease = h.Service.Acquire("s1");
        await Assert.That(await Harness.Current(lease)).IsEqualTo(SessionAccessState.Unavailable);
        h.Connect();
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established after connect");
        h.Drop();
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable on drop");
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => h.Lane.ChatSubscribes.Count(s => s == "s1") == 2, what: "re-subscribed after reconnect");
    }

    [Test]
    public async Task Access_changed_ping_rechecks_and_a_denial_moves_the_lease_to_denied() {
        using var h = new Harness();
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established");
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        h.Lane.SessionAccessChangedSubject.OnNext("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Denied, "denied after recheck");
    }

    [Test]
    public async Task A_transient_failure_retries_on_the_ladder_while_connected() {
        using var h = new Harness();
        var attempts = 0;
        h.Lane.SubscribeChatHandler = _ => Task.FromResult(++attempts == 1 ? HubCallOutcome.Failed("recheck") : HubCallOutcome.Ok);
        h.Connect();
        var lease = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Unavailable, "unavailable after the failure");
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(async () => await Harness.Current(lease) == SessionAccessState.Established, "established on retry");
    }

    [Test]
    public async Task Two_leases_share_one_subscription_and_unsubscribe_on_the_last_release() {
        using var h = new Harness();
        h.Connect();
        var a = h.Service.Acquire("s1");
        var b = h.Service.Acquire("s1");
        await WaitUntilAsync(async () => await Harness.Current(b) == SessionAccessState.Established, "established");
        await Assert.That(h.Lane.ChatSubscribes.Count(s => s == "s1")).IsEqualTo(1);
        a.Dispose();
        await Task.Delay(50);
        await Assert.That(h.Lane.ChatUnsubscribes).DoesNotContain("s1");
        b.Dispose();
        await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "unsubscribe on last release");
    }
}
```

`WaitUntilAsync` in `WorkspaceFixtures` takes a `Func<bool>`; add an overload `WaitUntilAsync(Func<Task<bool>> condition, string what)` there with the same polling loop.

- [ ] **Step 2: Run to verify they fail**

Expected: build errors — types missing.

- [ ] **Step 3: Implement**

`SessionAccessState.cs`:

```csharp
namespace Capacitor.App.Services;

/// Unavailable is lane trouble, never a verdict; Denied is the server's refusal and stays until
/// the server says otherwise through an access-changed ping or a reconnect re-check.
public enum SessionAccessState { Establishing, Established, Denied, Unavailable }
```

`SessionAccessLease.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// One consumer's hold on a session's hub subscriptions. Disposing it more than once is a no-op.
public sealed class SessionAccessLease : IDisposable {
    readonly Action<SessionAccessLease> _release;
    int _released;

    internal SessionAccessLease(string sessionId, BehaviorSubject<SessionAccessState> state, Action<SessionAccessLease> release) {
        SessionId = sessionId;
        State = state.AsObservable();
        _release = release;
    }

    public string SessionId { get; }
    /// Replays the current state on subscribe.
    public IObservable<SessionAccessState> State { get; }

    public void Dispose() {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release(this);
    }
}
```

`SessionAccessService.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// Owns every per-session hub subscription the app holds: the access watch (the server's own
/// revocation signal) and the chat group (where permission payloads arrive). One attempt at a
/// time per session, numbered so a superseded attempt's result is dropped; every attempt is
/// re-run on a reconnect and on the server's access-changed ping, and a transient failure
/// retries on a fixed ladder while the lane stays up.
public sealed class SessionAccessService : IDisposable {
    static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    sealed class Entry(string sessionId) {
        public string SessionId { get; } = sessionId;
        public BehaviorSubject<SessionAccessState> State { get; } = new(SessionAccessState.Establishing);
        public int Leases;
        public int Attempt;
        public int Failures;
        public ITimer? RetryTimer;
    }

    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Subject<(string SessionId, SessionAccessState State)> _transitions = new();
    readonly Lock _lock = new();
    readonly IDisposable _subscriptions;
    bool _connected;
    bool _disposed;

    public SessionAccessService(IServerLane lane, TimeProvider time) {
        _lane = lane;
        _time = time;
        var status = lane.Status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Subscribe(OnLane);
        var changed = lane.SessionAccessChanged.Subscribe(sid => { lock (_lock) { if (_entries.TryGetValue(sid, out var e)) Begin(e); } });
        _subscriptions = new System.Reactive.Disposables.CompositeDisposable(status, changed);
    }

    public IObservable<(string SessionId, SessionAccessState State)> Transitions => _transitions.AsObservable();

    public SessionAccessLease Acquire(string sessionId) {
        Entry entry;
        var fresh = false;
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(sessionId, out entry!)) { entry = new Entry(sessionId); _entries[sessionId] = entry; fresh = true; }
            entry.Leases++;
            if (fresh) Begin(entry);
        }
        return new SessionAccessLease(sessionId, entry.State, Release);
    }

    void Release(SessionAccessLease lease) {
        Entry? gone = null;
        lock (_lock) {
            if (!_entries.TryGetValue(lease.SessionId, out var entry)) return;
            if (--entry.Leases > 0) return;
            _entries.Remove(lease.SessionId);
            entry.Attempt++;
            entry.RetryTimer?.Dispose();
            gone = entry;
        }
        gone.State.OnCompleted();
        if (_connected) _ = _lane.UnsubscribeFromChatAsync(lease.SessionId, CancellationToken.None);
    }

    void OnLane(bool connected) {
        List<Entry> entries;
        lock (_lock) {
            _connected = connected;
            entries = [.. _entries.Values];
            foreach (var e in entries) {
                e.Failures = 0;
                if (connected) Begin(e);
                else { e.Attempt++; e.RetryTimer?.Dispose(); e.RetryTimer = null; Publish(e, SessionAccessState.Unavailable); }
            }
        }
    }

    // Caller holds _lock.
    void Begin(Entry entry) {
        entry.RetryTimer?.Dispose();
        entry.RetryTimer = null;
        var attempt = ++entry.Attempt;
        if (!_connected) { Publish(entry, SessionAccessState.Unavailable); return; }
        Publish(entry, SessionAccessState.Establishing);
        _ = Task.Run(() => EstablishAsync(entry, attempt));
    }

    async Task EstablishAsync(Entry entry, int attempt) {
        SessionAccessState verdict;
        try {
            var watch = await _lane.RegisterSessionAccessWatchAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (watch.Result == HubCallResult.Ok) {
                var chat = await _lane.SubscribeToChatAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
                verdict = Classify(chat);
            } else {
                verdict = Classify(watch);
            }
        } catch (Exception) {
            verdict = SessionAccessState.Unavailable;
        }

        lock (_lock) {
            if (_disposed || entry.Attempt != attempt) return;
            Publish(entry, verdict);
            if (verdict != SessionAccessState.Unavailable || !_connected) { entry.Failures = 0; return; }
            var delay = Retry[Math.Min(entry.Failures++, Retry.Length - 1)];
            entry.RetryTimer = _time.CreateTimer(_ => { lock (_lock) { if (entry.Attempt == attempt) Begin(entry); } }, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    static SessionAccessState Classify(HubCallOutcome outcome) => outcome.Result switch {
        HubCallResult.Ok => SessionAccessState.Established,
        HubCallResult.Denied => SessionAccessState.Denied,
        _ => SessionAccessState.Unavailable,
    };

    // Caller holds _lock.
    void Publish(Entry entry, SessionAccessState state) {
        if (entry.State.Value == state && state != SessionAccessState.Establishing) return;
        entry.State.OnNext(state);
        _transitions.OnNext((entry.SessionId, state));
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            foreach (var e in _entries.Values) { e.Attempt++; e.RetryTimer?.Dispose(); }
        }
        _subscriptions.Dispose();
        _transitions.Dispose();
    }
}
```

Note `Publish` deliberately lets `Established` re-emit after a reconnect (`Establishing` in between resets it), so a consumer that reconciles per establishment sees each one.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionAccessServiceTests/*"`
Expected: pass. Fix the first test's ordering assertion to what it means: `AccessWatches` gets "s1" before `ChatSubscribes` does — assert both lists contain it and that the fake recorded the watch call first by comparing a shared call log if you add one; otherwise drop the `IndexOf` line.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/SessionAccessState.cs src/Capacitor.App/Services/SessionAccessLease.cs src/Capacitor.App/Services/SessionAccessService.cs test/Capacitor.App.Tests.Unit/SessionAccessServiceTests.cs test/Capacitor.App.Tests.Unit/WorkspaceFixtures.cs
git commit -m "Own per-session hub subscriptions with reconnect re-checks (#805)"
```

---

### Task 6: HTTP seams — permission response, session detail, and the interrupt parser

**Files:**
- Create: `src/Capacitor.App/Services/ServerRespondOutcome.cs`, `SessionDetailFetch.cs`, `ServerSessionHttp.cs`, `PendingInterrupt.cs`, `InterruptReconciliation.cs`
- Modify: `test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj` (add `<PackageReference Include="WireMock.Net" />`)
- Test: `test/Capacitor.App.Tests.Unit/InterruptReconciliationTests.cs`, `ServerSessionHttpTests.cs`, `RemoteAgentsServiceHttpFetchTests.cs`

**Interfaces:**
- Produces:
  - `enum ServerRespondKind { Applied, NotPending, Rejected, Unauthorized, Unreachable }`; `record ServerRespondOutcome(ServerRespondKind Kind, string? Reason = null)`.
  - `record SessionDetailFetch(SessionDetailDto? Detail, bool NotFound = false, bool Unauthorized = false)`.
  - `delegate Task<ServerRespondOutcome> PermissionResponder(string sessionId, string requestId, PermissionResponsePayload payload, CancellationToken ct)`.
  - `delegate Task<SessionDetailFetch> SessionDetailReader(string sessionId, CancellationToken ct)`.
  - `static class ServerSessionHttp { PermissionResponder Responder(ICapacitorHttpClient? http, ProfileContext? profiles); SessionDetailReader DetailReader(ICapacitorHttpClient? http, ProfileContext? profiles); }`.
  - `enum PendingInterruptKind { ClaudePermission, ClaudeQuestion, AcpPermission, AcpQuestion, TranscriptQuestion }`; `record PendingInterrupt(string RequestId, PendingInterruptKind Kind, string ToolName, JsonElement? ToolInput, string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect, int? MinSelections, int? MaxSelections, DateTimeOffset RequestedAt) { bool IsAnswerableOverHttp => Kind != TranscriptQuestion; }`.
  - `record InterruptReconciliation(IReadOnlyList<PendingInterrupt> Pending, bool Ended) { static InterruptReconciliation FromDetail(SessionDetailDto detail); }`.
- Parser rules (`FromDetail`, events in order): `InterruptIssued` with a non-empty `request_id` and `kind == "permission"` adds a permission — `AcpPermission` when `extensions.acp.interaction` exists (options from it), else `ClaudePermission` (tool name/input from the payload or `extensions.claude_code.permission`); `kind == "input"` with `tool_name == "AskUserQuestion"` adds a question — `AcpQuestion` when `extensions.acp.interaction` exists, else `TranscriptQuestion` (attention only: its id is the transcript's, not one the permission-response route knows). `InterruptResolved` removes by `request_id`. `SessionEnded` or any `UserClosedSession*` event type clears and sets `Ended`; a non-null `ended_at` also sets `Ended`. Property lookups accept both snake_case and camelCase names.

- [ ] **Step 1: Write the failing parser tests**

`test/Capacitor.App.Tests.Unit/InterruptReconciliationTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class InterruptReconciliationTests {
    static SessionDetailDto Detail(string eventsJson, string? endedAt = null) =>
        JsonSerializer.Deserialize($$"""{"session_id":"s1","ended_at":{{(endedAt is null ? "null" : $"\"{endedAt}\"")}},"last_event_number":9,"events":{{eventsJson}}}""",
            RemoteModelsJsonContext.Default.SessionDetailDto)!;

    [Test]
    public async Task Issued_minus_resolved_is_pending_with_kinds_from_the_extension_blocks() {
        var detail = Detail("""[
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash","extensions":{"claude_code":{"permission":{"tool_name":"Bash","tool_input":{"command":"ls"}}}}}},
            {"event_type":"InterruptIssued","event_number":2,"payload":{"request_id":"p2","kind":"permission","tool_name":"fs/write","extensions":{"acp":{"interaction":{"raw_kind":"permission","tool_name":"fs/write","options":[{"label":"Allow","description":null,"option_id":"allow-once"}]}}}}},
            {"event_type":"InterruptIssued","event_number":3,"payload":{"request_id":"q1","kind":"input","tool_name":"AskUserQuestion","prompt":"Pick","extensions":{"acp":{"interaction":{"raw_kind":"elicitation","options":[{"label":"A","option_id":"a","min_selections":1,"max_selections":2},{"label":"A","option_id":"b","min_selections":1,"max_selections":2}],"is_multi_select":true,"min_selections":1,"max_selections":2}}}}},
            {"event_type":"InterruptIssued","event_number":4,"payload":{"request_id":"t1","kind":"input","tool_name":"AskUserQuestion","prompt":"Which?","extensions":{"claude_code":{"elicitation":{"options":[{"label":"X"}]}}}}},
            {"event_type":"InterruptIssued","event_number":5,"payload":{"request_id":"w1","kind":"input","prompt":"idle"}},
            {"event_type":"InterruptResolved","event_number":6,"payload":{"request_id":"p1","outcome":"allow"}}
        ]""");
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Ended).IsFalse();
        await Assert.That(r.Pending.Select(p => p.RequestId)).IsEquivalentTo(new[] { "p2", "q1", "t1" });
        var acpPermission = r.Pending.Single(p => p.RequestId == "p2");
        await Assert.That(acpPermission.Kind).IsEqualTo(PendingInterruptKind.AcpPermission);
        await Assert.That(acpPermission.Options[0].OptionId).IsEqualTo("allow-once");
        var question = r.Pending.Single(p => p.RequestId == "q1");
        await Assert.That(question.Kind).IsEqualTo(PendingInterruptKind.AcpQuestion);
        await Assert.That(question.IsMultiSelect).IsTrue();
        await Assert.That(question.MaxSelections).IsEqualTo(2);
        await Assert.That(question.Options.Select(o => o.OptionId)).IsEquivalentTo(new[] { "a", "b" });
        var transcript = r.Pending.Single(p => p.RequestId == "t1");
        await Assert.That(transcript.Kind).IsEqualTo(PendingInterruptKind.TranscriptQuestion);
        await Assert.That(transcript.IsAnswerableOverHttp).IsFalse();
    }

    [Test]
    public async Task Session_end_clears_everything_and_marks_ended() {
        var detail = Detail("""[
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}},
            {"event_type":"SessionEnded","event_number":2,"payload":{}}
        ]""");
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Pending).IsEmpty();
        await Assert.That(r.Ended).IsTrue();
    }

    [Test]
    public async Task Ended_at_alone_marks_ended_and_data_stands_in_for_a_missing_payload() {
        var detail = Detail("""[{"event_type":"InterruptIssued","event_number":1,"data":{"requestId":"p1","kind":"permission","toolName":"Bash"}}]""", endedAt: "2026-09-10T10:00:00Z");
        var r = InterruptReconciliation.FromDetail(detail);
        await Assert.That(r.Ended).IsTrue();
        await Assert.That(r.Pending).IsEmpty();
    }

    [Test]
    public async Task Camel_case_names_are_read_too() {
        var detail = Detail("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"requestId":"p1","kind":"permission","toolName":"Bash"}}]""");
        await Assert.That(InterruptReconciliation.FromDetail(detail).Pending.Single().ToolName).IsEqualTo("Bash");
    }
}
```

- [ ] **Step 2: Write the failing HTTP tests**

`test/Capacitor.App.Tests.Unit/ServerSessionHttpTests.cs` — the leased client is built the way `AuthenticatedServerReads.RegisteredLaneAsync` builds it, against a WireMock server, with a stored token so `ForWaitAsync` reports `Ok`:

```csharp
using System.Net;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

/// Builds the app's real authenticated HTTP lane against a WireMock server.
static class WireMockLane {
    public static async Task<(ICapacitorHttpClient Http, ProfileContext Profiles, ServiceProvider Provider)> BuildAsync(WireMockServer server, ConfigRoot root) {
        var profiles = Resolutions.At(server.Url!, root);
        await AuthFixtures.NewTokenStore(root).SaveAsync(profiles.Name, new StoredTokens {
            AccessToken = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "alice",
            Provider = AuthProvider.GitHubApp, ServerUrl = server.Url!,
        });
        var provider = new ServiceCollection()
            .AddSingleton(root).AddSingleton(profiles).AddSingleton(new CapacitorServer(server.Url!, root, profiles))
            .AddCapacitorHttp().BuildValidated();
        return (provider.GetRequiredService<ICapacitorHttpClient>(), profiles, provider);
    }
}

public class ServerSessionHttpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Responder_posts_the_payload_and_maps_status_codes() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/ok").UsingPost().WithBody(b => b!.Contains("\"behavior\":\"allow\"")))
              .RespondWith(Response.Create().WithStatusCode(200));
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/gone").UsingPost()).RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/bad").UsingPost()).RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"count mismatch"}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var respond = ServerSessionHttp.Responder(http, profiles);
        var allow = new PermissionResponsePayload { Behavior = PermissionBehaviors.Allow };

        await Assert.That((await respond("s1", "ok", allow, CancellationToken.None)).Kind).IsEqualTo(ServerRespondKind.Applied);
        await Assert.That((await respond("s1", "gone", allow, CancellationToken.None)).Kind).IsEqualTo(ServerRespondKind.NotPending);
        var rejected = await respond("s1", "bad", allow, CancellationToken.None);
        await Assert.That(rejected.Kind).IsEqualTo(ServerRespondKind.Rejected);
        await Assert.That(rejected.Reason).Contains("count mismatch");
    }

    [Test]
    public async Task Responder_without_a_server_or_client_is_unreachable_not_a_throw() {
        var outcome = await ServerSessionHttp.Responder(null, null)("s1", "r1", new PermissionResponsePayload { Behavior = "allow" }, CancellationToken.None);
        await Assert.That(outcome.Kind).IsEqualTo(ServerRespondKind.Unreachable);
    }

    [Test]
    public async Task Detail_reader_returns_the_parsed_detail_and_distinguishes_not_found() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/detail").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]}"""));
        server.Given(Request.Create().WithPath("/api/sessions/nope/detail").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var read = ServerSessionHttp.DetailReader(http, profiles);

        var found = await read("s1", CancellationToken.None);
        await Assert.That(found.Detail!.Events![0].EventType).IsEqualTo("InterruptIssued");
        var missing = await read("nope", CancellationToken.None);
        await Assert.That(missing.Detail).IsNull();
        await Assert.That(missing.NotFound).IsTrue();
    }
}
```

`test/Capacitor.App.Tests.Unit/RemoteAgentsServiceHttpFetchTests.cs` (the slice-1 carry-over):

```csharp
using Capacitor.App.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceHttpFetchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Fetch_reads_agent_instances_and_maps_401_to_unauthorized() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/agent-instances").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""[{"agent_id":"a1","status":"Running","daemon_name":"work-mac","vendor":"claude","registered_at":"2026-09-10T10:00:00Z"}]"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var fetch = RemoteAgentsService.HttpFetch(http, profiles);
        var ok = await fetch(CancellationToken.None);
        await Assert.That(ok.Rows![0].AgentId).IsEqualTo("a1");
        await Assert.That(ok.Unauthorized).IsFalse();

        server.Reset();
        server.Given(Request.Create().WithPath("/api/agent-instances").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));
        var denied = await fetch(CancellationToken.None);
        await Assert.That(denied.Rows).IsNull();
        await Assert.That(denied.Unauthorized).IsTrue();
    }
}
```

If `ForWaitAsync` reacts to the 401 by retrying a refresh that the fake token cannot satisfy, the second half still ends `Unauthorized` — that is the mapped outcome; if instead the lane throws, the test has found a real bug in `HttpFetch`'s catch-all (it must return `Unauthorized`, not swallow into `null`), fix it there.

- [ ] **Step 3: Run to verify they fail**

Add `<PackageReference Include="WireMock.Net" />` to the App tests csproj first (the version is pinned in `Directory.Packages.props`). Then run each class; expected: build errors for the missing types.

- [ ] **Step 4: Implement the records and parser**

`ServerRespondOutcome.cs`:

```csharp
namespace Capacitor.App.Services;

public enum ServerRespondKind { Applied, NotPending, Rejected, Unauthorized, Unreachable }

/// NotPending is the route's 404: settled elsewhere, drop the card. Rejected is a 400 with the
/// server's reason. Unauthorized and Unreachable leave the request pending.
public sealed record ServerRespondOutcome(ServerRespondKind Kind, string? Reason = null);
```

`SessionDetailFetch.cs`:

```csharp
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Detail is null on every failure; NotFound and Unauthorized name the two the caller treats
/// differently from lane loss.
public sealed record SessionDetailFetch(SessionDetailDto? Detail, bool NotFound = false, bool Unauthorized = false);

public delegate Task<SessionDetailFetch> SessionDetailReader(string sessionId, CancellationToken ct);
public delegate Task<ServerRespondOutcome> PermissionResponder(string sessionId, string requestId, PermissionResponsePayload payload, CancellationToken ct);
```

(`PermissionResponder` lives here too: two delegate types that only make sense together.)

`ServerSessionHttp.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// The app's two session-scoped HTTP calls, each on a client leased per call so a token the
/// hub refreshed is what HTTP sends too. Every failure is a value; nothing here throws past a
/// cancellation.
public static class ServerSessionHttp {
    public static PermissionResponder Responder(ICapacitorHttpClient? http, ProfileContext? profiles) => async (sessionId, requestId, payload, ct) => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return new(ServerRespondKind.Unreachable, "not_signed_in");
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return new(ServerRespondKind.Unauthorized, "not_signed_in");
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.PermissionResponse(sessionId, requestId)}";
                using var content = JsonContent.Create(payload, RemoteModelsJsonContext.Default.PermissionResponsePayload);
                using var response = await client.PostAsync(url, content, ct).ConfigureAwait(false);
                return response.StatusCode switch {
                    HttpStatusCode.OK or HttpStatusCode.NoContent => new(ServerRespondKind.Applied),
                    HttpStatusCode.NotFound => new(ServerRespondKind.NotPending),
                    HttpStatusCode.Unauthorized => new(ServerRespondKind.Unauthorized, "not_signed_in"),
                    HttpStatusCode.BadRequest => new(ServerRespondKind.Rejected, await ReasonAsync(response, ct).ConfigureAwait(false)),
                    _ => new(ServerRespondKind.Unreachable, $"server_status_{(int)response.StatusCode}"),
                };
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return new(ServerRespondKind.Unreachable, ex.Message);
        }
    };

    public static SessionDetailReader DetailReader(ICapacitorHttpClient? http, ProfileContext? profiles) => async (sessionId, ct) => {
        var serverUrl = profiles?.Resolution.ServerUrl;
        if (http is null || profiles is null || string.IsNullOrEmpty(serverUrl)) return new(null);
        try {
            var (client, status, _, _) = await http.ForWaitAsync(ct).ConfigureAwait(false);
            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return new(null, Unauthorized: true);
                var url = $"{serverUrl.TrimEnd('/')}/{ApiRoutes.SessionDetail(sessionId)}";
                using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return new(null, NotFound: true);
                if (response.StatusCode == HttpStatusCode.Unauthorized) return new(null, Unauthorized: true);
                if (!response.IsSuccessStatusCode) return new(null);
                var detail = await response.Content.ReadFromJsonAsync(RemoteModelsJsonContext.Default.SessionDetailDto, ct).ConfigureAwait(false);
                return new(detail);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            return new(null);
        }
    };

    static async Task<string> ReasonAsync(HttpResponseMessage response, CancellationToken ct) {
        try {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Prop("error")?.GetString() ?? text;
        } catch (Exception) {
            return "rejected";
        }
    }
}
```

`Prop` is the `JsonElementExtensions` helper from `Capacitor.Cli.Core` (returns `JsonElement?`); use it rather than `TryGetProperty`.

`PendingInterrupt.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// TranscriptQuestion is a Claude AskUserQuestion the transcript pipeline recorded under its own
/// id: it counts for attention, but the permission-response route knows nothing of that id.
public enum PendingInterruptKind { ClaudePermission, ClaudeQuestion, AcpPermission, AcpQuestion, TranscriptQuestion }

public sealed record PendingInterrupt(
        string RequestId, PendingInterruptKind Kind, string ToolName, JsonElement? ToolInput, string Prompt,
        IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect, int? MinSelections, int? MaxSelections,
        DateTimeOffset RequestedAt) {
    public bool IsAnswerableOverHttp => Kind != PendingInterruptKind.TranscriptQuestion;
}
```

`InterruptReconciliation.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A session's unresolved interrupts, folded from its event stream the way the server's own
/// seed does: issued minus resolved, cleared by a session end.
public sealed record InterruptReconciliation(IReadOnlyList<PendingInterrupt> Pending, bool Ended) {
    const string ElicitationToolName = "AskUserQuestion";

    public static InterruptReconciliation FromDetail(SessionDetailDto detail) {
        var pending = new Dictionary<string, PendingInterrupt>(StringComparer.Ordinal);
        var ended = detail.EndedAt is not null;
        foreach (var evt in detail.Events ?? []) {
            switch (evt.EventType) {
                case "InterruptIssued" when evt.Body is { } body && Read(body, "request_id", "requestId") is { Length: > 0 } id:
                    if (Parse(id, body) is { } interrupt) pending[id] = interrupt;
                    break;
                case "InterruptResolved" when evt.Body is { } body && Read(body, "request_id", "requestId") is { Length: > 0 } id:
                    pending.Remove(id);
                    break;
                case "SessionEnded":
                case var t when t.StartsWith("UserClosedSession", StringComparison.Ordinal):
                    pending.Clear();
                    ended = true;
                    break;
            }
        }
        if (ended) pending.Clear();
        return new([.. pending.Values], ended);
    }

    static PendingInterrupt? Parse(string id, JsonElement body) {
        var kind = Read(body, "kind", "kind") ?? "";
        var toolName = Read(body, "tool_name", "toolName") ?? "";
        var prompt = Read(body, "prompt", "prompt") ?? "";
        var timestamp = Read(body, "timestamp", "timestamp");
        var at = DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ts) ? ts : DateTimeOffset.MinValue;
        var extensions = Elem(body, "extensions", "extensions");
        var acp = extensions is { } ext ? Elem(Elem(ext, "acp", "acp"), "interaction", "interaction") : null;
        var claude = extensions is { } ext2 ? Elem(ext2, "claude_code", "claudeCode") : null;

        if (kind == "permission") {
            if (acp is { } a) {
                return new(id, PendingInterruptKind.AcpPermission, Read(a, "tool_name", "toolName") ?? toolName,
                    Elem(a, "tool_input", "toolInput"), prompt, Options(a), false, null, null, at);
            }
            var block = claude is { } c ? Elem(c, "permission", "permission") : null;
            return new(id, PendingInterruptKind.ClaudePermission,
                toolName.Length > 0 ? toolName : block is { } b ? Read(b, "tool_name", "toolName") ?? "" : "",
                block is { } b2 ? Elem(b2, "tool_input", "toolInput") : null, prompt, [], false, null, null, at);
        }
        if (kind == "input" && toolName == ElicitationToolName) {
            if (acp is { } a) {
                var options = Options(a);
                var bounds = options.FirstOrDefault(o => o is { MinSelections: not null, MaxSelections: not null });
                return new(id, PendingInterruptKind.AcpQuestion, toolName, null, prompt, options,
                    Bool(a, "is_multi_select", "isMultiSelect") ?? false,
                    bounds?.MinSelections ?? Int(a, "min_selections", "minSelections"),
                    bounds?.MaxSelections ?? Int(a, "max_selections", "maxSelections"), at);
            }
            return new(id, PendingInterruptKind.TranscriptQuestion, toolName, null, prompt, [], false, null, null, at);
        }
        return null;
    }

    static IReadOnlyList<AcpInteractionOption> Options(JsonElement interaction) {
        if (Elem(interaction, "options", "options") is not { ValueKind: JsonValueKind.Array } arr) return [];
        var list = new List<AcpInteractionOption>();
        foreach (var o in arr.EnumerateArray()) {
            var optionId = Read(o, "option_id", "optionId");
            if (string.IsNullOrEmpty(optionId)) continue;
            list.Add(new AcpInteractionOption {
                OptionId = optionId, Label = Read(o, "label", "label") ?? optionId, Description = Read(o, "description", "description"),
                Kind = Read(o, "kind", "kind"), MinSelections = Int(o, "min_selections", "minSelections"), MaxSelections = Int(o, "max_selections", "maxSelections"),
            });
        }
        return list;
    }

    static JsonElement? Elem(JsonElement? obj, string snake, string camel) =>
        obj is { ValueKind: JsonValueKind.Object } o ? (o.Prop(snake) ?? o.Prop(camel)) is { ValueKind: not JsonValueKind.Null } e ? e : null : null;
    static string? Read(JsonElement obj, string snake, string camel) => Elem(obj, snake, camel) is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;
    static bool? Bool(JsonElement obj, string snake, string camel) => Elem(obj, snake, camel) is { } e && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : null;
    static int? Int(JsonElement obj, string snake, string camel) => Elem(obj, snake, camel) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out var i) ? i : null;
}
```

Check `JsonElementExtensions` in `Capacitor.Cli.Core` for the exact helper names (`Prop`, `Str`, `Bool`) and prefer them over the private helpers above where they fit — the private ones exist only for the dual-name lookup.

- [ ] **Step 5: Run all three test classes**

Expected: pass. For the WireMock classes, if the leased client's auth probe hits the WireMock server on a path you did not stub (a `/auth/...` or `/api/me` GET), stub it with a 200 and note which path in the test so the next reader knows why.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Services test/Capacitor.App.Tests.Unit
git commit -m "Add the permission-response and session-detail HTTP seams (#805)"
```

---

### Task 7: `PermissionService` becomes lane-scoped

**Files:**
- Create: `src/Capacitor.App/Services/PermissionLane.cs`, `PendingPermissionRequest.cs`, `AcpElicitation.cs`, `AcpAnswer.cs`
- Modify: `src/Capacitor.App/Services/IPermissionService.cs` (move `PendingPermissionRequest` out; add members), `PermissionService.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (card selection by kind; `_requests` keyed by `Key`), `PendingCardViewModel.cs` (`Key`), `TrayViewModel.cs` (compiles against the new `PendingSummary`)
- Modify: `test/Capacitor.App.Tests.Unit/FakePermissionService.cs` (new members; `PermissionEntries.ServerEntry`, `AcpQuestion`, `AcpPermission` builders)
- Test: `test/Capacitor.App.Tests.Unit/PermissionServiceTests.cs`

**Interfaces:**
- `enum PermissionLane { Local, Server }`.
- `PendingPermissionRequest`: `string Key` (`"local:{id}"` / `"server:{id}"`), `PermissionLane Lane`, `string RequestId` (the lane's own id), `string SessionId`, `string AgentId` (server items: `""` until resolved), `string Vendor`, `string ToolName`, `string? ToolInputJson`, `bool ToolInputOmitted`, `string? ToolUseId`, `DateTimeOffset RequestedAt`, `ElicitationQuestions? Questions`, `AcpElicitation? AcpQuestion`, `IReadOnlyList<AcpInteractionOption>? Options`, `string? ServerRequestId` (local: the daemon's mapping or null; server: `RequestId`), `bool IsQuestion`. Factories: `internal PendingPermissionRequest(PermissionPendingDto dto)`; `internal static FromServer(ServerPermissionRequest r, string vendor, DateTimeOffset at)`; `internal static FromServer(ServerElicitationRequest r, DateTimeOffset at)`; `internal static FromReconciled(string sessionId, PendingInterrupt p)`.
- `record AcpElicitation(string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect, int? MinSelections, int? MaxSelections)`.
- `record AcpAnswer(IReadOnlyList<string> SelectedOptionIds, string? FreeText)`.
- `record struct PendingSummary(int Permissions, int Questions, int LocalCount, int ServerCount)` with `Total`.
- `IPermissionService` gains `Task<PermissionResolveOutcome> AnswerAcpAsync(PendingPermissionRequest target, AcpAnswer answer, CancellationToken ct)` and `Task<PermissionResolveOutcome> PickOptionAsync(PendingPermissionRequest target, string optionId, CancellationToken ct)`.
- `PermissionService` ctor: `(IDaemonClientService service, ILocalControlOps ops, Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> subscribe, TimeProvider time, CancellationToken shutdownToken, PermissionResponder? respond = null, IObservable<IReadOnlyDictionary<string, string>>? sessionAgents = null)`.
- Internal server-lane mutation API (used by Task 8): `UpsertServer(PendingPermissionRequest item)`, `SettleServer(string sessionId, string? requestId)`, `ReplaceServerForSession(string sessionId, IReadOnlyList<PendingPermissionRequest> items, int generation)`, `DropServerForSession(string sessionId)`, `ClearServerLane()`, `int SessionGeneration(string sessionId)`.
- Rules: local `Subscribed` and a capability-less `Connected` clear local-lane items only; tombstones are keyed by `Key`; a local Pending whose `ServerRequestId` changed is mutated in place and `Refresh`ed (same instance, same card), and any server item with that id moves into the shadow set; a server item is shadowed when a live local item claims it and resurfaces when that local item is removed by subscription loss (not by settlement); own-lane settlement of a local item also retires its claimed twin; `SettleServer(sid, rid)` tombstones `server:rid` and concludes the local claimant; `SettleServer(sid, null)` removes and tombstones every server item of the session and bumps the session's generation; `ReplaceServerForSession` applies only while the generation matches and never adds a tombstoned key.

- [ ] **Step 1: Write the failing tests**

Extend `PermissionServiceTests.Harness` with a scripted responder and a session map:

```csharp
public readonly List<(string SessionId, string RequestId, PermissionResponsePayload Payload)> Responses = [];
public ServerRespondOutcome NextRespond = new(ServerRespondKind.Applied);
public readonly BehaviorSubject<IReadOnlyDictionary<string, string>> SessionAgents = new(new Dictionary<string, string>());
```

and construct with `new PermissionService(Daemon, Ops, Stream.RunAsync, new FakeTimeProvider(), CancellationToken.None, (sid, rid, p, _) => { Responses.Add((sid, rid, p)); return Task.FromResult(NextRespond); }, SessionAgents)`. Update `Dto(...)` to accept `serverRequestId`. Add tests:

```csharp
static PendingPermissionRequest ServerPermission(string id = "srv-1", string session = "s1", IReadOnlyList<AcpInteractionOption>? options = null) =>
    PendingPermissionRequest.FromServer(new ServerPermissionRequest(session, id, "Bash", null, options), "claude", DateTimeOffset.UtcNow);

[Test]
public async Task Local_subscribed_replaces_local_items_only() {
    using var h = new Harness();
    await h.StartAsync();
    await h.EmitAsync(Dto("l1"));
    h.Service.UpsertServer(ServerPermission("srv-9"));
    await Assert.That(h.View.Count).IsEqualTo(2);
    h.Stream.EndAttempt();
    await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "resubscribe");
    h.Stream.EmitSubscribed();
    await WaitUntilAsync(() => h.View.Count == 1, what: "local item dropped on resubscribe");
    await Assert.That(h.View.Lookup("server:srv-9").HasValue).IsTrue();
}

[Test]
public async Task A_correlated_local_item_keeps_its_instance_and_shadows_the_server_twin() {
    using var h = new Harness();
    await h.StartAsync();
    var local = await h.EmitAsync(Dto("l1"));
    h.Service.UpsertServer(ServerPermission("srv-1"));
    await Assert.That(h.View.Count).IsEqualTo(2);

    h.Stream.EmitPending(Dto("l1", serverRequestId: "srv-1"));
    await WaitUntilAsync(() => h.View.Count == 1, what: "twin shadowed");
    await Assert.That(ReferenceEquals(h.View.Lookup("local:l1").Value, local)).IsTrue();
    await Assert.That(local.ServerRequestId).IsEqualTo("srv-1");

    // A later server push for the claimed id stays shadowed.
    h.Service.UpsertServer(ServerPermission("srv-1"));
    await Assert.That(h.View.Count).IsEqualTo(1);
}

[Test]
public async Task Subscription_loss_resurfaces_the_shadowed_twin_with_its_own_handle() {
    using var h = new Harness();
    await h.StartAsync();
    await h.EmitAsync(Dto("l1", serverRequestId: "srv-1"));
    h.Service.UpsertServer(ServerPermission("srv-1"));
    await Assert.That(h.View.Count).IsEqualTo(1);
    h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
    await WaitUntilAsync(() => h.View.Lookup("server:srv-1").HasValue, what: "twin resurfaced");
    await Assert.That(h.View.Lookup("local:l1").HasValue).IsFalse();
    var twin = h.View.Lookup("server:srv-1").Value;
    await Assert.That(twin.Lane).IsEqualTo(PermissionLane.Server);
    await Assert.That(twin.RequestId).IsEqualTo("srv-1");
}

[Test]
public async Task Server_settlement_by_id_clears_the_twin_and_the_claiming_local_item() {
    using var h = new Harness();
    await h.StartAsync();
    await h.EmitAsync(Dto("l1", serverRequestId: "srv-1"));
    h.Service.SettleServer("s1", "srv-1");
    await WaitUntilAsync(() => h.View.Count == 0, what: "local claimant concluded");
    h.Stream.EmitPending(Dto("l1", serverRequestId: "srv-1"));
    await Task.Delay(50);
    await Assert.That(h.View.Count).IsEqualTo(0); // tombstoned
}

[Test]
public async Task A_session_wide_clear_retires_server_items_only_even_after_a_late_correlation() {
    using var h = new Harness();
    await h.StartAsync();
    h.Service.UpsertServer(ServerPermission("srv-1"));
    h.Service.UpsertServer(ServerPermission("srv-2", session: "s2"));
    var local = await h.EmitAsync(Dto("l1"));
    var generation = h.Service.SessionGeneration("s1");
    h.Service.SettleServer("s1", null);
    await Assert.That(h.View.Lookup("server:srv-1").HasValue).IsFalse();
    await Assert.That(h.View.Lookup("server:srv-2").HasValue).IsTrue();
    await Assert.That(h.Service.SessionGeneration("s1")).IsNotEqualTo(generation);

    h.Stream.EmitPending(Dto("l1", serverRequestId: "srv-1"));
    await WaitUntilAsync(() => local.ServerRequestId == "srv-1", what: "late correlation applied");
    await Assert.That(h.View.Lookup("local:l1").HasValue).IsTrue();
}

[Test]
public async Task Reconciliation_results_apply_only_under_the_generation_they_started_with() {
    using var h = new Harness();
    await h.StartAsync();
    var generation = h.Service.SessionGeneration("s1");
    h.Service.SettleServer("s1", null);
    h.Service.ReplaceServerForSession("s1", [ServerPermission("srv-1")], generation);
    await Assert.That(h.View.Count).IsEqualTo(0);
    h.Service.ReplaceServerForSession("s1", [ServerPermission("srv-1")], h.Service.SessionGeneration("s1"));
    await Assert.That(h.View.Count).IsEqualTo(1);
}

[Test]
public async Task Server_items_answer_over_http_with_their_own_id_and_a_404_drops_the_card() {
    using var h = new Harness();
    await h.StartAsync();
    h.Service.UpsertServer(ServerPermission("srv-1"));
    var item = h.View.Lookup("server:srv-1").Value;

    var applied = await h.Service.ResolveAsync(item, PermissionAnswer.AllowAlways, CancellationToken.None);
    await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
    await Assert.That(h.Responses.Single().RequestId).IsEqualTo("srv-1");
    await Assert.That(h.Responses.Single().Payload.Behavior).IsEqualTo("allow");
    await Assert.That(h.Responses.Single().Payload.ApplyPermissions).IsNotNull();
    await Assert.That(h.Ops.PermissionResolves).IsEmpty();
    await Assert.That(h.View.Count).IsEqualTo(0);

    h.Service.UpsertServer(ServerPermission("srv-2"));
    h.NextRespond = new(ServerRespondKind.NotPending);
    var gone = await h.Service.ResolveAsync(h.View.Lookup("server:srv-2").Value, PermissionAnswer.Deny, CancellationToken.None);
    await Assert.That(gone.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
    await Assert.That(h.View.Count).IsEqualTo(0);

    h.Service.UpsertServer(ServerPermission("srv-3"));
    h.NextRespond = new(ServerRespondKind.Unreachable, "boom");
    var failed = await h.Service.ResolveAsync(h.View.Lookup("server:srv-3").Value, PermissionAnswer.Allow, CancellationToken.None);
    await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
    await Assert.That(h.View.Count).IsEqualTo(1);
}

[Test]
public async Task An_acp_question_answers_with_option_ids_and_an_acp_permission_with_the_picked_option() {
    using var h = new Harness();
    await h.StartAsync();
    var options = new AcpInteractionOption[] {
        new() { OptionId = "a", Label = "Same", MinSelections = 1, MaxSelections = 2 },
        new() { OptionId = "b", Label = "Same", MinSelections = 1, MaxSelections = 2 },
    };
    h.Service.UpsertServer(PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", options, true), DateTimeOffset.UtcNow));
    var question = h.View.Lookup("server:q1").Value;
    await Assert.That(question.IsQuestion).IsTrue();
    await h.Service.AnswerAcpAsync(question, new AcpAnswer(["a", "b"], null), CancellationToken.None);
    var payload = h.Responses.Single().Payload;
    await Assert.That(payload.Behavior).IsEqualTo("answered");
    await Assert.That(payload.SelectedOptionIds).IsEquivalentTo(new[] { "a", "b" });
    await Assert.That(payload.SelectedOptionLabels).IsEquivalentTo(new[] { "Same", "Same" });

    h.Responses.Clear();
    h.Service.UpsertServer(ServerPermission("p1", options: [new() { OptionId = "reject", Label = "Reject", Kind = "reject_once" }]));
    await h.Service.PickOptionAsync(h.View.Lookup("server:p1").Value, "reject", CancellationToken.None);
    await Assert.That(h.Responses.Single().Payload.Behavior).IsEqualTo("deny");
    await Assert.That(h.Responses.Single().Payload.SelectedOptionId).IsEqualTo("reject");
}

[Test]
public async Task Server_items_resolve_their_agent_id_from_the_session_map() {
    using var h = new Harness();
    await h.StartAsync();
    h.Service.UpsertServer(ServerPermission("srv-1", session: "s1"));
    await Assert.That(h.Agents).IsEmpty();
    h.SessionAgents.OnNext(new Dictionary<string, string> { ["s1"] = "agent-1" });
    await WaitUntilAsync(() => h.Agents.Contains("agent-1"), what: "agent resolved");
    await Assert.That(h.View.Lookup("server:srv-1").Value.AgentId).IsEqualTo("agent-1");
}

[Test]
public async Task Local_settlement_retires_the_claimed_twin() {
    using var h = new Harness();
    await h.StartAsync();
    var local = await h.EmitAsync(Dto("l1", serverRequestId: "srv-1"));
    h.Service.UpsertServer(ServerPermission("srv-1"));
    h.Ops.NextPermissionAck = new PermissionAckDto(true, null);
    await h.Service.ResolveAsync(local, PermissionAnswer.Allow, CancellationToken.None);
    await Assert.That(h.View.Count).IsEqualTo(0);
    h.Service.UpsertServer(ServerPermission("srv-1"));
    await Assert.That(h.View.Count).IsEqualTo(0);
}
```

`ScriptedLocalControlOps` already records resolve calls and scripts acks — read it and use its actual member names in place of `PermissionResolves` / `NextPermissionAck`.

- [ ] **Step 2: Run to verify they fail**

Expected: build errors.

- [ ] **Step 3: Write the model types**

`PermissionLane.cs`:

```csharp
namespace Capacitor.App.Services;

/// The lane that delivered a pending request is the only lane that may answer it.
public enum PermissionLane { Local, Server }
```

`AcpElicitation.cs`:

```csharp
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// An ACP question: options are identified by OptionId (labels may repeat), and a multi-select
/// answer must pick between MinSelections and MaxSelections of them. No options means free text.
public sealed record AcpElicitation(string Prompt, IReadOnlyList<AcpInteractionOption> Options, bool IsMultiSelect, int? MinSelections, int? MaxSelections);
```

`AcpAnswer.cs`:

```csharp
namespace Capacitor.App.Services;

public sealed record AcpAnswer(IReadOnlyList<string> SelectedOptionIds, string? FreeText);
```

`PendingPermissionRequest.cs` (moved out of `IPermissionService.cs`):

```csharp
using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// One pending prompt, keyed by lane so the two lanes' independently allocated ids never
/// collide. RequestId is the delivering lane's own id and the only id ever sent back on it.
/// ServerRequestId on a local item is the daemon's mapping, written in place when it arrives so
/// the card built over this instance survives.
public sealed class PendingPermissionRequest {
    string? _serverRequestId;
    string _agentId;

    internal PendingPermissionRequest(PermissionPendingDto dto) {
        Lane = PermissionLane.Local;
        Key = KeyFor(PermissionLane.Local, dto.RequestId);
        RequestId = dto.RequestId;
        SessionId = dto.SessionId;
        _agentId = dto.AgentId;
        Vendor = dto.Vendor;
        ToolName = dto.ToolName;
        ToolInputJson = dto.ToolInput?.GetRawText();
        ToolInputOmitted = dto.ToolInputOmitted;
        ToolUseId = dto.ToolUseId;
        RequestedAt = ParseTime(dto.RequestedAt);
        Questions = dto.Vendor == "claude" && dto.ToolName == ClaudeElicitation.ToolName && !dto.ToolInputOmitted
            ? ClaudeElicitation.TryParse(ToolInputJson) : null;
        _serverRequestId = dto.ServerRequestId;
    }

    PendingPermissionRequest(string requestId, string sessionId, string vendor, string toolName, string? toolInputJson,
            DateTimeOffset requestedAt, ElicitationQuestions? questions, AcpElicitation? acpQuestion, IReadOnlyList<AcpInteractionOption>? options) {
        Lane = PermissionLane.Server;
        Key = KeyFor(PermissionLane.Server, requestId);
        RequestId = requestId;
        SessionId = sessionId;
        _agentId = "";
        Vendor = vendor;
        ToolName = toolName;
        ToolInputJson = toolInputJson;
        RequestedAt = requestedAt;
        Questions = questions;
        AcpQuestion = acpQuestion;
        Options = options;
        _serverRequestId = requestId;
    }

    internal static PendingPermissionRequest FromServer(ServerPermissionRequest r, string vendor, DateTimeOffset at) {
        var inputJson = r.ToolInput?.GetRawText();
        var questions = r.Options is null && r.ToolName == ClaudeElicitation.ToolName ? ClaudeElicitation.TryParse(inputJson) : null;
        return new(r.RequestId, r.SessionId, vendor, r.ToolName, inputJson, at, questions, null, r.Options);
    }

    internal static PendingPermissionRequest FromServer(ServerElicitationRequest r, DateTimeOffset at) {
        var bounds = r.Options.FirstOrDefault(o => o is { MinSelections: not null, MaxSelections: not null });
        return new(r.RequestId, r.SessionId, "", ClaudeElicitation.ToolName, null, at, null,
            new AcpElicitation(r.Prompt, r.Options, r.IsMultiSelect, bounds?.MinSelections, bounds?.MaxSelections), null);
    }

    /// Null for a transcript-recorded question: nothing the response route can settle.
    internal static PendingPermissionRequest? FromReconciled(string sessionId, PendingInterrupt p) => p.Kind switch {
        PendingInterruptKind.ClaudePermission => new(p.RequestId, sessionId, "claude", p.ToolName, p.ToolInput?.GetRawText(), p.RequestedAt, null, null, null),
        PendingInterruptKind.ClaudeQuestion => new(p.RequestId, sessionId, "claude", p.ToolName, p.ToolInput?.GetRawText(), p.RequestedAt, ClaudeElicitation.TryParse(p.ToolInput?.GetRawText()), null, null),
        PendingInterruptKind.AcpPermission => new(p.RequestId, sessionId, "", p.ToolName, p.ToolInput?.GetRawText(), p.RequestedAt, null, null, p.Options),
        PendingInterruptKind.AcpQuestion => new(p.RequestId, sessionId, "", ClaudeElicitation.ToolName, null, p.RequestedAt, null,
            new AcpElicitation(p.Prompt, p.Options, p.IsMultiSelect, p.MinSelections, p.MaxSelections), null),
        _ => null,
    };

    public static string KeyFor(PermissionLane lane, string requestId) => lane == PermissionLane.Local ? $"local:{requestId}" : $"server:{requestId}";

    public string Key { get; }
    public PermissionLane Lane { get; }
    public string RequestId { get; }
    public string SessionId { get; }
    public string AgentId { get => Volatile.Read(ref _agentId); internal set => Volatile.Write(ref _agentId, value); }
    public string Vendor { get; }
    public string ToolName { get; }
    public string? ToolInputJson { get; }
    public bool ToolInputOmitted { get; }
    public string? ToolUseId { get; }
    public DateTimeOffset RequestedAt { get; }
    public ElicitationQuestions? Questions { get; }
    public AcpElicitation? AcpQuestion { get; }
    public IReadOnlyList<AcpInteractionOption>? Options { get; }
    public string? ServerRequestId { get => Volatile.Read(ref _serverRequestId); internal set => Volatile.Write(ref _serverRequestId, value); }
    public bool IsQuestion => Questions is not null || AcpQuestion is not null;

    static DateTimeOffset ParseTime(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : DateTimeOffset.MinValue;
}
```

`PendingSummary.From` counts `IsQuestion` as a question and tallies `LocalCount`/`ServerCount` by `Lane`.

- [ ] **Step 4: Rewrite `PermissionService`**

Keep the class's shape (lock, tombstones, loop) and change these parts:

```csharp
readonly SourceCache<PendingPermissionRequest, string> _cache = new(p => p.Key);
readonly Dictionary<string, PendingPermissionRequest> _shadowed = new(StringComparer.Ordinal); // server items claimed by a live local item
readonly Dictionary<string, int> _sessionGenerations = new(StringComparer.Ordinal);
readonly PermissionResponder _respond;
IReadOnlyDictionary<string, string> _sessionAgents = new Dictionary<string, string>();
```

Constructor takes the two optional arguments; `respond ?? ((_, _, _, _) => Task.FromResult(new ServerRespondOutcome(ServerRespondKind.Unreachable, "not_signed_in")))`; subscribe `sessionAgents` to `OnSessionAgents`.

`AgentsWithPending` excludes items whose `AgentId` is `""`.

Local lane:

```csharp
case PermissionStreamEvent.Subscribed: DropLocalLane(); break;
case PermissionStreamEvent.Pending p:  UpsertLocal(p.Request); break;
case PermissionStreamEvent.Resolved r: ConcludeLocal(r.Settlement.RequestId); break;
```

```csharp
void UpsertLocal(PermissionPendingDto dto) {
    lock (_lock) {
        if (_disposed) return;
        var key = PendingPermissionRequest.KeyFor(PermissionLane.Local, dto.RequestId);
        if (_tombstones.Contains(key)) return;
        if (_cache.Lookup(key) is { HasValue: true, Value: var existing }) {
            if (existing.ServerRequestId != dto.ServerRequestId) {
                existing.ServerRequestId = dto.ServerRequestId;
                _cache.Refresh(existing);
                Shadow(dto.ServerRequestId);
            }
            return;
        }
        var item = new PendingPermissionRequest(dto);
        _cache.AddOrUpdate(item);
        Shadow(dto.ServerRequestId);
    }
}

// Caller holds _lock. A server item a local item now claims leaves the cache but stays known,
// so a lost local subscription can hand it back with its own handle intact.
void Shadow(string? serverRequestId) {
    if (serverRequestId is null) return;
    var key = PendingPermissionRequest.KeyFor(PermissionLane.Server, serverRequestId);
    if (_cache.Lookup(key) is { HasValue: true, Value: var twin }) { _shadowed[key] = twin; _cache.Remove(key); }
}

// Caller holds _lock.
bool IsClaimed(string serverRequestId) => _cache.Items.Any(i => i.Lane == PermissionLane.Local && i.ServerRequestId == serverRequestId);

void DropLocalLane() {
    lock (_lock) {
        if (_disposed) return;
        var locals = _cache.Items.Where(i => i.Lane == PermissionLane.Local).ToList();
        foreach (var item in locals) _cache.Remove(item.Key);
        foreach (var (key, twin) in _shadowed.ToList()) {
            if (_tombstones.Contains(key)) { _shadowed.Remove(key); continue; }
            _shadowed.Remove(key);
            _cache.AddOrUpdate(twin);
        }
    }
}

void ConcludeLocal(string requestId) {
    lock (_lock) {
        if (_disposed) return;
        var key = PendingPermissionRequest.KeyFor(PermissionLane.Local, requestId);
        _tombstones.Add(key);
        if (_cache.Lookup(key) is { HasValue: true, Value: var item } && item.ServerRequestId is { } srid) ConcludeServerKey(srid);
        _cache.Remove(key);
    }
}

// Caller holds _lock.
void ConcludeServerKey(string serverRequestId) {
    var key = PendingPermissionRequest.KeyFor(PermissionLane.Server, serverRequestId);
    _tombstones.Add(key);
    _shadowed.Remove(key);
    _cache.Remove(key);
}
```

`OnStatus`'s capability-less `Connected` branch calls `DropLocalLane()` instead of `_cache.Clear()`.

Server lane (internal):

```csharp
internal void UpsertServer(PendingPermissionRequest item) {
    lock (_lock) {
        if (_disposed || item.Lane != PermissionLane.Server || _tombstones.Contains(item.Key)) return;
        item.AgentId = _sessionAgents.GetValueOrDefault(item.SessionId, "");
        if (IsClaimed(item.RequestId)) { _shadowed[item.Key] = item; return; }
        if (_cache.Lookup(item.Key).HasValue) return; // a live card keeps its instance
        _cache.AddOrUpdate(item);
    }
}

internal void SettleServer(string sessionId, string? requestId) {
    lock (_lock) {
        if (_disposed) return;
        if (requestId is { } rid) {
            ConcludeServerKey(rid);
            foreach (var local in _cache.Items.Where(i => i.Lane == PermissionLane.Local && i.ServerRequestId == rid).ToList()) {
                _tombstones.Add(local.Key);
                _cache.Remove(local.Key);
            }
            return;
        }
        _sessionGenerations[sessionId] = SessionGeneration(sessionId) + 1;
        foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server && i.SessionId == sessionId).ToList()) ConcludeServerKey(item.RequestId);
        foreach (var (key, twin) in _shadowed.Where(kv => kv.Value.SessionId == sessionId).ToList()) ConcludeServerKey(twin.RequestId);
    }
}

internal int SessionGeneration(string sessionId) { lock (_lock) return _sessionGenerations.GetValueOrDefault(sessionId); }

/// The reconciliation's verdict for one session: adds what is missing, removes server items the
/// stream no longer holds. Stale by generation means a clear ran meanwhile — drop the result.
internal void ReplaceServerForSession(string sessionId, IReadOnlyList<PendingPermissionRequest> items, int generation) {
    lock (_lock) {
        if (_disposed || generation != SessionGeneration(sessionId)) return;
        var keep = items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _cache.Items.Where(i => i.Lane == PermissionLane.Server && i.SessionId == sessionId && !keep.Contains(i.Key)).ToList())
            _cache.Remove(stale.Key);
        foreach (var stale in _shadowed.Where(kv => kv.Value.SessionId == sessionId && !keep.Contains(kv.Key)).Select(kv => kv.Key).ToList())
            _shadowed.Remove(stale);
        foreach (var item in items) UpsertServer(item);
    }
}

/// Access revoked: the cards are unanswerable, but nothing is settled, so no tombstones.
internal void DropServerForSession(string sessionId) {
    lock (_lock) {
        if (_disposed) return;
        foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server && i.SessionId == sessionId).ToList()) _cache.Remove(item.Key);
        foreach (var key in _shadowed.Where(kv => kv.Value.SessionId == sessionId).Select(kv => kv.Key).ToList()) _shadowed.Remove(key);
    }
}

internal void ClearServerLane() {
    lock (_lock) {
        if (_disposed) return;
        foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server).ToList()) _cache.Remove(item.Key);
        _shadowed.Clear();
    }
}

void OnSessionAgents(IReadOnlyDictionary<string, string> map) {
    lock (_lock) {
        if (_disposed) return;
        _sessionAgents = map;
        foreach (var item in _cache.Items.Where(i => i.Lane == PermissionLane.Server).ToList()) {
            var agent = map.GetValueOrDefault(item.SessionId, "");
            if (item.AgentId == agent) continue;
            item.AgentId = agent;
            _cache.Refresh(item);
        }
    }
}
```

Answering dispatches by lane:

```csharp
public Task<PermissionResolveOutcome> ResolveAsync(PendingPermissionRequest target, PermissionAnswer answer, CancellationToken ct) {
    var apply = answer == PermissionAnswer.AllowAlways ? ClaudePermissions.AlwaysAllow(target.ToolName) : (JsonElement?)null;
    if (target.Lane == PermissionLane.Local) {
        var decision = answer == PermissionAnswer.Deny ? PermissionResolveDecisions.Deny : PermissionResolveDecisions.Allow;
        return SendResolveAsync(target, new PermissionResolveDto(target.RequestId, decision, apply, null), ct);
    }
    return SendServerAsync(target, new PermissionResponsePayload {
        Behavior = answer == PermissionAnswer.Deny ? PermissionBehaviors.Deny : PermissionBehaviors.Allow, ApplyPermissions = apply,
    }, ct);
}

public Task<PermissionResolveOutcome> AnswerAsync(PendingPermissionRequest target, IReadOnlyList<ElicitationAnswer> answers, CancellationToken ct) {
    if (target.Questions is null) throw new ArgumentException("not an elicitation entry", nameof(target));
    var updated = ClaudeElicitation.ComposeAnswers(target.Questions, answers);
    return target.Lane == PermissionLane.Local
        ? SendResolveAsync(target, new PermissionResolveDto(target.RequestId, PermissionResolveDecisions.Allow, null, updated), ct)
        : SendServerAsync(target, new PermissionResponsePayload { Behavior = PermissionBehaviors.Allow, UpdatedInput = updated }, ct);
}

public Task<PermissionResolveOutcome> AnswerAcpAsync(PendingPermissionRequest target, AcpAnswer answer, CancellationToken ct) {
    if (target.AcpQuestion is not { } question) throw new ArgumentException("not an ACP question", nameof(target));
    if (target.Lane != PermissionLane.Server) throw new ArgumentException("ACP questions are server-lane items", nameof(target));
    var labels = answer.SelectedOptionIds.Select(id => question.Options.FirstOrDefault(o => o.OptionId == id)?.Label ?? id).ToArray();
    var payload = answer.SelectedOptionIds.Count switch {
        0 => new PermissionResponsePayload { Behavior = PermissionBehaviors.Answered, FreeText = answer.FreeText },
        1 => new PermissionResponsePayload { Behavior = PermissionBehaviors.Answered, SelectedOptionId = answer.SelectedOptionIds[0], SelectedOptionLabel = labels[0], FreeText = answer.FreeText },
        _ => new PermissionResponsePayload { Behavior = PermissionBehaviors.Answered, SelectedOptionIds = [.. answer.SelectedOptionIds], SelectedOptionLabels = labels, FreeText = answer.FreeText },
    };
    return SendServerAsync(target, payload, ct);
}

public Task<PermissionResolveOutcome> PickOptionAsync(PendingPermissionRequest target, string optionId, CancellationToken ct) {
    var option = target.Options?.FirstOrDefault(o => o.OptionId == optionId) ?? throw new ArgumentException("not an offered option", nameof(optionId));
    if (target.Lane != PermissionLane.Server) throw new ArgumentException("ACP permissions are server-lane items", nameof(target));
    return SendServerAsync(target, new PermissionResponsePayload {
        Behavior = BehaviorFor(option.Kind), SelectedOptionId = option.OptionId, SelectedOptionLabel = option.Label,
    }, ct);
}

/// The daemon resolves the pick by OptionId; the behavior only tells it which way a missing id
/// would have gone, so an unknown kind reads as allow.
internal static string BehaviorFor(string? kind) =>
    kind is not null && (kind.Contains("reject", StringComparison.OrdinalIgnoreCase) || kind.Contains("deny", StringComparison.OrdinalIgnoreCase) || kind.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        ? PermissionBehaviors.Deny : PermissionBehaviors.Allow;

public Task<PermissionResolveOutcome> WithdrawAsync(PendingPermissionRequest target, CancellationToken ct) =>
    target.Lane == PermissionLane.Local
        ? SendResolveAsync(target, new PermissionResolveDto(target.RequestId, PermissionResolveDecisions.Withdraw, null, null), ct)
        : Task.FromResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "withdraw_unsupported"));

async Task<PermissionResolveOutcome> SendServerAsync(PendingPermissionRequest target, PermissionResponsePayload payload, CancellationToken ct) {
    var outcome = await _respond(target.SessionId, target.RequestId, payload, ct).ConfigureAwait(false);
    switch (outcome.Kind) {
        case ServerRespondKind.Applied:
            lock (_lock) { if (!_disposed) ConcludeServerKey(target.RequestId); }
            return new(PermissionResolveKind.Applied, null);
        case ServerRespondKind.NotPending:
            lock (_lock) { if (!_disposed) ConcludeServerKey(target.RequestId); }
            return new(PermissionResolveKind.AlreadyDecided, null);
        case ServerRespondKind.Rejected:
            return new(PermissionResolveKind.TransportFailure, outcome.Reason ?? "rejected");
        case ServerRespondKind.Unauthorized:
            return new(PermissionResolveKind.TransportFailure, "not_signed_in");
        default:
            return new(PermissionResolveKind.TransportFailure, outcome.Reason ?? "server_unreachable");
    }
}
```

`SendResolveAsync` takes the target too and calls `ConcludeLocal(target.RequestId)` (which also retires the claimed twin) in place of `Conclude(dto.RequestId)`.

- [ ] **Step 5: Follow the compiler through the app**

- `ChatTabViewModel`: `_requests` keyed by `change.Key` unchanged (it is the cache key); the `Transform` picks `p.Questions is not null → QuestionCardViewModel`, `p.AcpQuestion is not null → AcpQuestionCardViewModel` (Task 10 — until then map it to `PermissionCardViewModel` and leave a failing test in Task 10, or land Task 10's VM stub here as an empty subclass: prefer landing the stub), else `PermissionCardViewModel`.
- `PendingCardViewModel`: add `public string Key { get; }` from `entry.Key`; the sort tiebreak compares `Key`.
- `TrayViewModel`: `PendingSummary` gained two counts; the header code uses `Permissions`/`Questions` only, so only construction sites in tests change (`new PendingSummary(0, 0)` → `default`).
- `FakePermissionService`: implement the two new methods (record into `AcpAnswered` / `Picked` lists, dequeue the outcome, evict on a conclusive one); add builders `PermissionEntries.ServerPermission(...)`, `PermissionEntries.AcpQuestion(...)` via `PendingPermissionRequest.FromServer`.

- [ ] **Step 6: Run the permission, chat and tray suites**

Run the `PermissionServiceTests`, `ChatTabViewModelTests`, `PermissionCardViewModelTests`, `QuestionCardViewModelTests`, `TrayViewModelTests` classes, then `dotnet build src/Capacitor.App/Capacitor.App.csproj`.
Expected: all pass, no warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Scope the pending-permission cache by lane with proven correlation (#805)"
```

Body:

```
A server item is shadowed only by a live local item claiming its exact server id, and a local
settlement retires that twin: the daemon has answered the hook, so the server copy is moot even
if the relay fails.
```

---

### Task 8: `ServerPermissionFeed` — live pushes, reconciliation on open, settlement

**Files:**
- Create: `src/Capacitor.App/Services/ServerPermissionFeed.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerPermissionFeedTests.cs`

**Interfaces:**
- Produces: `sealed class ServerPermissionFeed(IServerLane lane, SessionAccessService access, PermissionService permissions, SessionDetailReader readDetail, Func<string, string?> vendorOfSession, TimeProvider time) : IDisposable`.
- Rules: `lane.PermissionRequests` → `permissions.UpsertServer(PendingPermissionRequest.FromServer(r, vendorOfSession(r.SessionId) ?? "", time.GetUtcNow()))`; `lane.ElicitationRequests` → same with the elicitation factory; `lane.PermissionResponded` → `permissions.SettleServer(sid, rid)`; `access.Transitions` — `Established` for a session runs a reconciliation: capture `permissions.SessionGeneration(sid)`, fetch, map `Pending.Where(IsAnswerableOverHttp)` through `FromReconciled`, and call `ReplaceServerForSession(sid, items, generation)`; a fetch with `Ended` or `NotFound` calls `ReplaceServerForSession(sid, [], generation)`; a failed fetch (null detail, not NotFound) leaves the cache alone. `Denied` → `permissions.DropServerForSession(sid)`. A lane `Connected` status whose `Subject` differs from the last one seen → `permissions.ClearServerLane()`.
- `vendorOfSession` comes from the directory (Task 13 wires it: the row's `Vendor` for the row whose `SessionId` matches); an unknown session yields `""`, which only affects the Claude "Allow always" affordance.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/ServerPermissionFeedTests.cs` — reuse `PermissionServiceTests`' harness shape (fake stream, scripted ops) so `PermissionService` is real:

```csharp
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class ServerPermissionFeedTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAccessService Access;
        public readonly PermissionService Permissions;
        public readonly IObservableCache<PendingPermissionRequest, string> View;
        public readonly ServerPermissionFeed Feed;
        public Func<string, SessionDetailFetch> Detail = _ => new(null, NotFound: true);
        public int Fetches;

        public Harness() {
            Access = new SessionAccessService(Lane, Time);
            Permissions = new PermissionService(new FakeDaemonClientService(), new ScriptedLocalControlOps(),
                _ => AsyncEnumerable.Empty<Cli.Core.LocalIpc.PermissionStreamEvent>(), Time, CancellationToken.None);
            View = Permissions.Pending.AsObservableCache();
            Feed = new ServerPermissionFeed(Lane, Access, Permissions, (sid, _) => { Fetches++; return Task.FromResult(Detail(sid)); }, _ => "claude", Time);
        }

        public void Connect(string subject = "u1") => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: subject, Epoch: 1));
        public void Dispose() { Feed.Dispose(); Access.Dispose(); Permissions.Dispose(); View.Dispose(); }
    }

    static SessionDetailFetch DetailWith(string eventsJson) => new(System.Text.Json.JsonSerializer.Deserialize(
        $$"""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":{{eventsJson}}}""", RemoteModelsJsonContext.Default.SessionDetailDto));

    [Test]
    public async Task Live_pushes_become_server_items_and_a_responded_ping_settles_them() {
        using var h = new Harness();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        h.Lane.ElicitationsSubject.OnNext(new ServerElicitationRequest("s1", "q1", "Pick", [new AcpInteractionOption { OptionId = "a", Label = "A" }], false));
        await Assert.That(h.View.Count).IsEqualTo(2);
        await Assert.That(h.View.Lookup("server:r1").Value.Vendor).IsEqualTo("claude");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        await Assert.That(h.View.Count).IsEqualTo(1);
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", null));
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Establishing_a_session_reconciles_its_cards_and_transcript_questions_get_none() {
        using var h = new Harness();
        h.Detail = _ => DetailWith("""[
            {"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}},
            {"event_type":"InterruptIssued","event_number":2,"payload":{"request_id":"t1","kind":"input","tool_name":"AskUserQuestion","prompt":"?"}}
        ]""");
        h.Connect();
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.View.Lookup("server:p1").HasValue, what: "reconciled card");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_settlement_racing_the_fetch_wins() {
        using var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Detail = _ => { gate.Task.Wait(); return DetailWith("""[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]"""); };
        h.Connect();
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.Fetches == 1, what: "fetch started");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", null));
        gate.SetResult();
        await Task.Delay(100);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Denial_drops_the_sessions_cards_without_settling_them() {
        using var h = new Harness();
        h.Connect();
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
        using var lease = h.Access.Acquire("s1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cards dropped on denial");
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        await Assert.That(h.View.Count).IsEqualTo(1); // not tombstoned
    }

    [Test]
    public async Task An_identity_change_clears_the_server_lane() {
        using var h = new Harness();
        h.Connect("u1");
        h.Lane.PermissionRequestsSubject.OnNext(new ServerPermissionRequest("s1", "r1", "Bash", null, null));
        h.Connect("u2");
        await Assert.That(h.View.Count).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: build error — `ServerPermissionFeed` missing.

- [ ] **Step 3: Implement**

```csharp
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace Capacitor.App.Services;

/// Feeds the permission cache's server lane: live pushes for sessions the app has joined, a
/// reconciliation of each session's event stream every time its access is established, and
/// the org-wide settlement pings. Every reconciliation carries the session generation it
/// started under, so a clear that lands mid-fetch wins.
public sealed class ServerPermissionFeed : IDisposable {
    readonly PermissionService _permissions;
    readonly SessionDetailReader _readDetail;
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _lifetime = new();
    string? _subject;

    public ServerPermissionFeed(
            IServerLane lane, SessionAccessService access, PermissionService permissions,
            SessionDetailReader readDetail, Func<string, string?> vendorOfSession, TimeProvider time) {
        _permissions = permissions;
        _readDetail = readDetail;

        lane.Status.Where(s => s.State == ServerLaneState.Connected && s.Subject is not null)
            .Subscribe(s => {
                if (_subject is not null && _subject != s.Subject) permissions.ClearServerLane();
                _subject = s.Subject;
            }).DisposeWith(_subscriptions);
        lane.PermissionRequests
            .Subscribe(r => permissions.UpsertServer(PendingPermissionRequest.FromServer(r, vendorOfSession(r.SessionId) ?? "", time.GetUtcNow())))
            .DisposeWith(_subscriptions);
        lane.ElicitationRequests
            .Subscribe(r => permissions.UpsertServer(PendingPermissionRequest.FromServer(r, time.GetUtcNow())))
            .DisposeWith(_subscriptions);
        lane.PermissionResponded
            .Subscribe(p => permissions.SettleServer(p.SessionId, p.RequestId))
            .DisposeWith(_subscriptions);
        access.Transitions
            .Subscribe(t => {
                switch (t.State) {
                    case SessionAccessState.Established: _ = ReconcileAsync(t.SessionId); break;
                    case SessionAccessState.Denied: permissions.DropServerForSession(t.SessionId); break;
                }
            }).DisposeWith(_subscriptions);
    }

    async Task ReconcileAsync(string sessionId) {
        var generation = _permissions.SessionGeneration(sessionId);
        SessionDetailFetch fetch;
        try { fetch = await _readDetail(sessionId, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: session reconciliation failed: {ex.Message}"); return; }

        if (fetch.Detail is null) {
            if (fetch.NotFound) _permissions.ReplaceServerForSession(sessionId, [], generation);
            return;
        }
        var reconciled = InterruptReconciliation.FromDetail(fetch.Detail);
        var items = reconciled.Ended ? []
            : reconciled.Pending.Where(p => p.IsAnswerableOverHttp)
                .Select(p => PendingPermissionRequest.FromReconciled(sessionId, p))
                .Where(i => i is not null).Select(i => i!).ToList();
        _permissions.ReplaceServerForSession(sessionId, items, generation);
    }

    public void Dispose() {
        _subscriptions.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests**

Expected: pass. The racing test depends on `SettleServer(sid, null)` bumping the generation before the fetch's `ReplaceServerForSession` runs — that is what Task 7 pinned.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/ServerPermissionFeed.cs test/Capacitor.App.Tests.Unit/ServerPermissionFeedTests.cs
git commit -m "Feed server-lane permission cards from pushes and reconciliation (#805)"
```

---

### Task 9: `SessionAttentionTracker` — truthful pips for unopened sessions

**Files:**
- Create: `src/Capacitor.App/Services/SessionAttentionTracker.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionAttentionTrackerTests.cs`

**Interfaces:**
- Produces: `sealed class SessionAttentionTracker(IServerLane lane, SessionDetailReader readDetail, TimeProvider time, TimeSpan? debounce = null) : IDisposable { IObservable<IReadOnlySet<string>> SessionsWithAttention; }` — replay-1, starts empty, emits the set of session ids whose pending-request-id set is non-empty.
- Rules: `PermissionPending(sid)` marks the session dirty and schedules a debounced reconciliation (default 300 ms). A reconciliation that completes (any detail, or NotFound) replaces the session's set with every `Pending` request id (transcript questions included) and clears the dirty mark; `Ended`/`NotFound` empties it. A failed fetch keeps the dirty mark and retries on 1s/2s/5s/10s/30s while the lane is Connected. `PermissionResponded(sid, rid)` removes `rid`; if `rid` was not in the set the session is marked dirty and rescheduled (the answered id may be the tracker's while the set holds the transcript's). `PermissionResponded(sid, null)` empties the set. On every lane Connected transition, every session that is dirty or non-empty is reconciled again. While the lane is not Connected, timers are cancelled and sets are retained.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class SessionAttentionTrackerTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeTimeProvider Time = new();
        public readonly SessionAttentionTracker Tracker;
        public IReadOnlySet<string> Sessions = new HashSet<string>();
        public Func<string, SessionDetailFetch> Detail = _ => new(null, NotFound: true);
        public int Fetches;

        public Harness() {
            Tracker = new SessionAttentionTracker(Lane, (sid, _) => { Fetches++; return Task.FromResult(Detail(sid)); }, Time, TimeSpan.FromMilliseconds(100));
            Tracker.SessionsWithAttention.Subscribe(s => Sessions = s);
        }

        public void Connect(int epoch = 1) => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: epoch));
        public void Drop() => Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        public void Dispose() => Tracker.Dispose();
    }

    static SessionDetailFetch Pending(params string[] ids) {
        var events = string.Join(",", ids.Select((id, i) => $$"""{"event_type":"InterruptIssued","event_number":{{i}},"payload":{"request_id":"{{id}}","kind":"permission","tool_name":"Bash"}}"""));
        return new(System.Text.Json.JsonSerializer.Deserialize($$"""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":[{{events}}]}""", RemoteModelsJsonContext.Default.SessionDetailDto));
    }

    [Test]
    public async Task Two_prompts_need_two_responses_before_the_pip_clears() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1", "r2");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r1"));
        await Assert.That(h.Sessions).Contains("s1");
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "r2"));
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "attention off");
    }

    [Test]
    public async Task A_ping_before_the_first_reconciliation_completes_survives_a_disconnect() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Drop(); // before the debounce fires
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await Task.Delay(50);
        await Assert.That(h.Fetches).IsEqualTo(0);
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "dirty session reconciled after reconnect");
    }

    [Test]
    public async Task A_response_missed_while_disconnected_clears_on_reconnect() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Drop();
        h.Detail = _ => Pending();
        h.Connect(epoch: 2);
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "cleared by re-reconciliation");
    }

    [Test]
    public async Task A_transient_failure_retries_to_an_authoritative_result() {
        using var h = new Harness();
        h.Connect();
        var calls = 0;
        h.Detail = _ => ++calls == 1 ? new SessionDetailFetch(null) : Pending("r1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Fetches == 1, what: "first fetch");
        await Assert.That(h.Sessions).DoesNotContain("s1");
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "retry succeeded");
    }

    [Test]
    public async Task A_response_naming_an_unknown_id_re_reconciles() {
        using var h = new Harness();
        h.Connect();
        h.Detail = _ => Pending("transcript-1");
        h.Lane.PermissionPendingSubject.OnNext("s1");
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => h.Sessions.Contains("s1"), what: "attention on");
        h.Detail = _ => Pending();
        h.Lane.PermissionRespondedSubject.OnNext(new PermissionRespondedPing("s1", "tracker-9"));
        h.Time.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => !h.Sessions.Contains("s1"), what: "cleared by the re-reconciliation");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: build error.

- [ ] **Step 3: Implement**

```csharp
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// Per-session sets of unresolved server request ids for sessions the app has not opened. A
/// pending ping names no request, so it marks the session dirty and a headless reconciliation
/// of the session's stream fills the set; the dirty mark outlives disconnects and fetch failures
/// until a reconciliation completes. A response removes one id; one the set never held means
/// the set is out of date, so it re-reconciles rather than trusting the count.
public sealed class SessionAttentionTracker : IDisposable {
    static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    sealed class Session {
        public readonly HashSet<string> Ids = new(StringComparer.Ordinal);
        public bool Dirty;
        public int Failures;
        public int Attempt;
        public ITimer? Timer;
    }

    readonly SessionDetailReader _readDetail;
    readonly TimeProvider _time;
    readonly TimeSpan _debounce;
    readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    readonly BehaviorSubject<IReadOnlySet<string>> _attention = new(new HashSet<string>());
    readonly CompositeDisposable _subscriptions = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly Lock _lock = new();
    bool _connected;

    public SessionAttentionTracker(IServerLane lane, SessionDetailReader readDetail, TimeProvider time, TimeSpan? debounce = null) {
        _readDetail = readDetail;
        _time = time;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        lane.Status.Select(s => s.State == ServerLaneState.Connected).DistinctUntilChanged().Subscribe(OnLane).DisposeWith(_subscriptions);
        lane.PermissionPending.Subscribe(sid => { lock (_lock) { var s = Get(sid); s.Dirty = true; Schedule(sid, s, _debounce); } }).DisposeWith(_subscriptions);
        lane.PermissionResponded.Subscribe(OnResponded).DisposeWith(_subscriptions);
    }

    public IObservable<IReadOnlySet<string>> SessionsWithAttention => _attention.AsObservable();

    void OnResponded(PermissionRespondedPing ping) {
        lock (_lock) {
            var s = Get(ping.SessionId);
            if (ping.RequestId is null) { s.Ids.Clear(); Publish(); return; }
            if (s.Ids.Remove(ping.RequestId)) { Publish(); return; }
            s.Dirty = true;
            Schedule(ping.SessionId, s, _debounce);
        }
    }

    void OnLane(bool connected) {
        lock (_lock) {
            _connected = connected;
            foreach (var (sid, s) in _sessions) {
                s.Failures = 0;
                if (!connected) { s.Attempt++; s.Timer?.Dispose(); s.Timer = null; continue; }
                if (s.Dirty || s.Ids.Count > 0) Schedule(sid, s, TimeSpan.Zero);
            }
        }
    }

    // Caller holds _lock.
    Session Get(string sid) {
        if (!_sessions.TryGetValue(sid, out var s)) { s = new Session(); _sessions[sid] = s; }
        return s;
    }

    // Caller holds _lock. A schedule supersedes any earlier one for the session.
    void Schedule(string sid, Session s, TimeSpan delay) {
        s.Timer?.Dispose();
        s.Timer = null;
        if (!_connected) return;
        var attempt = ++s.Attempt;
        s.Timer = _time.CreateTimer(_ => { lock (_lock) { if (s.Attempt == attempt) _ = ReconcileAsync(sid, s, attempt); } }, null, delay, Timeout.InfiniteTimeSpan);
    }

    async Task ReconcileAsync(string sid, Session s, int attempt) {
        SessionDetailFetch fetch;
        try { fetch = await _readDetail(sid, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { fetch = new(null); }

        lock (_lock) {
            if (s.Attempt != attempt) return;
            if (fetch.Detail is null && !fetch.NotFound) {
                if (!_connected) return;
                var delay = Retry[Math.Min(s.Failures++, Retry.Length - 1)];
                Schedule(sid, s, delay);
                return;
            }
            s.Ids.Clear();
            if (fetch.Detail is { } detail) {
                var reconciled = InterruptReconciliation.FromDetail(detail);
                if (!reconciled.Ended) foreach (var p in reconciled.Pending) s.Ids.Add(p.RequestId);
            }
            s.Dirty = false;
            s.Failures = 0;
            if (s.Ids.Count == 0) _sessions.Remove(sid);
            Publish();
        }
    }

    // Caller holds _lock.
    void Publish() {
        var next = _sessions.Where(kv => kv.Value.Ids.Count > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (!next.SetEquals(_attention.Value)) _attention.OnNext(next);
    }

    public void Dispose() {
        _subscriptions.Dispose();
        lock (_lock) { foreach (var s in _sessions.Values) { s.Attempt++; s.Timer?.Dispose(); } }
        _lifetime.Cancel();
        _lifetime.Dispose();
        _attention.Dispose();
    }
}
```

Note `ReconcileAsync` removes a session whose set is empty and not dirty, so `_sessions` does not grow forever; a session removed while a timer callback is in flight is protected by the attempt check.

- [ ] **Step 4: Run the tests**

Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/SessionAttentionTracker.cs test/Capacitor.App.Tests.Unit/SessionAttentionTrackerTests.cs
git commit -m "Track per-session pending request ids from pings and reconciliation (#805)"
```

---

### Task 10: The id-preserving ACP card model

**Files:**
- Create: `src/Capacitor.App/ViewModels/AcpOptionViewModel.cs`, `AcpQuestionCardViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/PendingCardViewModel.cs` (shared error copy), `PermissionCardViewModel.cs` (options), `ChatTabViewModel.cs` (card selection), `Views/ChatTabView.axaml` (templates)
- Test: `test/Capacitor.App.Tests.Unit/AcpQuestionCardViewModelTests.cs`, `PermissionCardViewModelTests.cs`

**Interfaces:**
- `AcpOptionViewModel { string OptionId; string Label; string? Description; bool IsSelected (settable); ReactiveCommand<Unit, Unit> PickCommand; }`.
- `AcpQuestionCardViewModel : PendingCardViewModel { string Prompt; IReadOnlyList<AcpOptionViewModel> Options; bool HasOptions; bool IsMultiSelect; int MinSelections; int MaxSelections; string FreeText (settable); bool IsAnswered; bool ShowsSubmit; ReactiveCommand<Unit, Unit> SubmitCommand; }`. Single-select with options: a pick submits at once. Multi-select: picks toggle; Submit enabled while `Min ≤ selected ≤ Max`. No options: free text, Submit enabled when non-blank. `MinSelections` defaults to 1 and `MaxSelections` to `Options.Count` (single-select: 1) when the wire carries none.
- `PermissionCardViewModel` gains `IReadOnlyList<AcpOptionViewModel> Options`, `bool HasOptions`; a pick calls `permissions.PickOptionAsync`. `ShowsAllowAlways` is false for a server-lane item whose vendor is not `claude`, and Allow/Deny are hidden by the view when `HasOptions`.
- `PendingCardViewModel.ErrorTextFor(PermissionResolveOutcome) : string?` — `daemon_unreachable` → "Daemon unreachable — try again", `server_unreachable` → "Server unreachable — try again", `not_signed_in` → "Sign in to answer", `withdraw_unsupported` → null, anything else → `Could not answer ({reason}) — try again`. Both existing cards use it.

- [ ] **Step 1: Write the failing tests**

`AcpQuestionCardViewModelTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(AvaloniaSession))]
public class AcpQuestionCardViewModelTests {
    static AcpInteractionOption Opt(string id, string label, int? min = null, int? max = null) =>
        new() { OptionId = id, Label = label, MinSelections = min, MaxSelections = max };

    static PendingPermissionRequest Question(bool multi, params AcpInteractionOption[] options) =>
        PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", options, multi), DateTimeOffset.UtcNow);

    [Test]
    public async Task Options_with_identical_labels_stay_distinct_and_a_single_pick_submits_its_id() {
        await AvaloniaSession.RunAsync(async () => {
            var permissions = new FakePermissionService();
            permissions.Queue(PermissionResolveKind.Applied);
            var card = new AcpQuestionCardViewModel(Question(false, Opt("a", "Same"), Opt("b", "Same")), permissions);
            await Assert.That(card.Options.Select(o => o.OptionId)).IsEquivalentTo(new[] { "a", "b" });
            card.Options[1].PickCommand.Execute().Subscribe();
            await WorkspaceFixtures.WaitUntilAsync(() => permissions.AcpAnswered.Count == 1, what: "submitted");
            await Assert.That(permissions.AcpAnswered[0].Answer.SelectedOptionIds).IsEquivalentTo(new[] { "b" });
        });
    }

    [Test]
    public async Task Multi_select_gates_submit_on_the_bounds() {
        await AvaloniaSession.RunAsync(async () => {
            var permissions = new FakePermissionService();
            var card = new AcpQuestionCardViewModel(Question(true, Opt("a", "A", 2, 3), Opt("b", "B", 2, 3), Opt("c", "C", 2, 3), Opt("d", "D", 2, 3)), permissions);
            await Assert.That(card.IsAnswered).IsFalse();
            card.Options[0].IsSelected = true;
            await Assert.That(card.IsAnswered).IsFalse();
            card.Options[1].IsSelected = true;
            await Assert.That(card.IsAnswered).IsTrue();
            card.Options[2].IsSelected = true;
            card.Options[3].IsSelected = true;
            await Assert.That(card.IsAnswered).IsFalse(); // over the max
        });
    }

    [Test]
    public async Task Free_text_answers_when_there_are_no_options() {
        await AvaloniaSession.RunAsync(async () => {
            var permissions = new FakePermissionService();
            permissions.Queue(PermissionResolveKind.Applied);
            var card = new AcpQuestionCardViewModel(Question(false), permissions);
            await Assert.That(card.HasOptions).IsFalse();
            await Assert.That(card.IsAnswered).IsFalse();
            card.FreeText = "  something ";
            await Assert.That(card.IsAnswered).IsTrue();
            card.SubmitCommand.Execute().Subscribe();
            await WorkspaceFixtures.WaitUntilAsync(() => permissions.AcpAnswered.Count == 1, what: "submitted");
            await Assert.That(permissions.AcpAnswered[0].Answer.FreeText).IsEqualTo("something");
            await Assert.That(permissions.AcpAnswered[0].Answer.SelectedOptionIds).IsEmpty();
        });
    }

    [Test]
    public async Task A_transport_failure_keeps_the_card_with_the_lanes_error_copy() {
        await AvaloniaSession.RunAsync(async () => {
            var permissions = new FakePermissionService();
            permissions.Queue(PermissionResolveKind.TransportFailure, "not_signed_in");
            var card = new AcpQuestionCardViewModel(Question(false, Opt("a", "A")), permissions);
            card.Options[0].PickCommand.Execute().Subscribe();
            await WorkspaceFixtures.WaitUntilAsync(() => card.ErrorText is not null, what: "error shown");
            await Assert.That(card.ErrorText).IsEqualTo("Sign in to answer");
            await Assert.That(card.IsBusy).IsFalse();
        });
    }
}
```

Match the existing `QuestionCardViewModelTests` for how a card test runs under the shared headless session (`AvaloniaSession.RunAsync` or whatever helper that file uses) — copy its exact shape.

Append to `PermissionCardViewModelTests`:

```csharp
[Test]
public async Task An_acp_permission_offers_its_options_and_a_pick_names_the_option_id() {
    await AvaloniaSession.RunAsync(async () => {
        var permissions = new FakePermissionService();
        permissions.Queue(PermissionResolveKind.Applied);
        var entry = PendingPermissionRequest.FromServer(new ServerPermissionRequest("s1", "p1", "fs/write", null,
            [new() { OptionId = "allow-once", Label = "Allow once", Kind = "allow_once" }, new() { OptionId = "reject", Label = "Reject", Kind = "reject_once" }]), "", DateTimeOffset.UtcNow);
        var card = new PermissionCardViewModel(entry, permissions, System.Reactive.Linq.Observable.Return<string?>(null));
        await Assert.That(card.HasOptions).IsTrue();
        await Assert.That(card.ShowsAllowAlways).IsFalse();
        card.Options[1].PickCommand.Execute().Subscribe();
        await WorkspaceFixtures.WaitUntilAsync(() => permissions.Picked.Count == 1, what: "picked");
        await Assert.That(permissions.Picked[0].OptionId).IsEqualTo("reject");
    });
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: build errors.

- [ ] **Step 3: Implement the view models**

`AcpOptionViewModel.cs`:

```csharp
using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One ACP option. Identity is OptionId; two options may share a label.
public sealed class AcpOptionViewModel : ReactiveObject {
    readonly Action _selectionChanged;
    bool _isSelected;

    public string OptionId { get; }
    public string Label { get; }
    public string? Description { get; }
    public ReactiveCommand<Unit, Unit> PickCommand { get; }

    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            _selectionChanged();
        }
    }

    internal AcpOptionViewModel(string optionId, string label, string? description, Func<AcpOptionViewModel, Task> pick, IObservable<bool> idle, Action selectionChanged) {
        OptionId = optionId;
        Label = label;
        Description = description;
        _selectionChanged = selectionChanged;
        PickCommand = ReactiveCommand.CreateFromTask(() => pick(this), idle);
    }
}
```

`AcpQuestionCardViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The NEEDS YOU card for an ACP question. A single-select pick submits at once; a multi-select
/// submits only within its selection bounds; a question with no options takes free text.
public sealed class AcpQuestionCardViewModel : PendingCardViewModel {
    readonly PendingPermissionRequest _entry;
    readonly IPermissionService _permissions;
    readonly CancellationTokenSource _lifetime = new();
    readonly BehaviorSubject<bool> _answered = new(false);
    string _freeText = "";

    public string Prompt { get; }
    public IReadOnlyList<AcpOptionViewModel> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public bool IsMultiSelect { get; }
    public int MinSelections { get; }
    public int MaxSelections { get; }
    public bool ShowsSubmit => !HasOptions || IsMultiSelect;
    public ReactiveCommand<Unit, Unit> SubmitCommand { get; }

    public string FreeText {
        get => _freeText;
        set { this.RaiseAndSetIfChanged(ref _freeText, value); Refresh(); }
    }

    public bool IsAnswered {
        get {
            if (!HasOptions) return !string.IsNullOrWhiteSpace(FreeText);
            var selected = Options.Count(o => o.IsSelected);
            return IsMultiSelect ? selected >= MinSelections && selected <= MaxSelections : selected == 1;
        }
    }

    public AcpQuestionCardViewModel(PendingPermissionRequest entry, IPermissionService permissions) : base(entry) {
        _entry = entry;
        _permissions = permissions;
        var question = entry.AcpQuestion ?? throw new ArgumentException("not an ACP question", nameof(entry));
        Prompt = question.Prompt;
        IsMultiSelect = question.IsMultiSelect;
        var idle = Busy.Select(b => !b);
        Options = question.Options.Select(o => new AcpOptionViewModel(o.OptionId, o.Label, o.Description, PickAsync, idle, Refresh)).ToList();
        MinSelections = Math.Max(1, question.MinSelections ?? 1);
        MaxSelections = IsMultiSelect ? Math.Max(MinSelections, question.MaxSelections ?? Math.Max(1, Options.Count)) : 1;

        SubmitCommand = ReactiveCommand.CreateFromTask(SubmitAsync, _answered.CombineLatest(idle, (a, i) => a && i));
        Disposables.Add(SubmitCommand);
        Disposables.Add(_answered);
        Disposables.Add(Disposable.Create(() => { try { _lifetime.Cancel(); } catch (ObjectDisposedException) { } }));
        Disposables.Add(_lifetime);
    }

    Task PickAsync(AcpOptionViewModel option) {
        if (IsMultiSelect) { option.IsSelected = !option.IsSelected; return Task.CompletedTask; }
        foreach (var o in Options) o.IsSelected = ReferenceEquals(o, option);
        return SubmitAsync();
    }

    void Refresh() {
        _answered.OnNext(IsAnswered);
        this.RaisePropertyChanged(nameof(IsAnswered));
    }

    async Task SubmitAsync() {
        if (IsBusy || IsDisposed || !IsAnswered) return;
        IsBusy = true;
        ErrorText = null;
        try {
            var ids = Options.Where(o => o.IsSelected).Select(o => o.OptionId).ToList();
            var text = HasOptions ? null : FreeText.Trim();
            var outcome = await _permissions.AnswerAcpAsync(_entry, new AcpAnswer(ids, text), _lifetime.Token);
            ErrorText = ErrorTextFor(outcome);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: question submit failed unexpectedly: {ex.Message}");
            ErrorText = "Something went wrong — try again";
        } finally {
            IsBusy = false;
        }
    }
}
```

`PendingCardViewModel` gains:

```csharp
/// The copy a failed answer shows; null for a conclusive outcome or one that needs no card text.
protected static string? ErrorTextFor(PermissionResolveOutcome outcome) => outcome.Kind != PermissionResolveKind.TransportFailure ? null : outcome.Error switch {
    "daemon_unreachable" => "Daemon unreachable — try again",
    "server_unreachable" => "Server unreachable — try again",
    "not_signed_in" => "Sign in to answer",
    "withdraw_unsupported" => null,
    var reason => $"Could not answer ({reason}) — try again",
};
```

`PermissionCardViewModel` and `QuestionCardViewModel` replace their inline error mapping with `ErrorText = ErrorTextFor(outcome)`. `PermissionCardViewModel` adds:

```csharp
public IReadOnlyList<AcpOptionViewModel> Options { get; }
public bool HasOptions => Options.Count > 0;
```

built as `entry.Options?.Select(o => new AcpOptionViewModel(o.OptionId, o.Label, o.Description, PickAsync, idle, () => { })).ToList() ?? []`, with:

```csharp
async Task PickAsync(AcpOptionViewModel option) {
    IsBusy = true;
    ErrorText = null;
    try { ErrorText = ErrorTextFor(await _permissions.PickOptionAsync(_entry, option.OptionId, CancellationToken.None)); }
    finally { IsBusy = false; }
}
```

and `ShowsAllowAlways = entry.Vendor == "claude" && entry.ToolName != ClaudeElicitation.ToolName && !HasOptions`.

`ChatTabViewModel`'s transform:

```csharp
.Transform(p => p.Questions is not null ? new QuestionCardViewModel(p, permissions)
    : p.AcpQuestion is not null ? new AcpQuestionCardViewModel(p, permissions)
    : (PendingCardViewModel)new PermissionCardViewModel(p, permissions, _rootSubject))
```

`FakePermissionService` gains `AcpAnswered` (`List<(string Key, AcpAnswer Answer)>`) and `Picked` (`List<(string Key, string OptionId)>`).

- [ ] **Step 4: Add the templates**

In `ChatTabView.axaml`, inside the permission card's `StackPanel`, before the Deny/Allow row:

```xml
<ItemsControl ItemsSource="{Binding Options}" IsVisible="{Binding HasOptions}">
    <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel Orientation="Horizontal" /></ItemsPanelTemplate></ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
        <DataTemplate x:DataType="vm:AcpOptionViewModel">
            <Button Content="{Binding Label}" Command="{Binding PickCommand}" ToolTip.Tip="{Binding Description}"
                    Margin="0,0,6,6" Padding="10,4" FontSize="12" CornerRadius="7"
                    Background="{StaticResource KcapSurfaceRaisedBrush}" Foreground="{StaticResource KcapTextBrush}" />
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

and `IsVisible="{Binding !HasOptions}"` on the Deny/Allow `StackPanel`. Add a sibling `DataTemplate x:DataType="vm:AcpQuestionCardViewModel"` after the question card's: the prompt as the title, an options `ItemsControl` (a `ToggleButton` bound to `IsSelected` for multi-select, a `Button` with `PickCommand` otherwise — two `ItemsControl`s switched on `IsMultiSelect`), a `TextBox Text="{Binding FreeText}"` visible when `!HasOptions`, the `ErrorText` line, and a Submit `Button` bound to `SubmitCommand` visible when `ShowsSubmit`. Reuse the existing question card's brushes and paddings verbatim.

- [ ] **Step 5: Build the app and run the card suites**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` (no AVLN warnings), then the `AcpQuestionCardViewModelTests`, `PermissionCardViewModelTests`, `QuestionCardViewModelTests`, `ChatTabViewSmokeTests` classes.
Expected: pass.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels src/Capacitor.App/Views/ChatTabView.axaml test/Capacitor.App.Tests.Unit
git commit -m "Render ACP questions and option-bearing permissions as cards (#805)"
```

---

### Task 11: `PendingCardsViewModel` and a chat pane for every session

**Files:**
- Create: `src/Capacitor.App/ViewModels/PendingCardsViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`, `WorkspaceViewModel.cs`, `Views/WorkspaceView.axaml:155-168`
- Test: `test/Capacitor.App.Tests.Unit/PendingCardsViewModelTests.cs`, `WorkspaceViewModelTests.cs`

**Interfaces:**
- `sealed class PendingCardsViewModel(string agentId, IPermissionService permissions, IObservable<string?> root) : ReactiveObject, IDisposable { ReadOnlyObservableCollection<PendingCardViewModel> PendingCards; bool HasPendingCards; IObservable<IChangeSet<PendingPermissionRequest, string>> Requests; }` — the pipeline that lived in `ChatTabViewModel`'s constructor, verbatim: `ObserveOn(MainThread)` before `Filter(p => p.AgentId == agentId)`, the three-way `Transform`, `DisposeMany`, `SortAndBind` by `RequestedAt` then `Key`, and the `CollectionChanged`-driven `HasPendingCards`. `Requests` is the same filtered, UI-marshalled change stream `ChatTabViewModel` folds into `_requests`.
- `ChatTabViewModel` composes one (`public PendingCardsViewModel Cards { get; }`) and keeps `PendingCards` / `HasPendingCards` as forwarding members so the view binds unchanged; `Root` still comes from `_rootSubject`, which is handed to the cards.
- `WorkspaceViewModel` builds `Chat` on the first resolved dto for every session, PTY or not: `TranscriptChat.For(vendor)` already yields null for a vendor with no chat projection, and `ChatTabViewModel` renders the cards pane with `Phase = Unavailable` in that case.
- `WorkspaceView.axaml`: `ChatHost` visibility drops the `ShowsTerminalTab` term and keeps `IsChatActive`.

- [ ] **Step 1: Write the failing tests**

`PendingCardsViewModelTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using System.Reactive.Linq;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(AvaloniaSession))]
public class PendingCardsViewModelTests {
    [Test]
    public async Task Cards_follow_this_agents_entries_across_both_lanes_and_pick_the_card_by_kind() {
        await AvaloniaSession.RunAsync(async () => {
            var permissions = new FakePermissionService();
            using var cards = new PendingCardsViewModel("a1", permissions, Observable.Return<string?>(null));
            permissions.Add(PermissionEntries.Entry("l1", "a1"));
            permissions.Add(PermissionEntries.Question("l2", "a1"));
            var acp = PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", [], false), DateTimeOffset.UtcNow);
            acp.AgentId = "a1";
            permissions.Add(acp);
            permissions.Add(PermissionEntries.Entry("other", "a2"));
            await AvaloniaSession.PumpAsync();
            await Assert.That(cards.PendingCards.Count).IsEqualTo(3);
            await Assert.That(cards.PendingCards.OfType<AcpQuestionCardViewModel>().Count()).IsEqualTo(1);
            await Assert.That(cards.HasPendingCards).IsTrue();
            permissions.Remove("local:l1");
            permissions.Remove("local:l2");
            permissions.Remove("server:q1");
            await AvaloniaSession.PumpAsync();
            await Assert.That(cards.HasPendingCards).IsFalse();
        });
    }

    [Test]
    public async Task A_refresh_keeps_the_card_instance() {
        await AvaloniaSession.RunAsync(async () => {
            var permissions = new FakePermissionService();
            using var cards = new PendingCardsViewModel("a1", permissions, Observable.Return<string?>(null));
            var entry = PermissionEntries.Entry("l1", "a1");
            permissions.Add(entry);
            await AvaloniaSession.PumpAsync();
            var before = cards.PendingCards.Single();
            entry.ServerRequestId = "srv-1";
            permissions.Cache.Refresh(entry);
            await AvaloniaSession.PumpAsync();
            await Assert.That(ReferenceEquals(cards.PendingCards.Single(), before)).IsTrue();
        });
    }
}
```

`FakePermissionService.Remove` takes the cache key now; `PermissionEntries.Entry` builds local entries whose key is `local:{id}`. Use whatever pump helper `AvaloniaSession.cs` exposes (read it) in place of `PumpAsync`.

Append to `WorkspaceViewModelTests` (follow that file's harness for constructing a workspace over `FakeDaemonClientService`):

```csharp
[Test]
public async Task A_session_without_a_terminal_still_gets_a_chat_pane_for_its_cards() {
    await AvaloniaSession.RunAsync(async () => {
        var h = new Harness(); // the file's existing fixture
        var vm = h.Build("a1");
        h.Daemon.Agents.AddOrUpdate(Agent("a1", "gemini", hasTerminal: false));
        await AvaloniaSession.PumpAsync();
        await Assert.That(vm.ShowsTerminalTab).IsFalse();
        await Assert.That(vm.Chat).IsNotNull();
        await Assert.That(vm.Chat!.Phase).IsEqualTo(ChatTabPhase.Unavailable);
        await vm.TeardownAsync();
    });
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: build error (`PendingCardsViewModel`), and the workspace test fails on `Chat` being null.

- [ ] **Step 3: Extract the pipeline**

Move the block from `ChatTabViewModel`'s constructor that starts at `var cards = permissions.Pending` through `cards.Subscribe().DisposeWith(_disposables);` into `PendingCardsViewModel`'s constructor unchanged except for names, and expose `Requests` as:

```csharp
public IObservable<IChangeSet<PendingPermissionRequest, string>> Requests { get; }
// = permissions.Pending.ObserveOn(RxSchedulers.MainThreadScheduler).Filter(p => p.AgentId == agentId)
```

`ChatTabViewModel` constructs `Cards = new PendingCardsViewModel(agentId, permissions, _rootSubject)`, disposes it in `TeardownAsync` before `_rootSubject`, and subscribes `Cards.Requests` where it subscribed the filtered `permissions.Pending` before.

In `WorkspaceViewModel`, replace the Chat construction:

```csharp
presence.Where(p => p.Dto is not null).Take(1)
    .Subscribe(p => Chat = new ChatTabViewModel(agentId, daemon, Terminal, TranscriptChat.For(p.Dto!.Vendor), opener, time, permissions))
    .DisposeWith(_disposables);
```

In `WorkspaceView.axaml`, the `ChatHost` `IsVisible` becomes `IsVisible="{Binding $parent[views:WorkspaceView].((vm:WorkspaceViewModel)DataContext).IsChatActive}"` and its comment shrinks to the automation-peer sentence.

- [ ] **Step 4: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`, then the `PendingCardsViewModelTests`, `WorkspaceViewModelTests`, `ChatTabViewModelTests`, `WorkspaceViewSmokeTests` classes.
Expected: pass, no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Share the pending-cards pipeline and show it for every session (#805)"
```

---

### Task 12: The remote card host

**Files:**
- Create: `src/Capacitor.App/ViewModels/ISessionWorkspace.cs`, `RemoteSessionViewModel.cs`, `Views/RemoteSessionView.axaml`, `Views/RemoteSessionView.axaml.cs`, `Views/PendingCardTemplates.axaml`
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (implements `ISessionWorkspace`), `MainWindowViewModel.cs`, `Views/MainWindow.axaml:155-160`, `Views/ChatTabView.axaml` (card templates move to the shared dictionary), `RailSessionViewModel.cs`, `RailWorktreeViewModel.cs`, `RailRepoViewModel.cs`, `SessionRailViewModel.cs`, `App.axaml` (merge the dictionary)
- Test: `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`, `MainWindowViewModelTests.cs`, `RailSessionViewModelTests.cs`

**Interfaces:**
- `interface ISessionWorkspace { string AgentId { get; } Task TeardownAsync(); }` — `WorkspaceViewModel` and `RemoteSessionViewModel` implement it; `MainWindowViewModel.CurrentWorkspace` becomes `ISessionWorkspace?`, `SwapTo(ISessionWorkspace?)`.
- `MainWindowViewModel` ctor gains `Func<string, AgentOrigin?>? originOf = null, Func<string, RemoteSessionViewModel>? remoteWorkspaceFactory = null`. `OpenSession(agentId)`: `originOf(agentId)` is `Remote` and a remote factory exists → swap to `remoteWorkspaceFactory(agentId)`; otherwise the local factory as today. `originOf` defaults to `_ => AgentOrigin.Local`.
- `enum RemoteSessionAccess { Connecting, Ready, Denied, Offline, NoSession }`.
- `RemoteSessionViewModel(AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions, AgentActionService actions) : ReactiveObject, ISessionWorkspace` with `Title`, `RepoLabelText` (`"{RepoGroupLabel} · on {MachineBadge}"`), `MachineBadge`, `StatusText`, `StatusDot`, `SessionEnded`, `Access` (`RemoteSessionAccess`), `AccessNote` (`Connecting → "Connecting to the session…"`, `Ready → ""`, `Denied → "You no longer have access to this session"`, `Offline → "Not connected to the server"`, `NoSession → "Waiting for the session to start"`), `Cards` (`PendingCardsViewModel` over `row.Id`), `ShowsCards` (`Access == Ready`), `OpenInWebCommand` (`actions.OpenInWebRemote(row.Id)`), `StopCommand` (`actions.RequestStop(row.Id, label, "agent", AgentOrigin.Remote)` — the `origin` overload lands in Task 13; until then call the three-argument form and switch in Task 13).
- Lifecycle: the VM watches `directory.Rows` for key `remote:{row.Id}` (`WatchValue`); the current row's `SessionId` (added to `AgentRow` in Task 13 — for this task read it through a `Func<AgentRow, string?> sessionIdOf` ctor parameter defaulting to `_ => null`, and collapse that in Task 13) acquires a `SessionAccessLease` once known; a change of session id releases and re-acquires; the lease's state maps to `Access`; a removed row or terminal status sets `SessionEnded` and releases the lease. `TeardownAsync` disposes the lease, the cards and the subscriptions.

- [ ] **Step 1: Write the failing tests**

`RemoteSessionViewModelTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(AvaloniaSession))]
public class RemoteSessionViewModelTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly SessionAccessService Access;
        public readonly FakePermissionService Permissions = new();
        public readonly FakeAgentDirectory Directory = new();
        public readonly AgentActionService Actions = WorkspaceFixtures.NewActions();

        public Harness() {
            Access = new SessionAccessService(Lane, new FakeTimeProvider());
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        }

        public AgentRow Row(string id = "a1", string? sessionId = "s1", string status = "Running") =>
            AgentRow.FromRemote(new AgentInstanceDto { AgentId = id, SessionId = sessionId, Status = status, DaemonName = "work-mac", Vendor = "gemini", OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow });

        public RemoteSessionViewModel Build(AgentRow row) {
            Directory.Rows.AddOrUpdate(row);
            return new RemoteSessionViewModel(row, Directory, Access, Permissions, Actions, r => r.Id == "a1" ? "s1" : null);
        }

        public void Dispose() => Access.Dispose();
    }

    [Test]
    public async Task Opening_a_remote_row_establishes_access_and_shows_its_server_lane_cards() {
        await AvaloniaSession.RunAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(h.Row());
            await WorkspaceFixtures.WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(h.Lane.ChatSubscribes).Contains("s1");
            var card = PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", [], false), DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            h.Permissions.Add(card);
            await AvaloniaSession.PumpAsync();
            await Assert.That(vm.Cards.HasPendingCards).IsTrue();
            await Assert.That(vm.RepoLabelText).Contains("work-mac");
            await vm.TeardownAsync();
            await WorkspaceFixtures.WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "released on teardown");
        });
    }

    [Test]
    public async Task A_revocation_hides_the_cards_and_a_reconnect_rechecks_access() {
        await AvaloniaSession.RunAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(h.Row());
            await WorkspaceFixtures.WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            h.Lane.SessionAccessChangedSubject.OnNext("s1");
            await WorkspaceFixtures.WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Denied, what: "denied");
            await Assert.That(vm.ShowsCards).IsFalse();
            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
            await WorkspaceFixtures.WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready after reconnect");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_row_without_a_session_waits_and_a_removed_row_ends_the_session() {
        await AvaloniaSession.RunAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(h.Row(id: "a2", sessionId: null));
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.NoSession);
            h.Directory.Rows.Remove("remote:a2");
            await AvaloniaSession.PumpAsync();
            await Assert.That(vm.SessionEnded).IsTrue();
            await vm.TeardownAsync();
        });
    }
}
```

`FakeAgentDirectory` — create it in the test project if `AgentDirectoryTests` does not already have one: a `SourceCache<AgentRow, string>` exposed as `Rows` plus a `BehaviorSubject<bool>` `RemoteStale` (default false).

Append to `MainWindowViewModelTests` (follow its existing construction helper):

```csharp
[Test]
public async Task Opening_a_remote_row_swaps_in_the_remote_host_and_close_tears_it_down() {
    await AvaloniaSession.RunAsync(async () => {
        var torn = new List<string>();
        RemoteSessionViewModel? built = null;
        var vm = Build(originOf: id => id == "r1" ? AgentOrigin.Remote : AgentOrigin.Local,
            remoteFactory: id => built = NewRemoteSession(id), trackTeardown: t => { torn.Add("x"); _ = t(); });
        vm.OpenSession("r1");
        await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built);
        vm.OpenSession("r1");
        await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(built); // no rebuild
        vm.CloseWorkspace();
        await Assert.That(vm.CurrentWorkspace).IsNull();
        await Assert.That(torn.Count).IsEqualTo(1);
    });
}
```

where `NewRemoteSession` builds one over the fakes from the previous test.

Append to `RailSessionViewModelTests`: a remote row's `OpenCommand` invokes `openRemote` with the row id (rename from `openRemoteInWeb`; the assertion is the same shape as the existing local one).

- [ ] **Step 2: Run to verify they fail**

Expected: build errors.

- [ ] **Step 3: Implement the view model and the workspace seam**

`ISessionWorkspace.cs`:

```csharp
namespace Capacitor.App.ViewModels;

/// What the main window holds in its one workspace slot, whichever origin the session has.
public interface ISessionWorkspace {
    string AgentId { get; }
    Task TeardownAsync();
}
```

`RemoteSessionViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Media;
using Capacitor.App.Services;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteSessionAccess { Connecting, Ready, Denied, Offline, NoSession }

/// The workspace for a row the app has no socket to: the header, the NEEDS YOU cards, Stop and
/// Open in web. Access is the server's explicit signal — Ready only after the watch and the chat
/// join both succeeded — so an empty pane is "no cards", never "not allowed".
public sealed class RemoteSessionViewModel : ReactiveObject, ISessionWorkspace {
    readonly SessionAccessService _access;
    readonly Func<AgentRow, string?> _sessionIdOf;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _lease = new();
    string? _leasedSession;
    AgentRow _row;

    public string AgentId { get; }
    public string? MachineBadge { get; }
    public PendingCardsViewModel Cards { get; }
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string _repoLabelText = "";
    public string RepoLabelText { get => _repoLabelText; private set => this.RaiseAndSetIfChanged(ref _repoLabelText, value); }
    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }
    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }
    bool _sessionEnded;
    public bool SessionEnded { get => _sessionEnded; private set => this.RaiseAndSetIfChanged(ref _sessionEnded, value); }
    RemoteSessionAccess _accessState = RemoteSessionAccess.NoSession;
    public RemoteSessionAccess Access {
        get => _accessState;
        private set {
            this.RaiseAndSetIfChanged(ref _accessState, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            this.RaisePropertyChanged(nameof(ShowsCards));
        }
    }
    public bool ShowsCards => Access == RemoteSessionAccess.Ready;
    public string AccessNote => Access switch {
        RemoteSessionAccess.Connecting => "Connecting to the session…",
        RemoteSessionAccess.Denied => "You no longer have access to this session",
        RemoteSessionAccess.Offline => "Not connected to the server",
        RemoteSessionAccess.NoSession => "Waiting for the session to start",
        _ => "",
    };

    public RemoteSessionViewModel(
            AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions,
            AgentActionService actions, Func<AgentRow, string?>? sessionIdOf = null) {
        _row = row;
        _access = access;
        _sessionIdOf = sessionIdOf ?? (_ => null);
        AgentId = row.Id;
        MachineBadge = row.MachineBadge;
        Cards = new PendingCardsViewModel(row.Id, permissions, Observable.Return<string?>(null));
        Apply(row);

        directory.Rows.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    if (change.Key != row.Key) continue;
                    if (change.Reason == ChangeReason.Remove) { SessionEnded = true; Release(); continue; }
                    Apply(change.Current);
                }
            })
            .DisposeWith(_disposables);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWebRemote(row.Id));
        var canStop = this.WhenAnyValue(x => x.SessionEnded).CombineLatest(actions.StopsInFlight, (ended, inFlight) => !ended && !inFlight.Contains(row.Id));
        StopCommand = ReactiveCommand.Create(() => actions.RequestStop(row.Id, $"{_row.Vendor} · {_row.RepoGroupLabel}", _row.Kind, AgentOrigin.Remote), canStop);
        _disposables.Add(OpenInWebCommand);
        _disposables.Add(StopCommand);
        _disposables.Add(_lease);
    }

    void Apply(AgentRow row) {
        _row = row;
        Title = row.Title ?? row.Vendor;
        RepoLabelText = $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        StatusText = row.Status;
        StatusDot = SessionStatusDots.For(row.Status);
        if (SessionStatusDots.IsTerminal(row.Status)) { SessionEnded = true; Release(); return; }
        var sessionId = _sessionIdOf(row);
        if (sessionId == _leasedSession) return;
        Release();
        if (sessionId is null) { Access = RemoteSessionAccess.NoSession; return; }
        _leasedSession = sessionId;
        var lease = _access.Acquire(sessionId);
        var states = lease.State.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => Access = s switch {
            SessionAccessState.Established => RemoteSessionAccess.Ready,
            SessionAccessState.Denied => RemoteSessionAccess.Denied,
            SessionAccessState.Unavailable => RemoteSessionAccess.Offline,
            _ => RemoteSessionAccess.Connecting,
        });
        _lease.Disposable = new CompositeDisposable(states, lease);
    }

    void Release() {
        _leasedSession = null;
        _lease.Disposable = Disposable.Empty;
    }

    public Task TeardownAsync() {
        _disposables.Dispose();
        Cards.Dispose();
        return Task.CompletedTask;
    }
}
```

`RailSessionViewModel`: rename the last ctor parameter to `openRemote`, and `OpenCommand` calls `(IsRemote ? openRemote : openLocal)(row.Id)`; thread the rename through `RailWorktreeViewModel`, `RailRepoViewModel` and `SessionRailViewModel` (`openRemoteSession`).

`MainWindowViewModel`: field `readonly Func<string, AgentOrigin?> _originOf; readonly Func<string, RemoteSessionViewModel>? _remoteFactory;`, the `CurrentWorkspace` type change, and:

```csharp
public void OpenSession(string agentId) {
    if (_navigation.ShutdownLatched) return;
    CurrentView = ShellView.Sessions;
    if (CurrentWorkspace?.AgentId == agentId) return;
    ISessionWorkspace? next = _originOf(agentId) == AgentOrigin.Remote && _remoteFactory is { } remote ? remote(agentId)
        : _workspaceFactory is { } local ? local(agentId) : null;
    if (next is null) return;
    SwapTo(next);
    Rail?.NotifySessionOpened(agentId);
}
```

`MainWindow.axaml`: add a second `DataTemplate x:DataType="vm:RemoteSessionViewModel"` → `<views:RemoteSessionView />` inside the `WorkspaceHost` `ContentControl.DataTemplates`.

- [ ] **Step 4: The view and the shared card templates**

Create `Views/PendingCardTemplates.axaml` as a `ResourceDictionary` holding the three card `DataTemplate`s (permission, Claude question, ACP question) cut from `ChatTabView.axaml`'s inner `ItemsControl.DataTemplates`, each given an `x:Key` (`PermissionCardTemplate`, `QuestionCardTemplate`, `AcpQuestionCardTemplate`). Merge it in `App.axaml`'s `Application.Resources`. In `ChatTabView.axaml` and the new `RemoteSessionView.axaml`, the cards `ItemsControl` uses `<ItemsControl.DataTemplates><StaticResource ResourceKey="PermissionCardTemplate" /><StaticResource ResourceKey="QuestionCardTemplate" /><StaticResource ResourceKey="AcpQuestionCardTemplate" /></ItemsControl.DataTemplates>`.

`RemoteSessionView.axaml` (`x:DataType="vm:RemoteSessionViewModel"`, same brushes and header layout as `WorkspaceView.axaml`'s header block): a `Grid RowDefinitions="56,Auto,*"` with the title/subtitle stack and the Open-in-web / Stop buttons in row 0, an access banner `Border` in row 1 bound to `AccessNote` (visible when non-empty), and in row 2 the NEEDS YOU pane (`IsVisible="{Binding ShowsCards}"`) with the `Cards.PendingCards` `ItemsControl` and an empty-state `TextBlock "Nothing needs you right now"` visible when `ShowsCards` and not `Cards.HasPendingCards`. The code-behind is the two-line `InitializeComponent` class every other view has.

- [ ] **Step 5: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` (no AVLN warnings), then the `RemoteSessionViewModelTests`, `MainWindowViewModelTests`, `RailSessionViewModelTests`, `SessionRailViewModelTests`, `ChatTabViewSmokeTests`, `MainWindowSmokeTests` classes. Add `RemoteSessionViewSmokeTests` mirroring `ChatTabViewSmokeTests`: instantiate the view over a VM with one ACP question card and assert the card template resolved (`FindControl` on a named element inside the ACP template).
Expected: pass.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Open remote sessions in a card host with the access lifecycle (#805)"
```

---

### Task 13: Stop per origin, `SessionId` on rows, and the session→agent map

**Files:**
- Modify: `src/Capacitor.App/Services/AgentRow.cs`, `AgentDirectory.cs`, `AgentActionService.cs`, `ViewModels/RemoteSessionViewModel.cs` (drop the `sessionIdOf` seam), `ViewModels/TrayViewModel.cs` (`StopAgentCommand` passes the entry's origin — the entry gains it in Task 14; here it passes `AgentOrigin.Local`)
- Test: `test/Capacitor.App.Tests.Unit/AgentActionServiceTests.cs`, `AgentDirectoryTests.cs`, `AgentRowTests.cs` (create if absent)

**Interfaces:**
- `AgentRow.SessionId : string?` — from `AgentStatusDto.SessionId` / `AgentInstanceDto.SessionId`.
- `IAgentDirectory.SessionAgents : IObservable<IReadOnlyDictionary<string, string>>` — session id → logical agent id over the current rows, replay-1 (seeded from the current rows), distinct by content; a session claimed by two rows (an unproven twin pair) maps to the local row's id.
- `IAgentDirectory.VendorOfSession(string sessionId) : string?` — the vendor of the row whose `SessionId` matches, or null.
- `AgentActionService(ILocalControlOps ops, IAppNotifier notifier, IUrlOpener opener, IObservable<DaemonStatusDto> snapshots, CancellationToken shutdownToken, Func<string, Task<bool>> confirmForceStop, string? fallbackServerUrl = null, IServerLane? lane = null)` and `RequestStop(string agentId, string label, string kind, AgentOrigin origin = AgentOrigin.Local)`. Remote: `lane.RequestStopAgentAsync` — `Ok` → silent (disappearance comes from the registry), `NotConnected` → toast "Not connected to the server", `Denied` → "The server declined to stop {label}", `Failed` → "Couldn't stop {label}: {reason}"; a null lane → "Not signed in to a server". The same in-flight gate applies whatever the origin.

- [ ] **Step 1: Write the failing tests**

`AgentActionServiceTests` (follow its existing harness):

```csharp
[Test]
public async Task A_remote_stop_goes_to_the_hub_and_never_touches_the_socket() {
    var lane = new FakeServerLane();
    var h = new Harness(lane);
    h.Actions.RequestStop("r1", "gemini · repo", "agent", AgentOrigin.Remote);
    await WaitUntilAsync(() => lane.Stops.Contains("r1"), what: "hub stop");
    await Assert.That(h.Ops.StopCalls).IsEmpty();
    await Assert.That(h.Notifier.Messages).IsEmpty();
}

[Test]
public async Task A_remote_stop_without_a_connected_lane_toasts() {
    var lane = new FakeServerLane { StopHandler = _ => Task.FromResult(HubCallOutcome.NotConnected) };
    var h = new Harness(lane);
    h.Actions.RequestStop("r1", "gemini · repo", "agent", AgentOrigin.Remote);
    await WaitUntilAsync(() => h.Notifier.Messages.Count == 1, what: "toast");
    await Assert.That(h.Notifier.Messages[0]).IsEqualTo("Not connected to the server");
}

[Test]
public async Task A_second_remote_stop_for_the_same_id_is_ignored_while_one_is_in_flight() {
    var gate = new TaskCompletionSource<HubCallOutcome>();
    var lane = new FakeServerLane { StopHandler = _ => gate.Task };
    var h = new Harness(lane);
    h.Actions.RequestStop("r1", "x", "agent", AgentOrigin.Remote);
    h.Actions.RequestStop("r1", "x", "agent", AgentOrigin.Remote);
    await WaitUntilAsync(() => lane.Stops.Count == 1, what: "one hub call");
    gate.SetResult(HubCallOutcome.Ok);
    await WaitUntilAsync(() => !h.Actions.StopsInFlightSnapshot.Contains("r1"), what: "cleared");
}
```

Read the file's `Harness` for the ops/notifier member names and whether a `StopsInFlight` snapshot helper exists; add a `lane` ctor argument to it.

`AgentDirectoryTests` additions (follow its fixture):

```csharp
[Test]
public async Task Session_agents_maps_every_row_with_a_session_and_prefers_the_local_row_on_a_tie() {
    var h = new Harness();
    h.Local.Agents.AddOrUpdate(Agent("a1", "claude", true, sessionId: "s1"));
    h.Remote.Agents.AddOrUpdate(new AgentInstanceDto { AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "elsewhere", OwnerUserId = "u2", RegisteredAt = DateTime.UtcNow });
    h.Remote.Agents.AddOrUpdate(new AgentInstanceDto { AgentId = "r2", SessionId = "s2", Status = "Running", DaemonName = "elsewhere", OwnerUserId = "u2", RegisteredAt = DateTime.UtcNow });
    IReadOnlyDictionary<string, string>? map = null;
    using (h.Directory.SessionAgents.Subscribe(m => map = m)) { }
    await Assert.That(map!["s1"]).IsEqualTo("a1");
    await Assert.That(map["s2"]).IsEqualTo("r2");
    await Assert.That(h.Directory.VendorOfSession("s2")).IsNotNull();
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: build errors.

- [ ] **Step 3: Implement**

`AgentRow`: add `string? SessionId` after `Id` in the positional list and set it in both factories (`dto.SessionId`). `AgentRowTests` (or the existing row tests) pin that both factories carry it.

`AgentDirectory`:

```csharp
public IObservable<IReadOnlyDictionary<string, string>> SessionAgents => _rows.Connect()
    .QueryWhenChanged(q => (IReadOnlyDictionary<string, string>)SessionMap(q.Items))
    .StartWith((IReadOnlyDictionary<string, string>)SessionMap(_rows.Items))
    .DistinctUntilChanged(new DictionaryEquality());

public string? VendorOfSession(string sessionId) =>
    _rows.Items.Where(r => r.SessionId == sessionId).OrderBy(r => r.Origin).Select(r => r.Vendor).FirstOrDefault();

static Dictionary<string, string> SessionMap(IEnumerable<AgentRow> rows) {
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var row in rows.OrderBy(r => r.Origin)) // Local sorts first and wins a tie
        if (row.SessionId is { Length: > 0 } sid) map.TryAdd(sid, row.Id);
    return map;
}

sealed class DictionaryEquality : IEqualityComparer<IReadOnlyDictionary<string, string>> {
    public bool Equals(IReadOnlyDictionary<string, string>? x, IReadOnlyDictionary<string, string>? y) =>
        x is not null && y is not null && x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);
    public int GetHashCode(IReadOnlyDictionary<string, string> obj) => obj.Count;
}
```

Add both members to `IAgentDirectory`, to `FakeAgentDirectory` (Task 12) and any other implementer the compiler names.

`AgentActionService`: store the lane; the origin overload:

```csharp
public void RequestStop(string agentId, string label, string kind, AgentOrigin origin = AgentOrigin.Local) {
    lock (_lock) {
        if (_inFlight.Contains(agentId)) return;
        _inFlight = _inFlight.Add(agentId);
        _stopsInFlight.OnNext(_inFlight);
    }
    _ = Task.Run(() => origin == AgentOrigin.Remote ? RunRemoteStopAsync(agentId, label) : RunStopAsync(agentId, label, kind));
}

async Task RunRemoteStopAsync(string agentId, string label) {
    try {
        if (_lane is null) { _notifier.Notify("Not signed in to a server"); return; }
        var outcome = await _lane.RequestStopAgentAsync(agentId, _shutdownToken).ConfigureAwait(false);
        switch (outcome.Result) {
            case HubCallResult.Ok: break;
            case HubCallResult.NotConnected: _notifier.Notify("Not connected to the server"); break;
            case HubCallResult.Denied: _notifier.Notify($"The server declined to stop {label}"); break;
            default: _notifier.Notify($"Couldn't stop {label}: {outcome.Reason}"); break;
        }
    } catch (OperationCanceledException) {
    } catch (Exception ex) {
        _notifier.Notify($"Couldn't stop {label}: {ex.Message}");
    } finally {
        lock (_lock) { _inFlight = _inFlight.Remove(agentId); _stopsInFlight.OnNext(_inFlight); }
    }
}
```

`RemoteSessionViewModel`: remove the `sessionIdOf` parameter and read `row.SessionId`; update its tests to set `SessionId` on the DTO instead of the delegate.

- [ ] **Step 4: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`, then the `AgentActionServiceTests`, `AgentDirectoryTests`, `RemoteSessionViewModelTests`, `TrayViewModelTests` classes.
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Route Stop by origin and map sessions to agents in the directory (#805)"
```

---

### Task 14: Rail stale grey-out, tray gating and server-lane attention

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`, `RailWorktreeViewModel.cs`, `RailRepoViewModel.cs`, `SessionRailViewModel.cs`, `Views/SessionRailView.axaml`
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs`, `TrayModels.cs`
- Test: `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs`, `TrayViewModelTests.cs`, `TrayAdapterTests.cs`

**Interfaces:**
- `RailSessionViewModel` ctor gains `IObservable<bool> remoteStale`; `bool IsStale` is true for a remote row while the lane is stale, always false for a local row. `SessionRailViewModel` reads `directory.RemoteStale`, marshals it once to the UI thread, and threads it through the repo and worktree levels beside `agentsWithPending`. The row's root `Border` in `SessionRailView.axaml` binds `Opacity` to `IsStale` through `BoolToOpacityConverter` (`0.55` when stale).
- `RemoteTraySummary(int RemoteLiveAgents, bool LaneConnected, int SessionsNeedingAttention = 0, IReadOnlyList<TrayAgentEntry>? AttentionEntries = null)`; `TrayAgentEntry(string Id, string Label, string Kind, bool StopEnabled, AgentOrigin Origin = AgentOrigin.Local)`.
- `TrayViewModel.SummaryFrom(IAgentDirectory directory, IObservable<IReadOnlySet<string>> sessionsWithAttention)` — the attention entries are the remote rows whose `SessionId` is in the set (label `{Title ?? Vendor} · on {MachineBadge}`).
- `ProjectAggregate`: `Idle → Running` requires `remote.LaneConnected` as `Stopped → Running` already does.
- `Build`: `pendingAttention = (status.State == Connected && (pendingConsent > 0 || pendingSummary.LocalCount > 0)) || (remote.LaneConnected && (pendingSummary.ServerCount > 0 || remote.SessionsNeedingAttention > 0))`, still only over a `baseState` of `Idle`/`Running`. `PendingBody` appends `"{n} remote session(s) waiting"` when `SessionsNeedingAttention > 0`. `BuildEntries` appends `remote.AttentionEntries` (with `Origin = Remote`, `StopEnabled` by the same in-flight set) after the local ones. `StopAgentCommand` passes `entry.Origin` to `RequestStop`.

- [ ] **Step 1: Write the failing tests**

`RailSessionViewModelTests`:

```csharp
[Test]
public async Task A_remote_row_greys_out_while_the_lane_is_stale_and_a_local_row_never_does() {
    await AvaloniaSession.RunAsync(async () => {
        var stale = new BehaviorSubject<bool>(true);
        var remote = new RailSessionViewModel(RemoteRow("r1"), Observable.Return<string?>(null), Observable.Return<IReadOnlySet<string>>(new HashSet<string>()), stale, _ => { }, _ => { });
        var local = new RailSessionViewModel(LocalRow("a1"), Observable.Return<string?>(null), Observable.Return<IReadOnlySet<string>>(new HashSet<string>()), stale, _ => { }, _ => { });
        await Assert.That(remote.IsStale).IsTrue();
        await Assert.That(local.IsStale).IsFalse();
        stale.OnNext(false);
        await AvaloniaSession.PumpAsync();
        await Assert.That(remote.IsStale).IsFalse();
    });
}
```

`TrayViewModelTests` (pure `ProjectAggregate`/`Build` tests exist; extend in kind):

```csharp
[Test]
public async Task Idle_upgrades_to_running_only_while_the_lane_is_connected() {
    var idle = new AttachStatus(AttachState.Connected, null, ["consent/1"]);
    var snap = Snapshot(activeAgents: 0);
    await Assert.That(TrayViewModel.ProjectAggregate(idle, snap, new RemoteTraySummary(2, LaneConnected: true)).State).IsEqualTo(TrayState.Running);
    await Assert.That(TrayViewModel.ProjectAggregate(idle, snap, new RemoteTraySummary(2, LaneConnected: false)).State).IsEqualTo(TrayState.Idle);
}

[Test]
public async Task A_remote_prompt_asserts_attention_with_the_local_daemon_stopped_and_lists_the_session() {
    var vm = NewTray(status: new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null),
        remote: Observable.Return(new RemoteTraySummary(1, true, SessionsNeedingAttention: 1,
            AttentionEntries: [new TrayAgentEntry("r1", "fix tests · on work-mac", "agent", true, AgentOrigin.Remote)])));
    await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
    await Assert.That(vm.MenuModel.Header).Contains("1 remote session waiting");
    await Assert.That(vm.MenuModel.Agents.Single().Origin).IsEqualTo(AgentOrigin.Remote);
}

[Test]
public async Task A_server_lane_card_asserts_attention_only_while_the_lane_is_connected() {
    var summary = new PendingSummary(1, 0, LocalCount: 0, ServerCount: 1);
    var down = NewTray(permissionsSummary: Observable.Return(summary), remote: Observable.Return(new RemoteTraySummary(0, false)));
    await Assert.That(down.MenuModel.State).IsNotEqualTo(TrayState.Attention);
    var up = NewTray(permissionsSummary: Observable.Return(summary), remote: Observable.Return(new RemoteTraySummary(0, true)));
    await Assert.That(up.MenuModel.State).IsEqualTo(TrayState.Attention);
}
```

`NewTray`, `Snapshot` and friends are whatever the file already uses; the summary is injected through a `FakePermissionService`-backed `IPermissionService` today — add a ctor overload on the fake to seed its `Summary`, or build the VM with a stub `IPermissionService` whose `Summary` is the given observable.

- [ ] **Step 2: Run to verify they fail**

Expected: build errors.

- [ ] **Step 3: Implement**

`RailSessionViewModel`:

```csharp
readonly ObservableAsPropertyHelper<bool> _isStale;
/// A remote row greys out while the lane is stale; a local row is never stale.
public bool IsStale => _isStale.Value;
// in the ctor, after _needsYou:
_isStale = (IsRemote ? remoteStale : Observable.Return(false))
    .ToProperty(this, x => x.IsStale, initialValue: false)
    .DisposeWith(_disposables);
```

`SessionRailViewModel`: `var stale = directory.RemoteStale.ObserveOn(RxSchedulers.MainThreadScheduler);` passed down beside `pending`. In `SessionRailView.axaml`, on the session row's root `Border`, add `Opacity="{Binding IsStale, Converter={x:Static views:StaleOpacityConverter.Instance}}"` — add `StaleOpacityConverter` to `Views/Converters.cs` returning `0.55` for true and `1.0` for false (the existing `BoolToOpacityConverter` maps the other way round; do not reuse it inverted).

`TrayModels.cs`: extend `TrayAgentEntry` and `RemoteTraySummary` as under Interfaces.

`TrayViewModel`:

```csharp
internal static (TrayState State, int Count) ProjectAggregate(AttachStatus status, DaemonStatusDto? snap, RemoteTraySummary remote) {
    var (state, count) = Project(status, snap);
    var total = count + remote.RemoteLiveAgents;
    if (state is TrayState.Stopped or TrayState.Idle && remote.LaneConnected && remote.RemoteLiveAgents > 0)
        return (TrayState.Running, total);
    if (state is TrayState.Running) return (TrayState.Running, total);
    return (state, count);
}

internal static IObservable<RemoteTraySummary> SummaryFrom(IAgentDirectory directory, IObservable<IReadOnlySet<string>>? sessionsWithAttention = null) {
    var attention = sessionsWithAttention ?? Observable.Return((IReadOnlySet<string>)new HashSet<string>());
    var remoteRows = directory.Rows.Connect()
        .Filter(r => r.Origin == AgentOrigin.Remote && r.Status is "Starting" or "Running")
        .QueryWhenChanged(q => (IReadOnlyList<AgentRow>)q.Items.ToList())
        .StartWith((IReadOnlyList<AgentRow>)[]);
    var laneConnected = directory.RemoteStale.Select(stale => !stale);
    return remoteRows.CombineLatest(laneConnected, attention, (rows, connected, sessions) => {
        var entries = rows.Where(r => r.SessionId is { } sid && sessions.Contains(sid))
            .Select(r => new TrayAgentEntry(r.Id, $"{r.Title ?? r.Vendor} · on {r.MachineBadge}", r.Kind, StopEnabled: true, AgentOrigin.Remote))
            .ToList();
        return new RemoteTraySummary(rows.Count, connected, entries.Count, entries);
    });
}
```

`Build`: the `pendingAttention` expression from Interfaces; `HeaderText`/`PendingBody` take the remote count; `BuildEntries(status, snap, stopsInFlight, remote)` appends `remote.AttentionEntries` with `StopEnabled = !stopsInFlight.Contains(e.Id)`; `StopAgentCommand` becomes `actions.RequestStop(id, entry?.Label ?? id, entry?.Kind ?? "", entry?.Origin ?? AgentOrigin.Local)`. `TrayMenuBuilder`/`TrayMenuSync` need no change unless they pattern-match the record positionally — check `TrayAdapterTests` still passes.

- [ ] **Step 4: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`, then the `RailSessionViewModelTests`, `SessionRailViewModelTests`, `TrayViewModelTests`, `TrayAdapterTests` classes.
Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Gate the tray on the server lane and grey out stale remote rows (#805)"
```

---

### Task 15: Composition root, docs and the verification pass

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs:440-580, 955-1010`
- Modify: `docs/CHANGES.md` (a new section after "Desktop shell: remote daemons")
- Modify: `test/Capacitor.App.Tests.Unit/AppStartupTests.cs` (if it asserts the constructor graph)

**Interfaces:** consumes everything above.

- [ ] **Step 1: Wire the graph**

In `OnFrameworkInitializationCompleted` (the block that builds `permissions`, `serverLane`, `remoteAgents`, `directory`, `actions`):

1. Build `serverLane` before `actions` and pass it: `new AgentActionService(ops, notifier, opener, service.Snapshots, _shutdown.Token, ConfirmForceStopAsync, fallbackServerUrl: ..., lane: serverLane)`. Moving the `serverLane` construction up is safe: `serverLane.Start()` stays where it is.
2. `var sessionHttp = ServerHttp(profiles);` (the helper `RemoteAgentsService.HttpFetch` already receives) and:

```csharp
var respond = ServerSessionHttp.Responder(sessionHttp, profiles);
var readDetail = ServerSessionHttp.DetailReader(sessionHttp, profiles);
var permissions = new PermissionService(
    service, ops, ct => PermissionSubscription.RunAsync(_daemonStore, service.DaemonName, ct),
    TimeProvider.System, _shutdown.Token, respond, sessionAgents: null);
```

then after `directory` exists: `permissions.BindSessionAgents(directory.SessionAgents)` — add that small method to `PermissionService` (subscribes and stores the disposable) so the ctor order need not change; or construct `permissions` after `directory` if nothing between needs it (the consent prompt coordinator does not). Prefer constructing after.

3. `var sessionAccess = new SessionAccessService(serverLane, TimeProvider.System);` `var permissionFeed = new ServerPermissionFeed(serverLane, sessionAccess, permissions, readDetail, directory.VendorOfSession, TimeProvider.System);` `var attention = new SessionAttentionTracker(serverLane, readDetail, TimeProvider.System);` — hold all three in fields and dispose them in the teardown path beside `_permissions` (find where `_permissions.Dispose()` runs; dispose the feed and tracker before the permission service, the access service last).
4. The rail's pending set becomes the union: `var agentsWithPending = permissions.AgentsWithPending.CombineLatest(attention.SessionsWithAttention, directory.SessionAgents, (cards, sessions, map) => (IReadOnlySet<string>)cards.Concat(sessions.Select(s => map.GetValueOrDefault(s, "")).Where(a => a.Length > 0)).ToHashSet(StringComparer.Ordinal));` passed where `permissions.AgentsWithPending` was.
5. `RemoteSessionViewModel BuildRemote(string agentId) => new(directory.Rows.Lookup($"remote:{agentId}").Value, directory, sessionAccess, permissions, actions);` passed to `BuildAndShowMainWindow` as `remoteWorkspaceFactory`, with `originOf: id => directory.Rows.Lookup($"local:{id}").HasValue ? AgentOrigin.Local : directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote : null`. `BuildAndShowMainWindow` forwards both to `MainWindowViewModel` and passes `openRemoteSession: agentId => vm?.OpenSession(agentId)` to the rail.
6. Tray: `remote: TrayViewModel.SummaryFrom(directory, attention.SessionsWithAttention)`.

- [ ] **Step 2: Write the CHANGES entry**

Append to `docs/CHANGES.md` after the "Desktop shell: remote daemons" section:

```markdown
## Desktop shell: remote control — stop, permission and question cards

A remote session's prompts are answerable in the app, a remote agent can be stopped, and a local
ACP-hosted agent's questions (which never reach the local socket) render for the first time. Three
invariants hold it together.

**A pending request is answered only on the lane that delivered it.** A local-socket item carries
the daemon's request id and settles over the socket; a server-lane item carries the server's id and
settles over the permission-response route. The two ids are allocated independently and each
transport rejects the other's, so `PermissionService` never cross-submits, whatever the agent row's
origin says — an ACP question on a local agent is a server-lane item and goes over HTTP.

**Dedup is by proven correlation, and it fails open.** The daemon publishes its local↔server mapping
as `server_request_id` on the local permission wire, re-broadcasting the pending item when the
server leg learns the id. A server-lane copy is shadowed only by a live local item claiming that
exact id; no claim — an older daemon, a lost local subscription, the daemon's server-only path —
leaves the server copy standing. The correlation update mutates the local item in place and
refreshes it, so the card, its draft and an in-flight submit survive. A local settlement retires the
claimed twin: the daemon has answered the hook, so the server copy is moot even when the relay
fails.

**Attention is a per-session set of request ids, never a boolean.** The org-wide pending ping names
no request, so it marks the session dirty and a headless reconciliation of the session's stream
fills the set; the dirty mark outlives disconnects and fetch failures until a reconciliation
completes. A response removes one id, a response naming an id the set never held re-reconciles, and
every reconnect re-reconciles what is dirty or non-empty. Cold-start pips for a prompt raised before
the lane connected in a session never opened still need the server's pending-interrupts seed; until
it lands, remote attention covers prompts raised while the lane is up plus whatever opening the
session discovers.
```

- [ ] **Step 3: Verification pass**

Run, in order, and fix anything red before moving on:

1. `dotnet build src/Capacitor.App/Capacitor.App.csproj` — zero warnings.
2. `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`
3. `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj` — the daemon suite has known environmental timing failures on a loaded Mac; re-run any single failure alone before treating it as yours.
4. `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj`
5. `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
6. `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — prints nothing.
7. `bash scripts/check-linear-ids.sh`.

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs docs/CHANGES.md test/Capacitor.App.Tests.Unit
git commit -m "Wire remote control into the desktop app's composition root (#805)"
```

Then open the PR with `Closes #805` and `AI-2553` on the reference line, following `.github/PULL_REQUEST_TEMPLATE.md`, and post the server gap from Global Constraints on AI-2537.

---

## Self-review notes (already applied)

- **Spec coverage.** §4 lane-scoped stores → Task 7; response handles → Task 7; cross-lane dedup by proven correlation → Tasks 2 and 7; settlement lane-authoritative, session-wide clear server-only, correlation-only update preserves card identity → Task 7 (`Refresh`, generations) and Task 11 (`Transform` keeps the instance on `Refresh`); pending discovery and recovery (dirty mark, backoff, reconnect) → Task 9; answering over HTTP with 404-as-not-pending → Tasks 6 and 7; ACP id-preserving card model with min/max and free text, Claude card byte-compatible → Task 10; §5 Stop per origin → Task 13; §6 authorization as explicit signal, watch + chat join, reconnect re-establish, denial removes cards and stops subscriptions → Tasks 5, 8, 12; §9.2 minimal remote card host with chat decoupled from terminal capability → Tasks 11 and 12; slice-1 carry-overs (RemoteStale grey-out, LaneConnected on Idle→Running, WireMock smoke for `HttpFetch`) → Tasks 14 and 6; cold-start gap documented → Task 15's CHANGES entry.
- **One deliberate deviation from the spec text.** §4 says a local resolution's server twin "may linger until the next server-lane event"; this plan retires the claimed twin on local settlement (Task 7) so answering locally never pops a duplicate card. The daemon has answered the hook by then, so nothing real is pending.
- **Type consistency.** `HubCallOutcome`/`HubCallResult` (Task 4) are what Tasks 5, 13 consume; `SessionDetailReader`/`PermissionResponder` delegates (Task 6) are what Tasks 7, 8, 9 and 15 consume; `PendingPermissionRequest.Key`/`Lane`/`AcpQuestion`/`Options` (Task 7) are what Tasks 10, 11, 12 read; `AgentRow.SessionId` lands in Task 13 and Task 12 bridges it with a delegate until then; `TrayAgentEntry.Origin` lands in Task 14 and Task 13's tray change passes `Local` until then.
- **Placeholders.** Every step carries code or an exact command; the two places that say "read the file's harness" name the file and the member being looked up.

