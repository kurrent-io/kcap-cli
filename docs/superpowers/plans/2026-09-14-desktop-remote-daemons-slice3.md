# Desktop Remote Daemons — Slice 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A session hosted on another machine opens in the desktop app as a real workspace: its transcript as chat, a composer that sends to it, and — for a PTY harness — a read-only terminal, all over the server, with the session's authorization lifecycle and origin transitions handled — spec §5 and §9 slice 3.

**Architecture:** The chat pane becomes transport-neutral: `ChatTabViewModel` reads rows through an `IChatTranscriptFeed` (locally a tail of the transcript file, remotely a seed from `GET api/sessions/{id}/detail` followed by a live tail of the session's event stream over the Eventuous SignalR subscription protocol) and takes its session facts through a `ChatSessionInfo` observable. Canonical events arriving from the server are parsed with the schema's proto JSON parser and mapped by the same envelope mapping and vendor rules the file path uses. A `ServerChatInput` sends over the hub. A `RemoteTerminalViewModel` subscribes to the server's per-agent terminal buffer after access is established, locks its size to the source, reports its viewport and releases it on close. `RemoteSessionViewModel` (the slice-2 card host) grows into the remote workspace with Chat and Terminal tabs, and `MainWindowViewModel` rebinds an open workspace when its row changes origin.

**Tech Stack:** .NET 10, Avalonia + ReactiveUI + DynamicData, Microsoft.AspNetCore.SignalR.Client, Eventuous.SignalR.Client (contract types + `SignalRSubscriptionClient`), Kurrent.Agent.Schema (Google.Protobuf JSON), System.Text.Json source-gen, TUnit.

**Spec:** `docs/superpowers/specs/2026-09-06-ai2371-desktop-remote-daemons-design.md` — §5 (remote workspace), §6 (authorization signals, lane loss), §3 "Origin changes and open workspaces", §8 tests, §9.3. GitHub issue #806 / Linear AI-2554. Slices 1 and 2 landed as PRs #799 and #874; their plans are `docs/superpowers/plans/2026-09-06-desktop-remote-daemons-slice1.md` and `2026-09-10-desktop-remote-daemons-slice2.md`, and their conventions carry over.

## Global Constraints

- **Server contract facts** (read from kcap-server `main` and the Eventuous source, restated so nobody re-derives them):
  - The raw-stream subscription is the Eventuous SignalR protocol: the client invokes `SubscribeToStream(string stream, ulong? fromPosition)` (position is *exclusive*; `null` means from the start) and `UnsubscribeFromStream(string stream)`; the server pushes `StreamEvent(StreamEventEnvelope)` and `StreamError(StreamSubscriptionError)`. `StreamEventEnvelope` carries `EventId, Stream, EventType, StreamPosition, GlobalPosition, Timestamp, JsonPayload, JsonMetadata?` — over the hub's snake_case JSON. Both records and the four method names ship in `Eventuous.SignalR.Client` (namespace `Eventuous.SignalR`), whose `SignalRSubscriptionClient.SubscribeAsync(stream, fromPosition, ct)` yields envelopes, drops any at or before the last seen position, and re-subscribes itself on the hub's automatic reconnect.
  - `SubscribeToStream` throws `HubException` whose message is `Not authorized to subscribe to this stream.` on denial. A session's stream is `AgentSession-{id}` where a GUID id is written without dashes (the server's `CanonicalSessionId.Normalize`).
  - `GET api/sessions/{sessionId}/detail` returns `events[]` (`event_type`, `event_number`, `timestamp`, canonical payload under `payload`/`data` in proto3 JSON with snake_case names) and `last_event_number` (-1 when empty), floored at Full access: below Full the `events` member is absent, hidden is 404.
  - `SubscribeToTerminal(agentId)` never throws: on denial it returns silently; on success it sends `TerminalDimensions(agentId, cols, rows)` to the caller when the source size is known, replays the buffer as `TerminalOutput(agentId, base64)` chunks, then streams live output to the `terminal:{agentId}` group. `UnsubscribeFromTerminal(agentId)`, `RequestResizeTerminal(agentId, cols, rows)` (bounds 1..500 × 1..200; `(0,0)` is a server-internal clear sentinel a client never sends) and `ReleaseResizeTerminal(agentId)` return nothing.
  - `SendUserInput(agentId, text, attachmentIds?)` has **three** parameters and `SendSpecialKey(agentId, key)` two; both return nothing and silently no-op when the caller lacks Full or the agent is a flow participant. The special-key vocabulary is `SpecialKeys.All` in `Capacitor.Remote.Models`.
  - `SubscribeToChat(sessionId)` returns the session's pending-input snapshot as an array of `{dispatch_id, sender_user_id, text, attachments, dispatched_at}`; `PendingInputChanged(agentId, sessionId, items)` pushes the same shape to the `chat:{sessionId}` group.
- **Every hub method's arity is frozen**; never add a parameter to an existing method name, and always pass every argument (SignalR does not backfill a missing trailing one). Every new wire record pins its names with `[JsonPropertyName]`.
- **AOT:** `Capacitor.Cli` and `kcap-daemon` publish NativeAOT; `Capacitor.App` does not. Anything under `src/Capacitor.Models.Transcripts`, `src/Capacitor.Cli.Core` or `src/Capacitor.Remote.Models` must keep `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` printing nothing. The Eventuous package is referenced by the App only.
- **Authorization is a signal, never inferred from silence:** the seed fetch and the stream subscribe are the oracles; the terminal subscribe is attempted only after the session's access lease reads Established. Watches, subscriptions and tails re-establish on every establishment.
- **Never send `(0,0)` as a terminal size, and always release a reported viewport** (`ReleaseResizeTerminal`) when the viewer stops driving it.
- **Comments:** scarce, per CLAUDE.md `## Comments` — no change narration, no spec coordinates, no review artifacts. Existing files carry both; do not add more.
- **Commits:** subject `one clause (#806)`, imperative, ≤80 chars total. Body only for a non-obvious constraint, ≤5 lines. End with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- **Tests:** TUnit on Microsoft Testing Platform. One suite: `dotnet run --project test/<Proj>/<Proj>.csproj -- --treenode-filter "/*/*/<ClassName>/*"` (glob, not `--filter`). App suites that touch a VM run under `RunOnUiAsync` with `[NotInParallel("AvaloniaSession")]`; view smoke tests under `AvaloniaSession.DispatchAsync`. `WorkspaceFixtures.WaitUntilAsync` polls a condition. No test posts to or reads from a real server.
- **Build the app after every app change:** `dotnet build src/Capacitor.App/Capacitor.App.csproj` — AVLN XAML warnings appear only on a full rebuild and must be fixed in the same commit. Run `bash scripts/check-linear-ids.sh` before every push.
- **Worktree session:** run git as plain single-line `git` commands; no heredocs.

## File map

| Path | Responsibility |
|---|---|
| `Directory.Packages.props`, `src/Capacitor.App/Capacitor.App.csproj` | the Eventuous SignalR client package |
| `src/Capacitor.Remote.Models/RemoteWire.cs`, `SessionEventDto.cs`, `QueuedInputItem.cs`, `RemoteModelsJsonContext.cs` | stream names, the stream denial token, event timestamps, the queued-input record |
| `src/Capacitor.Models.Transcripts/CanonicalEventJson.cs` | proto JSON → schema message for the five conversational event types |
| `src/Capacitor.Cli.Core/TranscriptChat.cs` | `RulesFor(vendor)`, `Project(CanonicalEvent, rules)` shared by the file and server paths |
| `src/Capacitor.App/ViewModels/IChatTranscriptFeed.cs`, `LocalTranscriptFeed.cs`, `ChatSessionInfo.cs` | the chat pane's two seams |
| `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` | reads through a feed; session facts through `ChatSessionInfo`; origin-aware cards |
| `src/Capacitor.App/Services/IServerLane.cs`, `ServerConnectionService.cs`, `NoRemoteAgents.cs`, `TerminalOutputFrame.cs`, `TerminalSize.cs`, `PendingInputUpdate.cs` | stream tail, terminal, input and queue on the lane |
| `src/Capacitor.App/ViewModels/RemoteTranscriptFeed.cs` | seed + live tail of a remote session's stream |
| `src/Capacitor.App/ViewModels/ServerChatInput.cs` | the composer channel over the hub |
| `src/Capacitor.App/Services/SpecialKeyMapper.cs`, `ITerminalSurface.cs`, `XtermTerminalSurface.cs` | keystroke → special key; source-sized surface |
| `src/Capacitor.App/ViewModels/RemoteTerminalViewModel.cs` | the read-only terminal |
| `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`, `Views/RemoteSessionView.axaml(.cs)` | the remote workspace: tabs, chat, terminal, cards |
| `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` | origin-transition rebinding |
| `src/Capacitor.App/App.axaml.cs` | composition root |
| `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`, `HubTestHost.cs`, `FakeTerminalSurface.cs`, `RemoteFixtures.cs` | scripted lane, hub and surface; shared remote event fixtures |
| `docs/CHANGES.md`, `README.md` | the invariants this slice adds; the desktop section |

---

### Task 1: Wire contracts and the Eventuous client package

**Files:**
- Modify: `Directory.Packages.props`
- Modify: `src/Capacitor.App/Capacitor.App.csproj`
- Modify: `src/Capacitor.Remote.Models/RemoteWire.cs`
- Modify: `src/Capacitor.Remote.Models/SessionEventDto.cs`
- Test: `test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Capacitor.Remote.Models.StreamNames.AgentSession(string sessionId) : string`, `StreamNames.CanonicalSessionId(string) : string`, `WireTokens.StreamNotAuthorized`, `SessionEventDto.Timestamp : DateTimeOffset?`, and the `Eventuous.SignalR.Client` package available to `Capacitor.App` (types `Eventuous.SignalR.StreamEventEnvelope`, `Eventuous.SignalR.SignalRSubscriptionMethods`, `Eventuous.SignalR.Client.SignalRSubscriptionClient`).

- [ ] **Step 1: Write the failing tests**

Append to `test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs`:

```csharp
[Test]
public async Task AgentSessionStreamNameNormalizesAGuidLikeTheServer() {
    await Assert.That(StreamNames.AgentSession("2B070DA3-EEB4-4E33-8B0E-F36EC411811B"))
        .IsEqualTo("AgentSession-2b070da3eeb44e338b0ef36ec411811b");
    await Assert.That(StreamNames.AgentSession("codex-abc")).IsEqualTo("AgentSession-codex-abc");
}

[Test]
public async Task SessionEventCarriesItsTimestampWhenPresent() {
    const string json = """{"event_type":"UserMessageReceived","event_number":3,"timestamp":"2026-09-14T10:00:00Z","payload":{"content":"hi"}}""";
    var evt = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.SessionEventDto)!;
    await Assert.That(evt.Timestamp).IsEqualTo(new DateTimeOffset(2026, 9, 14, 10, 0, 0, TimeSpan.Zero));
    var older = JsonSerializer.Deserialize("""{"event_type":"SessionEnded"}""", RemoteModelsJsonContext.Default.SessionEventDto)!;
    await Assert.That(older.Timestamp).IsNull();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj -- --treenode-filter "/*/*/WireShapeTests/*"`
Expected: compile error — `StreamNames` not defined, `Timestamp` not a member.

- [ ] **Step 3: Add the package and the contracts**

In `Directory.Packages.props`, inside the `<ItemGroup>` (alphabetical position is fine):

```xml
<PackageVersion Include="Eventuous.SignalR.Client" Version="0.16.5-alpha.0.22" />
```

In `src/Capacitor.App/Capacitor.App.csproj`, next to `<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" />`:

```xml
<PackageReference Include="Eventuous.SignalR.Client" />
```

Append to `src/Capacitor.Remote.Models/RemoteWire.cs`:

```csharp
/// Stream names the hub's raw subscription takes. The server keys a session's stream by its
/// canonical id — a GUID without dashes, any other id as given — so the same rule applies here
/// or the subscribe names a stream that does not exist.
public static class StreamNames {
    public static string AgentSession(string sessionId) => $"AgentSession-{CanonicalSessionId(sessionId)}";

    public static string CanonicalSessionId(string sessionId) =>
        Guid.TryParse(sessionId, out var guid) ? guid.ToString("N") : sessionId;
}
```

Add to `WireTokens` in the same file:

```csharp
/// HubException message for a stream subscribe the caller may not make.
public const string StreamNotAuthorized = "Not authorized to subscribe to this stream.";
```

In `src/Capacitor.Remote.Models/SessionEventDto.cs`, add after `EventNumber`:

```csharp
[JsonPropertyName("timestamp")]    public DateTimeOffset? Timestamp { get; init; }
```

- [ ] **Step 4: Run to verify pass, and that the app restores the package**

Run: `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj -- --treenode-filter "/*/*/WireShapeTests/*"`
Expected: all PASS.

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings (the package resolves from nuget.org; the version matches the one kcap-server pins).

- [ ] **Step 5: Commit**

```bash
git add Directory.Packages.props src/Capacitor.App/Capacitor.App.csproj src/Capacitor.Remote.Models/RemoteWire.cs src/Capacitor.Remote.Models/SessionEventDto.cs test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs
git commit -m "Name the session stream and take the Eventuous SignalR client (#806)"
```

---

### Task 2: Canonical event JSON → chat rows

**Files:**
- Create: `src/Capacitor.Models.Transcripts/CanonicalEventJson.cs`
- Modify: `src/Capacitor.Cli.Core/TranscriptChat.cs`
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/CanonicalEventJsonTests.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/TranscriptChatCanonicalTests.cs`

**Interfaces:**
- Consumes: `CanonicalEventTypes` constants, `TranscriptEnvelopes.From(CanonicalEvent)`, `IChatDisplayRules`, `ClaudeChatRules.Instance`, `CodexChatRules.Instance` (all existing).
- Produces: `Capacitor.Models.Transcripts.CanonicalEventJson.TryParse(string eventType, string json) : object?`; `Capacitor.Cli.Core.TranscriptChat.RulesFor(string vendor) : IChatDisplayRules?`; `TranscriptChat.Project(CanonicalEvent evt, IChatDisplayRules? rules) : ChatProjectionResult`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Models.Transcripts.Tests.Unit/CanonicalEventJsonTests.cs`:

```csharp
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class CanonicalEventJsonTests {
    [Test]
    public async Task ReadsEachConversationalTypeBySnakeCaseName() {
        var user = CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":"hello","brand_new":1}""");
        await Assert.That(user).IsTypeOf<UserMessageReceived>();
        await Assert.That(((UserMessageReceived)user!).Content).IsEqualTo("hello");

        var calls = CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantToolCallsGenerated,
            """{"tool_calls":[{"call_id":"t1","tool_name":"Bash","arguments":{"command":"ls"}}]}""");
        var call = ((AssistantToolCallsGenerated)calls!).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("t1");
        await Assert.That(call.ToolName).IsEqualTo("Bash");
        await Assert.That(call.Arguments!.Fields["command"].StringValue).IsEqualTo("ls");

        var result = CanonicalEventJson.TryParse(CanonicalEventTypes.ToolResultReceived, """{"call_id":"t1","result":"ok"}""");
        await Assert.That(((ToolResultReceived)result!).Result).IsEqualTo("ok");
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantTextGenerated, """{"content":"Hi"}""")).IsTypeOf<AssistantTextGenerated>();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantThinkingGenerated, """{"content":"hm"}""")).IsTypeOf<AssistantThinkingGenerated>();
    }

    [Test]
    public async Task UnknownTypesAndMalformedPayloadsReadAsNull() {
        await Assert.That(CanonicalEventJson.TryParse("InterruptIssued", """{"request_id":"r1"}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.SessionStarted, """{}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, "not json")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":42}""")).IsNull();
    }
}
```

`test/Capacitor.Cli.Core.Tests.Unit/TranscriptChatCanonicalTests.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptChatCanonicalTests {
    static CanonicalEvent Event(string type, object payload) => new(type, payload, Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Test]
    public async Task A_user_message_is_one_row_and_one_submitted_input_without_rules() {
        var result = TranscriptChat.Project(Event(CanonicalEventTypes.UserMessageReceived, new UserMessageReceived { Content = "hello" }), rules: null);
        await Assert.That(result.Envelopes.Count).IsEqualTo(1);
        await Assert.That(result.Envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(result.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
    }

    [Test]
    public async Task Tool_calls_become_one_row_each_and_a_result_settles_by_call_id() {
        var calls = new AssistantToolCallsGenerated();
        calls.ToolCalls.Add(new ToolCallInfo { CallId = "t1", ToolName = "Bash", Arguments = Struct.Parser.ParseJson("""{"command":"ls"}""") });
        var rows = TranscriptChat.Project(Event(CanonicalEventTypes.AssistantToolCallsGenerated, calls), rules: null).Envelopes;
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(rows[0].ToolInputJson).IsEqualTo("""{"command":"ls"}""");

        var result = TranscriptChat.Project(Event(CanonicalEventTypes.ToolResultReceived, new ToolResultReceived { CallId = "t1", Result = "ok" }), rules: null).Envelopes;
        await Assert.That(result[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(result[0].ToolCallId).IsEqualTo("t1");
    }

    [Test]
    public async Task Claude_rules_hide_a_meta_message_and_acknowledge_no_input_for_it() {
        var meta = new UserMessageReceived { Content = "<command-name>/clear</command-name>" };
        meta.Extensions[ClaudeCodeExtension.Slug] = Struct.Parser.ParseJson($$"""{"{{ClaudeCodeExtension.IsMeta}}":true}""");
        var result = TranscriptChat.Project(Event(CanonicalEventTypes.UserMessageReceived, meta), ClaudeChatRules.Instance);
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.SubmittedInputs).IsEmpty();
    }

    [Test]
    public async Task RulesFor_names_the_two_vendors_with_chat_rules() {
        await Assert.That(TranscriptChat.RulesFor("Claude")).IsSameReferenceAs(ClaudeChatRules.Instance);
        await Assert.That(TranscriptChat.RulesFor("gemini")).IsNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj -- --treenode-filter "/*/*/CanonicalEventJsonTests/*"`
Expected: compile error — `CanonicalEventJson` not defined.

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/TranscriptChatCanonicalTests/*"`
Expected: compile error — `TranscriptChat.Project` / `RulesFor` not defined.

- [ ] **Step 3: Implement**

`src/Capacitor.Models.Transcripts/CanonicalEventJson.cs`:

```csharp
using Google.Protobuf;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts;

/// Reads a persisted conversational payload back into its schema message. Unknown fields are
/// ignored: a newer server may add one, and the client must still read the event.
public static class CanonicalEventJson {
    static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// Null for a type the chat does not render or a payload that does not parse as it.
    public static object? TryParse(string eventType, string json) {
        try {
            return eventType switch {
                CanonicalEventTypes.UserMessageReceived         => Parser.Parse<UserMessageReceived>(json),
                CanonicalEventTypes.AssistantTextGenerated      => Parser.Parse<AssistantTextGenerated>(json),
                CanonicalEventTypes.AssistantThinkingGenerated  => Parser.Parse<AssistantThinkingGenerated>(json),
                CanonicalEventTypes.AssistantToolCallsGenerated => Parser.Parse<AssistantToolCallsGenerated>(json),
                CanonicalEventTypes.ToolResultReceived          => Parser.Parse<ToolResultReceived>(json),
                _ => null,
            };
        } catch (InvalidJsonException) {
            return null;
        } catch (InvalidProtocolBufferException) {
            return null;
        }
    }
}
```

In `src/Capacitor.Cli.Core/TranscriptChat.cs`, keep the file's existing usings and replace `TranscriptChatProjection.ProjectWithInputs` and the `TranscriptChat` class with:

```csharp
    public ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var result = projection.Project(line, lineNumber, receivedAt, context);
        if (result.Events.Count == 0) return new([], []);
        var shown = new List<AcpEventEnvelope>(result.Events.Count);
        var submitted = new List<string>();
        foreach (var evt in result.Events) {
            var projected = TranscriptChat.Project(evt, rules);
            shown.AddRange(projected.Envelopes);
            submitted.AddRange(projected.SubmittedInputs);
        }
        return new(shown, submitted);
    }
}

/// The one registration site in Core: a vendor's chat rules live under Harness/&lt;Vendor&gt;/ and
/// are paired with the leaf's projection here, nowhere else.
public static class TranscriptChat {
    public static readonly IChatTranscriptProjection Journal = new EnvelopeJournalProjection();

    public static TranscriptChatProjection? For(string vendor) =>
        TranscriptProjection.For(vendor) is { } projection && RulesFor(vendor) is { } rules
            ? new TranscriptChatProjection(projection, rules)
            : null;

    public static IChatDisplayRules? RulesFor(string vendor) => vendor.ToLowerInvariant() switch {
        "claude" => ClaudeChatRules.Instance,
        "codex"  => CodexChatRules.Instance,
        _        => null,
    };

    /// The rows for one canonical event, wherever it came from: the envelope mapping, then the
    /// vendor's rules when it has any. Without rules every envelope shows, and a visible user
    /// message is the submitted input.
    public static ChatProjectionResult Project(CanonicalEvent evt, IChatDisplayRules? rules) {
        var envelopes = TranscriptEnvelopes.From(evt);
        if (envelopes.Count == 0) return new([], []);
        var shown = new List<AcpEventEnvelope>(envelopes.Count);
        var submitted = new List<string>();
        foreach (var envelope in envelopes) {
            var kept = rules is null ? envelope : rules.Filter(evt, envelope);
            if (kept is { } visible) shown.Add(visible);
            var text = rules is null
                ? kept is { Kind: AcpEventKind.UserMessage } user ? user.Text : null
                : rules.SubmittedInput(evt, envelope, kept);
            if (text is { Length: > 0 }) submitted.Add(text);
        }
        return new(shown, submitted);
    }
}
```

- [ ] **Step 4: Run to verify pass, including the existing chat rule tests and the AOT publish**

Run both filters from Step 2. Expected: all PASS.

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/*ChatRules*/*"`
Expected: PASS (the file path's behaviour is unchanged).

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: prints nothing.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Models.Transcripts/CanonicalEventJson.cs src/Capacitor.Cli.Core/TranscriptChat.cs test/Capacitor.Models.Transcripts.Tests.Unit/CanonicalEventJsonTests.cs test/Capacitor.Cli.Core.Tests.Unit/TranscriptChatCanonicalTests.cs
git commit -m "Read a persisted canonical event back into chat rows (#806)"
```

---

### Task 3: The chat pane reads through a feed and a session seam

The pane's local behaviour is unchanged and every existing `ChatTabViewModel*` test must stay green untouched: the existing constructor becomes an overload that builds the local adapters, and a new primary constructor takes the seams.

**Files:**
- Create: `src/Capacitor.App/ViewModels/IChatTranscriptFeed.cs`
- Create: `src/Capacitor.App/ViewModels/LocalTranscriptFeed.cs`
- Create: `src/Capacitor.App/ViewModels/ChatSessionInfo.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/LocalTranscriptFeedTests.cs`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` (existing, unchanged, must pass)

**Interfaces:**
- Consumes: `JsonlTail`, `TailRead`, `TailStatus`, `IChatTranscriptProjection`, `ChatProjectionResult` (Core, existing); `AgentRow`, `AgentOrigin`, `SessionStatusDots` (App, existing).
- Produces:
  - `enum FeedStatus { Ok, Reset, Missing, Failed }`
  - `readonly record struct ProjectedLine(ChatProjectionResult Projection, long Offset)`
  - `record FeedRead(FeedStatus Status, IReadOnlyList<ProjectedLine> Lines, long? SnapshotOffset = null, string? Failure = null)`
  - `interface IChatTranscriptFeed : IDisposable { FeedRead ReadAppended(); long? CurrentOffset { get; } }`
  - `record ChatSessionInfo(string Status, string StatusLabel, string Vendor, string? Model, string? Root, bool? AwaitingInput, bool Ended, string ReadOnlyNotice, string? FeedKey)` with `FromLocal(AgentStatusDto dto, bool ended)`, `FromRemote(AgentRow row, bool ended)` and the static `Gone`.
  - `ChatTabViewModel(string agentId, AgentOrigin origin, IObservable<ChatSessionInfo> session, IObservable<string[]?> supportedVendors, ChatInput input, Func<string, IChatTranscriptFeed>? openFeed, IUrlOpener opener, TimeProvider time, IPermissionService permissions, string? unavailableNote = null, string? missingNote = null, IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null)` — later tasks build the pane with this one.

- [ ] **Step 1: Write the failing feed test**

`test/Capacitor.App.Tests.Unit/LocalTranscriptFeedTests.cs`:

```csharp
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The file feed: appended lines project in order with their byte offsets, a truncated file
/// resets, and a missing file says so.
public class LocalTranscriptFeedTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";

    [Test]
    public async Task Appended_lines_project_with_their_offsets_and_a_missing_file_reads_as_missing() {
        var path = Tmp.PathTo("t.jsonl");
        var logged = new List<string>();
        using var feed = new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", new FakeTimeProvider(), logged.Add);

        await Assert.That(feed.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
        await Assert.That(feed.CurrentOffset).IsNull();

        await File.WriteAllTextAsync(path, UserLine + "\n");
        var first = feed.ReadAppended();
        await Assert.That(first.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(first.Lines.Single().Offset).IsEqualTo(0);
        await Assert.That(first.Lines.Single().Projection.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
        await Assert.That(feed.CurrentOffset).IsEqualTo(UserLine.Length + 1);

        await File.AppendAllTextAsync(path, AssistantLine + "\n");
        var second = feed.ReadAppended();
        await Assert.That(second.Lines.Single().Offset).IsEqualTo(UserLine.Length + 1);
        await Assert.That(second.Lines.Single().Projection.Envelopes[0].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(logged).IsEmpty();
    }

    [Test]
    public async Task A_truncated_file_resets_and_reprojects_from_its_start() {
        var path = Tmp.PathTo("t.jsonl");
        await File.WriteAllTextAsync(path, UserLine + "\n" + AssistantLine + "\n");
        using var feed = new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", new FakeTimeProvider(), _ => { });
        await Assert.That(feed.ReadAppended().Lines.Count).IsEqualTo(2);

        await File.WriteAllTextAsync(path, UserLine + "\n");
        var reset = feed.ReadAppended();
        await Assert.That(reset.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(reset.Lines.Single().Offset).IsEqualTo(0);
        await Assert.That(reset.SnapshotOffset).IsEqualTo(UserLine.Length + 1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalTranscriptFeedTests/*"`
Expected: compile error — `LocalTranscriptFeed` / `FeedStatus` not defined.

- [ ] **Step 3: Write the seams**

`src/Capacitor.App/ViewModels/IChatTranscriptFeed.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

public enum FeedStatus { Ok, Reset, Missing, Failed }

/// One projected transcript line and where it sits in its source: a byte offset in a file, an
/// event number in a stream. Offsets only ever grow within one source, which is what lets a send
/// be acknowledged by a line that landed after it.
public readonly record struct ProjectedLine(ChatProjectionResult Projection, long Offset);

/// What one poll drained. A Reset carries every line of the rebuilt source; SnapshotOffset is
/// where the source stood as the read began, so a send made against the previous source is
/// rebased past everything the new one replays.
public sealed record FeedRead(FeedStatus Status, IReadOnlyList<ProjectedLine> Lines, long? SnapshotOffset = null, string? Failure = null);

/// The chat pane's source of rows, drained from a worker thread on every poll.
public interface IChatTranscriptFeed : IDisposable {
    FeedRead ReadAppended();
    /// Where a send made now would be acknowledged from; null while the source has no position.
    long? CurrentOffset { get; }
}
```

`src/Capacitor.App/ViewModels/LocalTranscriptFeed.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// The transcript file, tailed and projected line by line. The projection context lives exactly as
/// long as the file the tail is reading: a reset discards it with the line count.
internal sealed class LocalTranscriptFeed(
        string path, IChatTranscriptProjection projection, string agentId, TimeProvider time, Action<string> logOnce)
    : IChatTranscriptFeed {
    readonly JsonlTail _tail = new(path);
    TranscriptContext? _context;
    int _linesRead;

    public long? CurrentOffset {
        get {
            try { return new FileInfo(path).Length; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public FeedRead ReadAppended() {
        var result = _tail.ReadAppended();
        if (result.Status == TailStatus.Reset) { _context = null; _linesRead = 0; }
        var lines = new List<ProjectedLine>(result.Lines.Count);
        if (result.Lines.Count > 0) {
            // The app has no session id and persists nothing, so the agent id stands in; only
            // attachment ids would read it.
            _context ??= projection.CreateContext(agentId, null);
            _context.BeginBatch();
            var receivedAt = time.GetUtcNow();
            for (var index = 0; index < result.Lines.Count; index++) {
                // Counts the lines the tail yields, which skips blank lines, so it is not the
                // file's physical line number.
                var lineNumber = ++_linesRead;
                try {
                    lines.Add(new(projection.ProjectWithInputs(result.Lines[index], lineNumber, receivedAt, _context), result.LineStartOffsets[index]));
                } catch (Exception ex) {
                    logOnce($"projection: {ex.Message}");
                }
            }
        }
        var status = result.Status switch {
            TailStatus.Reset => FeedStatus.Reset,
            TailStatus.Missing => FeedStatus.Missing,
            TailStatus.Failed => FeedStatus.Failed,
            _ => FeedStatus.Ok,
        };
        return new(status, lines, result.SnapshotLength, result.Failure);
    }

    public void Dispose() { }
}
```

`src/Capacitor.App/ViewModels/ChatSessionInfo.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.ViewModels;

/// What the chat pane knows about its session, whichever lane it came from. FeedKey names the
/// transcript to read — a file path locally, the session id remotely — and a change of key
/// rebuilds the rows. Ended is the lane's verdict that nothing more will arrive.
public sealed record ChatSessionInfo(
        string Status, string StatusLabel, string Vendor, string? Model, string? Root, bool? AwaitingInput, bool Ended,
        string ReadOnlyNotice, string? FeedKey) {
    /// The daemon dropped the agent before this pane ever saw it.
    public static readonly ChatSessionInfo Gone = new("Completed", "Completed", "", null, null, null, true, "", null);

    public static ChatSessionInfo FromLocal(AgentStatusDto dto, bool ended) => new(
        dto.Status, SessionStatusDots.Label(dto), dto.Vendor, dto.Model,
        // Tool paths are relative to the checkout the agent runs in. An older daemon sends only
        // RepoPath: the repository for a primary, or the borrowed checkout for a reviewer.
        dto.WorktreePath ?? dto.RepoPath, dto.AwaitingInput,
        ended || SessionStatusDots.IsTerminal(dto.Status), ChatTabViewModel.ParticipantNotice(dto), dto.TranscriptPath);

    public static ChatSessionInfo FromRemote(AgentRow row, bool ended) => new(
        row.Status, SessionStatusDots.WaitsOnUser(row) ? "Waiting for input" : row.Status, row.Vendor, row.Model,
        row.RepoPath, row.AwaitingInput, ended || SessionStatusDots.IsTerminal(row.Status), "", row.SessionId);
}
```

- [ ] **Step 4: Rework `ChatTabViewModel` onto the seams**

Open `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` and make these edits; everything not named here stays as it is.

Fields: delete `_projection`, `_path`, the nested `TailLease` class, `_lease`, and the static `TranscriptLength`. Add:

```csharp
    readonly Func<string, IChatTranscriptFeed>? _openFeed;
    readonly string? _missingNote;

    /// The feed and the generation it belongs to, taken as one reference: reading them separately
    /// lets a switch land between them and tag a read of the old source with the new generation,
    /// which Apply's guard would then wave through onto the freshly cleared list.
    sealed class FeedLease(IChatTranscriptFeed feed, int generation) {
        public IChatTranscriptFeed Feed { get; } = feed;
        public int Generation { get; } = generation;
    }

    string? _feedKey;
    volatile FeedLease? _lease;

    long? CurrentOffset => _lease?.Feed.CurrentOffset;
```

`PhaseNote`: the `Missing` arm becomes `ChatTabPhase.Missing => _missingNote ?? "The transcript file is missing",`.

Constructors — replace the existing one with these two (the parameter list of the first is unchanged, so no caller or test moves):

```csharp
    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, ChatInput input,
            IChatTranscriptProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions,
            string? unavailableNote = null, IObservable<string?>? sessionId = null,
            IObservable<bool>? localDaemonOnAppServer = null)
        : this(agentId, AgentOrigin.Local, LocalSession(agentId, daemon), daemon.Snapshots.Select(s => s.Daemon.SupportedVendors),
               input, projection is null ? null : LocalFeed(agentId, projection, time), opener, time, permissions,
               unavailableNote, null, sessionId, localDaemonOnAppServer) { }

    public ChatTabViewModel(
            string agentId, AgentOrigin origin, IObservable<ChatSessionInfo> session, IObservable<string[]?> supportedVendors,
            ChatInput input, Func<string, IChatTranscriptFeed>? openFeed, IUrlOpener opener, TimeProvider time,
            IPermissionService permissions, string? unavailableNote = null, string? missingNote = null,
            IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null) {
        _agentId = agentId;
        _input = input;
        _disposables.Add(input);
        _openFeed = openFeed;
        _unavailableNote = unavailableNote;
        _missingNote = missingNote;
        _opener = opener;
        _time = time;
        _permissions = permissions;
        _lifetimeToken = _lifetime.Token;
        _phase = openFeed is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        Cards = new PendingCardsViewModel(
            agentId, origin, sessionId ?? Observable.Return<string?>(null), permissions, _rootSubject,
            localDaemonOnAppServer);
```

…and from there the body continues exactly as before, with two substitutions: the block that read

```csharp
        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged)
            .DisposeWith(_disposables);
```

becomes

```csharp
        session
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSession)
            .DisposeWith(_disposables);
```

and the block that read

```csharp
        daemon.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot => {
                _options = HostedHarnessCatalog.Build(snapshot.Daemon.SupportedVendors);
```

becomes

```csharp
        supportedVendors
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(vendors => {
                _options = HostedHarnessCatalog.Build(vendors);
```

In `SendCommand`'s body, `new QueuedChatMessage(snapshot, edits, _inputGeneration, TranscriptLength(_path))` becomes `new QueuedChatMessage(snapshot, edits, _inputGeneration, CurrentOffset)`, and `(outcome == ChatSendOutcome.Accepted && _projection is null)` becomes `(outcome == ChatSendOutcome.Accepted && _openFeed is null)`.

Add the two static adapters the local overload uses (next to `ParticipantNotice`):

```csharp
    /// The daemon cache as session facts: an add or update is the dto, a removal keeps the last
    /// facts under a Completed status. Identical revisions are not republished.
    static IObservable<ChatSessionInfo> LocalSession(string agentId, IDaemonClientService daemon) =>
        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Scan((ChatSessionInfo?)null, (last, changes) => {
                var info = last;
                foreach (var change in changes) {
                    if (change.Key != agentId) continue;
                    if (change.Reason == ChangeReason.Remove)
                        info = (info ?? ChatSessionInfo.Gone) with { Status = "Completed", StatusLabel = "Completed", Ended = true };
                    else if (change.Reason is ChangeReason.Add or ChangeReason.Update)
                        info = ChatSessionInfo.FromLocal(change.Current, ended: false);
                }
                return info;
            })
            .Where(info => info is not null)
            .Select(info => info!)
            .DistinctUntilChanged();

    static Func<string, IChatTranscriptFeed> LocalFeed(string agentId, IChatTranscriptProjection projection, TimeProvider time) =>
        path => new LocalTranscriptFeed(path, projection, agentId, time, reason => Console.Error.WriteLine($"kcap: chat transcript: {reason}"));
```

Replace `OnAgentsChanged` and `OnDto` with:

```csharp
    void OnSession(ChatSessionInfo info) {
        ReadOnlyNotice = info.ReadOnlyNotice;
        IsReadOnlyParticipant = info.ReadOnlyNotice.Length > 0;
        _vendor = info.Vendor;
        _root = info.Root;
        _rootSubject.OnNext(info.Root);
        VendorLabel = HostedHarnessCatalog.LabelFor(_options, info.Vendor);
        ModelLabel = HostedHarnessCatalog.ModelLabelFor(info.Vendor, info.Model ?? "");
        StatusText = info.StatusLabel;
        StatusDot = SessionStatusDots.For(info.Status);
        _status = info.Status;
        if (info.Ended)
            foreach (var queued in _queuedMessages) queued.MarkUnconfirmed();
        _awaitingInput = info.AwaitingInput;
        if (_openFeed is { } open && info.FeedKey is { } key && key != _feedKey) SwitchFeed(key, open);
        RefreshActivityNote();
        RefreshQueue();
    }
```

Replace `SwitchPath` with:

```csharp
    void SwitchFeed(string key, Func<string, IChatTranscriptFeed> open) {
        _items.Clear();
        _pendingTools.Clear();
        _settledTools.Clear();
        _openGroup = null;
        _marked.Clear();
        _feedKey = key;
        var previous = _lease;
        _lease = new FeedLease(open(key), Interlocked.Increment(ref _generation));
        previous?.Feed.Dispose();
        RebaseQueuedMessages(CurrentOffset);
        var wasWaiting = _phase == ChatTabPhase.Waiting;
        Phase = ChatTabPhase.Waiting;
        // The rows are gone, so the view has to re-read what stands in for them even when the phase
        // is unchanged — and only then, since the setter itself raises the note on a real change.
        if (wasWaiting) this.RaisePropertyChanged(nameof(PhaseNote));
        OnTick();
    }
```

In `OnTick`, the guard `if (_lease is not { } lease || _projection is not { } projection) return;` becomes `if (_lease is not { } lease) return;` and the call becomes `_pendingRead = ReadAndApplyAsync(lease);`.

Replace `ReadAndApplyAsync` with:

```csharp
    async Task ReadAndApplyAsync(FeedLease lease) {
        try {
            var read = await Task.Run(lease.Feed.ReadAppended).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Apply(lease.Generation, read));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }
```

`Apply` becomes `void Apply(int generation, FeedRead read)`; inside it, `case TailStatus.Missing:` / `case TailStatus.Failed:` / `case TailStatus.Reset:` become the `FeedStatus` arms, `RebaseQueuedMessages(Math.Max(read.SnapshotLength ?? 0, TranscriptLength(_path) ?? 0));` becomes `RebaseQueuedMessages(Math.Max(read.SnapshotOffset ?? 0, CurrentOffset ?? 0));`, the `queued.Rebase(_inputGeneration, Math.Max(read.SnapshotLength ?? 0, TranscriptLength(_path) ?? 0));` line becomes `queued.Rebase(_inputGeneration, Math.Max(read.SnapshotOffset ?? 0, CurrentOffset ?? 0));`, `if (lines.Count == 0)` becomes `if (read.Lines.Count == 0)`, and the loop head `foreach (var (projected, offset) in lines)` becomes `foreach (var (projected, offset) in read.Lines)`.

In `TeardownAsync`, after `_lease = null;` insert nothing; instead change that line to:

```csharp
        var lease = _lease;
        _lease = null;
        lease?.Feed.Dispose();
```

- [ ] **Step 5: Build and run the feed test and every chat suite**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalTranscriptFeedTests/*"`
Expected: PASS.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/Chat*/*"`
Expected: every `ChatTabViewModelTests`, `ChatTabViewSmokeTests`, `ChatComposerTests` and `ChatTranscriptSourceTests` test PASSES with no test edited.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/IChatTranscriptFeed.cs src/Capacitor.App/ViewModels/LocalTranscriptFeed.cs src/Capacitor.App/ViewModels/ChatSessionInfo.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/LocalTranscriptFeedTests.cs
git commit -m "Read the chat through a transcript feed and a session seam (#806)"
```

---

### Task 4: The server lane tails a stream, carries the terminal and sends input

**Files:**
- Create: `src/Capacitor.App/Services/TerminalOutputFrame.cs`, `src/Capacitor.App/Services/TerminalSize.cs`
- Modify: `src/Capacitor.App/Services/IServerLane.cs`, `src/Capacitor.App/Services/ServerConnectionService.cs`, `src/Capacitor.App/Services/NoRemoteAgents.cs` (`NoServerLane`)
- Modify: `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`, `test/Capacitor.App.Tests.Unit/HubTestHost.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs`

**Interfaces:**
- Consumes: `HubMethods`, `HubBroadcasts`, `SpecialKeys`, `WireTokens.StreamNotAuthorized` (Remote.Models); `Eventuous.SignalR.StreamEventEnvelope`, `Eventuous.SignalR.SignalRSubscriptionMethods`, `Eventuous.SignalR.Client.SignalRSubscriptionClient`.
- Produces on `IServerLane`:
  - `IObservable<TerminalOutputFrame> TerminalOutput` — `record TerminalOutputFrame(string AgentId, string Base64)`
  - `IObservable<TerminalSize> TerminalDimensions` — `record TerminalSize(string AgentId, int Cols, int Rows)`
  - `IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, CancellationToken ct)` — completes when the connection closes or the subscription is replaced; throws `HubException` at the first `MoveNextAsync` on denial; yields nothing when the lane has no live hub.
  - `Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct)`, `UnsubscribeFromTerminalAsync(string agentId, …)`, `RequestResizeTerminalAsync(string agentId, int cols, int rows, …)`, `ReleaseResizeTerminalAsync(string agentId, …)`, `SendUserInputAsync(string agentId, string text, …)`, `SendSpecialKeyAsync(string agentId, string key, …)`.
- Produces on `FakeServerLane`: `TerminalOutputSubject`, `TerminalDimensionsSubject`, `TailHandler`, `TerminalSubscribeHandler`, `UserInputHandler`, `SpecialKeyHandler`, the recorders `Tails`, `TerminalSubscribes`, `TerminalUnsubscribes`, `Resizes`, `ResizeReleases`, `UserInputs`, `SpecialKeys`, and `PushStreamEvent(StreamEventEnvelope)`, `CloseTail(string stream)`.

- [ ] **Step 1: Write the failing lane tests**

Append to `test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs` (the file already has `Lane(host)` and `Next(observable, predicate)`; add `using System.Text;`, `using Eventuous.SignalR;` and `using Microsoft.AspNetCore.SignalR;` if missing):

```csharp
[Test]
public async Task StreamTailYieldsPushedEventsInOrderAndADeniedStreamThrows() {
    await using var host = await HubTestHost.StartAsync();
    HubTestHost.StreamHandler = stream => stream != "AgentSession-hidden";
    await using var lane = Lane(host);
    lane.Start();
    await Next(lane.Status, s => s.State == ServerLaneState.Connected);

    var received = new List<StreamEventEnvelope>();
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var tail = Task.Run(async () => {
        await foreach (var envelope in lane.TailStreamAsync("AgentSession-s1", 4, cts.Token)) {
            received.Add(envelope);
            if (received.Count == 2) break;
        }
    });
    await WaitUntilAsync(() => HubTestHost.StreamSubscribes.Contains(("AgentSession-s1", (ulong?)4)), what: "the subscribe");
    await host.PushStreamEventAsync(Envelope("AgentSession-s1", 5, "hello"));
    // At or before the last seen position: the client drops it.
    await host.PushStreamEventAsync(Envelope("AgentSession-s1", 5, "duplicate"));
    await host.PushStreamEventAsync(Envelope("AgentSession-s1", 6, "world"));
    await tail.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(received.Select(e => e.StreamPosition)).IsEquivalentTo(new ulong[] { 5, 6 });
    await Assert.That(received[0].JsonPayload).Contains("hello");

    var denied = lane.TailStreamAsync("AgentSession-hidden", null, cts.Token).GetAsyncEnumerator(cts.Token);
    HubException? error = null;
    try { await denied.MoveNextAsync(); } catch (HubException ex) { error = ex; }
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Message).Contains(WireTokens.StreamNotAuthorized);
}

[Test]
public async Task TerminalSubscribeReplaysDimensionsAndBufferThenTheInvokesRecordViewportAndInput() {
    await using var host = await HubTestHost.StartAsync();
    HubTestHost.TerminalDims = (120, 40);
    HubTestHost.TerminalReplay.Add("hel"u8.ToArray());
    HubTestHost.TerminalReplay.Add("lo"u8.ToArray());
    await using var lane = Lane(host);
    lane.Start();
    await Next(lane.Status, s => s.State == ServerLaneState.Connected);

    var dims = lane.TerminalDimensions.Take(1).ToTask();
    var frames = lane.TerminalOutput.Take(2).ToList().ToTask();
    await Assert.That((await lane.SubscribeToTerminalAsync("a1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
    await Assert.That(await dims.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(new TerminalSize("a1", 120, 40));
    var received = await frames.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(received.Select(f => Encoding.UTF8.GetString(Convert.FromBase64String(f.Base64)))).IsEquivalentTo(new[] { "hel", "lo" });

    await lane.RequestResizeTerminalAsync("a1", 100, 30, CancellationToken.None);
    await lane.ReleaseResizeTerminalAsync("a1", CancellationToken.None);
    await lane.SendUserInputAsync("a1", "fix it", CancellationToken.None);
    await lane.SendSpecialKeyAsync("a1", SpecialKeys.Escape, CancellationToken.None);
    await lane.UnsubscribeFromTerminalAsync("a1", CancellationToken.None);
    await Assert.That(HubTestHost.Resizes).Contains(("a1", 100, 30));
    await Assert.That(HubTestHost.ResizeReleases).Contains("a1");
    await Assert.That(HubTestHost.UserInputs).Contains(("a1", "fix it"));
    await Assert.That(HubTestHost.SpecialKeys).Contains(("a1", "Escape"));
    await Assert.That(HubTestHost.TerminalUnsubscribes).Contains("a1");
}

static StreamEventEnvelope Envelope(string stream, ulong position, string content) => new() {
    EventId = Guid.NewGuid(), Stream = stream, EventType = CanonicalEventTypes.UserMessageReceived,
    StreamPosition = position, GlobalPosition = position, Timestamp = DateTime.UtcNow,
    JsonPayload = $$"""{"content":"{{content}}"}""",
};
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerConnectionServiceTests/*"`
Expected: compile error — `TailStreamAsync`, `TerminalSize`, `HubTestHost.StreamHandler` not defined.

- [ ] **Step 3: Extend the hub host and the fake lane**

In `test/Capacitor.App.Tests.Unit/HubTestHost.cs`, add `using Eventuous.SignalR;` and these statics beside the existing ones (reset each in `StartAsync` the same way the existing ones are reset):

```csharp
    public static Func<string, bool> StreamHandler { get; set; } = _ => true;
    public static List<(string Stream, ulong? From)> StreamSubscribes { get; } = [];
    public static List<string> StreamUnsubscribes { get; } = [];
    public static Func<string, bool> TerminalHandler { get; set; } = _ => true;
    public static (int Cols, int Rows)? TerminalDims { get; set; }
    public static List<byte[]> TerminalReplay { get; } = [];
    public static List<string> TerminalSubscribes { get; } = [];
    public static List<string> TerminalUnsubscribes { get; } = [];
    public static List<(string AgentId, int Cols, int Rows)> Resizes { get; } = [];
    public static List<string> ResizeReleases { get; } = [];
    public static List<(string AgentId, string Text)> UserInputs { get; } = [];
    public static List<(string AgentId, string Key)> SpecialKeys { get; } = [];

    /// A stream event, the way the server's gateway delivers one.
    public Task PushStreamEventAsync(StreamEventEnvelope envelope) => BroadcastAsync(SignalRSubscriptionMethods.StreamEvent, envelope);
```

and these hub methods inside `SessionsHub`:

```csharp
        public Task SubscribeToStream(string stream, ulong? fromPosition) {
            if (!StreamHandler(stream)) throw new HubException(WireTokens.StreamNotAuthorized);
            StreamSubscribes.Add((stream, fromPosition));
            return Task.CompletedTask;
        }

        public Task UnsubscribeFromStream(string stream) { StreamUnsubscribes.Add(stream); return Task.CompletedTask; }

        // The server's terminal denial is silence, never a throw.
        public async Task SubscribeToTerminal(string agentId) {
            if (!TerminalHandler(agentId)) return;
            TerminalSubscribes.Add(agentId);
            if (TerminalDims is { } dims) await Clients.Caller.SendAsync(HubBroadcasts.TerminalDimensions, agentId, dims.Cols, dims.Rows);
            foreach (var chunk in TerminalReplay)
                await Clients.Caller.SendAsync(HubBroadcasts.TerminalOutput, agentId, Convert.ToBase64String(chunk));
        }

        public Task UnsubscribeFromTerminal(string agentId) { TerminalUnsubscribes.Add(agentId); return Task.CompletedTask; }
        public Task RequestResizeTerminal(string agentId, int cols, int rows) { Resizes.Add((agentId, cols, rows)); return Task.CompletedTask; }
        public Task ReleaseResizeTerminal(string agentId) { ResizeReleases.Add(agentId); return Task.CompletedTask; }
        public Task SendUserInput(string agentId, string text, string[]? attachmentIds) { UserInputs.Add((agentId, text)); return Task.CompletedTask; }
        public Task SendSpecialKey(string agentId, string key) { SpecialKeys.Add((agentId, key)); return Task.CompletedTask; }
```

In `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`, add `using System.Runtime.CompilerServices;`, `using System.Threading.Channels;`, `using Eventuous.SignalR;`, make the existing `Append` helper generic — `static void Append<T>(ref ImmutableList<T> list, T item) => ImmutableInterlocked.Update(ref list, static (l, i) => l.Add(i), item);` — and add:

```csharp
    public readonly Subject<TerminalOutputFrame> TerminalOutputSubject = new();
    public readonly Subject<TerminalSize> TerminalDimensionsSubject = new();
    /// Returns the exception a tail throws at its first MoveNextAsync, or null to tail normally.
    public Func<string, ulong?, Exception?> TailHandler = (_, _) => null;
    public Func<string, Task<HubCallOutcome>> TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> UserInputHandler = _ => Task.FromResult(HubCallOutcome.Ok);
    public Func<string, Task<HubCallOutcome>> SpecialKeyHandler = _ => Task.FromResult(HubCallOutcome.Ok);

    readonly Dictionary<string, Channel<StreamEventEnvelope>> _tailChannels = new(StringComparer.Ordinal);
    readonly Lock _tailLock = new();
    ImmutableList<(string Stream, ulong? From)> _tails = [];
    ImmutableList<string> _terminalSubscribes = [], _terminalUnsubscribes = [], _resizeReleases = [];
    ImmutableList<(string AgentId, int Cols, int Rows)> _resizes = [];
    ImmutableList<(string AgentId, string Text)> _userInputs = [];
    ImmutableList<(string AgentId, string Key)> _specialKeys = [];

    public ImmutableList<(string Stream, ulong? From)> Tails => Volatile.Read(ref _tails);
    public ImmutableList<string> TerminalSubscribes => Volatile.Read(ref _terminalSubscribes);
    public ImmutableList<string> TerminalUnsubscribes => Volatile.Read(ref _terminalUnsubscribes);
    public ImmutableList<(string AgentId, int Cols, int Rows)> Resizes => Volatile.Read(ref _resizes);
    public ImmutableList<string> ResizeReleases => Volatile.Read(ref _resizeReleases);
    public ImmutableList<(string AgentId, string Text)> UserInputs => Volatile.Read(ref _userInputs);
    public ImmutableList<(string AgentId, string Key)> SpecialKeys => Volatile.Read(ref _specialKeys);

    public IObservable<TerminalOutputFrame> TerminalOutput => TerminalOutputSubject;
    public IObservable<TerminalSize> TerminalDimensions => TerminalDimensionsSubject;

    public async IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, [EnumeratorCancellation] CancellationToken ct) {
        Append(ref _tails, (stream, fromPosition));
        Append(ref _calls, $"tail:{stream}@{fromPosition?.ToString() ?? "start"}");
        if (TailHandler(stream, fromPosition) is { } error) throw error;
        var channel = Channel.CreateUnbounded<StreamEventEnvelope>();
        lock (_tailLock) _tailChannels[stream] = channel;
        await foreach (var envelope in channel.Reader.ReadAllAsync(ct)) yield return envelope;
    }

    /// Delivers an event to the live tail of its stream; a stream nobody tails buffers nothing.
    public void PushStreamEvent(StreamEventEnvelope envelope) {
        Channel<StreamEventEnvelope>? channel;
        lock (_tailLock) _tailChannels.TryGetValue(envelope.Stream, out channel);
        channel?.Writer.TryWrite(envelope);
    }

    /// Ends the live tail the way a dropped connection does: the enumeration completes.
    public void CloseTail(string stream) {
        Channel<StreamEventEnvelope>? channel;
        lock (_tailLock) _tailChannels.Remove(stream, out channel);
        channel?.Writer.TryComplete();
    }

    public Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct) {
        Append(ref _calls, $"terminal:{agentId}");
        Append(ref _terminalSubscribes, agentId);
        return TerminalSubscribeHandler(agentId);
    }

    public Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct) {
        Append(ref _calls, $"unterminal:{agentId}");
        Append(ref _terminalUnsubscribes, agentId);
        return Task.FromResult(HubCallOutcome.Ok);
    }

    public Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct) {
        Append(ref _calls, $"resize:{agentId}:{cols}x{rows}");
        Append(ref _resizes, (agentId, cols, rows));
        return Task.FromResult(HubCallOutcome.Ok);
    }

    public Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct) {
        Append(ref _calls, $"release:{agentId}");
        Append(ref _resizeReleases, agentId);
        return Task.FromResult(HubCallOutcome.Ok);
    }

    public Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct) {
        Append(ref _calls, $"input:{agentId}");
        Append(ref _userInputs, (agentId, text));
        return UserInputHandler(agentId);
    }

    public Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct) {
        Append(ref _calls, $"key:{agentId}:{key}");
        Append(ref _specialKeys, (agentId, key));
        return SpecialKeyHandler(agentId);
    }
```

- [ ] **Step 4: Implement the lane**

`src/Capacitor.App/Services/TerminalOutputFrame.cs`:

```csharp
namespace Capacitor.App.Services;

/// One TerminalOutput push: the agent's bytes, base64 as the wire carries them.
public sealed record TerminalOutputFrame(string AgentId, string Base64);
```

`src/Capacitor.App/Services/TerminalSize.cs`:

```csharp
namespace Capacitor.App.Services;

/// The source PTY's size, as TerminalDimensions reports it to a viewer.
public sealed record TerminalSize(string AgentId, int Cols, int Rows);
```

In `src/Capacitor.App/Services/IServerLane.cs`, add `using Eventuous.SignalR;` and these members to the interface:

```csharp
    IObservable<TerminalOutputFrame> TerminalOutput { get; }
    IObservable<TerminalSize> TerminalDimensions { get; }
    /// A live tail from the position after `fromPosition` (null: the start). Completes when the
    /// connection closes or the stream is subscribed again; throws HubException on denial at the
    /// first MoveNextAsync; yields nothing while the lane has no live hub.
    IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, CancellationToken ct);
    Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct);
    Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct);
    Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct);
    Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct);
```

In `src/Capacitor.App/Services/ServerConnectionService.cs`, add `using Eventuous.SignalR;` and `using Eventuous.SignalR.Client;`, then:

Fields (beside the other subjects):

```csharp
    readonly Subject<TerminalOutputFrame> _terminalOutput = new();
    readonly Subject<TerminalSize> _terminalDimensions = new();
    // The subscription client of the live hub, replaced with it: it registers the StreamEvent
    // handler on the hub it wraps, so one per connection generation.
    volatile SignalRSubscriptionClient? _streams;

    public IObservable<TerminalOutputFrame> TerminalOutput => _terminalOutput.AsObservable();
    public IObservable<TerminalSize> TerminalDimensions => _terminalDimensions.AsObservable();
```

In `Build()`, after the `SessionAccessChanged` registration:

```csharp
        hub.On<string, string>(HubBroadcasts.TerminalOutput, (agentId, base64) => _terminalOutput.OnNext(new(agentId, base64)));
        hub.On<string, int, int>(HubBroadcasts.TerminalDimensions, (agentId, cols, rows) => _terminalDimensions.OnNext(new(agentId, cols, rows)));
```

In `RunAsync`, declare `SignalRSubscriptionClient? streams = null;` beside `HubConnection? hub = null;`. Right after `var capturedHub = hub;` add `streams = new SignalRSubscriptionClient(hub);`. Right after `_hub = hub;` add `_streams = streams;`. In the `finally`, before `_hub = null;`:

```csharp
                _streams = null;
                // Its dispose unsubscribes over a hub that is closing or closed; that call cannot
                // matter and must not turn a loop exit into a fault.
                if (streams is not null) {
                    try { await streams.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
                }
```

The new invokes, next to the existing ones:

```csharp
    public IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, CancellationToken ct) =>
        _streams is { } streams && _hub is { State: HubConnectionState.Connected }
            ? streams.SubscribeAsync(stream, fromPosition, ct)
            : AsyncEnumerable.Empty<StreamEventEnvelope>();

    public Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.SubscribeToTerminal, ct, agentId);
    public Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.UnsubscribeFromTerminal, ct, agentId);
    public Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct) => InvokeAsync(HubMethods.RequestResizeTerminal, ct, agentId, cols, rows);
    public Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct) => InvokeAsync(HubMethods.ReleaseResizeTerminal, ct, agentId);
    // Three arguments always: the method's arity is frozen and SignalR does not backfill a
    // missing trailing one.
    public Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct) => InvokeAsync(HubMethods.SendUserInput, ct, agentId, text, null);
    public Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct) => InvokeAsync(HubMethods.SendSpecialKey, ct, agentId, key);
```

In `NoServerLane` (`src/Capacitor.App/Services/NoRemoteAgents.cs`), add `using Eventuous.SignalR;` and:

```csharp
    public IObservable<TerminalOutputFrame> TerminalOutput => Observable.Never<TerminalOutputFrame>();
    public IObservable<TerminalSize> TerminalDimensions => Observable.Never<TerminalSize>();
    public IAsyncEnumerable<StreamEventEnvelope> TailStreamAsync(string stream, ulong? fromPosition, CancellationToken ct) =>
        AsyncEnumerable.Empty<StreamEventEnvelope>();
    public Task<HubCallOutcome> SubscribeToTerminalAsync(string agentId, CancellationToken ct) => Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> UnsubscribeFromTerminalAsync(string agentId, CancellationToken ct) => Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> RequestResizeTerminalAsync(string agentId, int cols, int rows, CancellationToken ct) => Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> ReleaseResizeTerminalAsync(string agentId, CancellationToken ct) => Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> SendUserInputAsync(string agentId, string text, CancellationToken ct) => Task.FromResult(HubCallOutcome.NotConnected);
    public Task<HubCallOutcome> SendSpecialKeyAsync(string agentId, string key, CancellationToken ct) => Task.FromResult(HubCallOutcome.NotConnected);
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerConnectionServiceTests/*"`
Expected: all PASS (the two new tests and every existing one).

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/FakeServerLaneTests/*"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Services/TerminalOutputFrame.cs src/Capacitor.App/Services/TerminalSize.cs src/Capacitor.App/Services/IServerLane.cs src/Capacitor.App/Services/ServerConnectionService.cs src/Capacitor.App/Services/NoRemoteAgents.cs test/Capacitor.App.Tests.Unit/FakeServerLane.cs test/Capacitor.App.Tests.Unit/HubTestHost.cs test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs
git commit -m "Tail a stream, carry the terminal and send input over the server lane (#806)"
```

---

### Task 5: `RemoteTranscriptFeed` — seed, then tail from where the seed ended

**Files:**
- Create: `src/Capacitor.App/ViewModels/RemoteTranscriptFeed.cs`
- Create: `test/Capacitor.App.Tests.Unit/RemoteFixtures.cs`
- Test: `test/Capacitor.App.Tests.Unit/RemoteTranscriptFeedTests.cs`

**Interfaces:**
- Consumes: `IChatTranscriptFeed`, `FeedRead`, `ProjectedLine`, `FeedStatus` (Task 3); `IServerLane.TailStreamAsync` (Task 4); `SessionDetailReader`, `SessionDetailFetch`, `SessionAccessState` (existing); `CanonicalEventJson.TryParse`, `TranscriptChat.RulesFor`, `TranscriptChat.Project` (Task 2); `StreamNames.AgentSession`, `WireTokens.StreamNotAuthorized` (Task 1).
- Produces: `internal sealed class RemoteTranscriptFeed(string sessionId, string vendor, IObservable<SessionAccessState> access, SessionDetailReader readDetail, IServerLane lane, TimeProvider time, Action<string> log) : IChatTranscriptFeed`, with `internal static readonly TimeSpan[] Retry`, `internal Task? PendingRunForTesting`, `internal bool WaitingToRetryForTesting`. Test fixtures `RemoteFixtures.Detail(params SessionEventDto[])`, `RemoteFixtures.Event(long number, string type, string payloadJson)`, `RemoteFixtures.Envelope(string sessionId, ulong position, string type, string payloadJson)`.

- [ ] **Step 1: Write the fixtures and the failing tests**

`test/Capacitor.App.Tests.Unit/RemoteFixtures.cs`:

```csharp
using System.Text.Json;
using Capacitor.Remote.Models;
using Eventuous.SignalR;

namespace Capacitor.App.Tests.Unit;

/// Server-shaped session events for the remote chat: a detail document and a live envelope.
static class RemoteFixtures {
    public static SessionDetailDto Detail(params SessionEventDto[] events) => new() {
        SessionId = "s1", LastEventNumber = events.Length == 0 ? -1 : events[^1].EventNumber, Events = events,
    };

    public static SessionEventDto Event(long number, string type, string payloadJson) => new() {
        EventType = type, EventNumber = number, Timestamp = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse(payloadJson).RootElement.Clone(),
    };

    public static StreamEventEnvelope Envelope(string sessionId, ulong position, string type, string payloadJson) => new() {
        EventId = Guid.NewGuid(), Stream = StreamNames.AgentSession(sessionId), EventType = type,
        StreamPosition = position, GlobalPosition = position, Timestamp = DateTime.UtcNow, JsonPayload = payloadJson,
    };

    public const string Hello = """{"content":"hello"}""";
    public const string HiThere = """{"content":"Hi there"}""";
    public const string LsCall = """{"tool_calls":[{"call_id":"t1","tool_name":"Bash","arguments":{"command":"ls"}}]}""";
}
```

`test/Capacitor.App.Tests.Unit/RemoteTranscriptFeedTests.cs`:

```csharp
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The remote feed: one seed from the detail route, a tail from the seed's last event number,
/// resumption from the last position on re-establishment, and the two refusals.
public class RemoteTranscriptFeedTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly FakeTimeProvider Time = new();
        public readonly List<string> Logged = [];
        public SessionDetailFetch NextDetail = new(Detail(
            Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
            Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
        public int DetailReads;
        public readonly RemoteTranscriptFeed Feed;

        public Harness(string vendor = "gemini") =>
            Feed = new RemoteTranscriptFeed("s1", vendor, Access, (_, _) => { DetailReads++; return Task.FromResult(NextDetail); }, Lane, Time, Logged.Add);

        public string Stream => StreamNames.AgentSession("s1");
        public void Dispose() => Feed.Dispose();
    }

    [Test]
    public async Task Seeds_from_the_detail_route_then_tails_from_the_last_event_number() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        await Assert.That(h.Lane.Tails[0]).IsEqualTo((h.Stream, (ulong?)1));

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 0, 1 });
        await Assert.That(seed.Lines[0].Projection.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);
        await Assert.That(h.Feed.CurrentOffset).IsEqualTo(2);

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");
        var live = h.Feed.ReadAppended();
        await Assert.That(live.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(live.Lines.Single().Offset).IsEqualTo(2);
        await Assert.That(live.Lines.Single().Projection.Envelopes[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(h.DetailReads).IsEqualTo(1);
        await Assert.That(h.Feed.ReadAppended().Lines).IsEmpty();
    }

    [Test]
    public async Task An_event_the_chat_does_not_render_moves_the_position_without_a_row() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, "InterruptIssued", """{"request_id":"r1"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");
        await Assert.That(h.Feed.ReadAppended().Lines).IsEmpty();
    }

    [Test]
    public async Task A_session_the_server_hides_reads_as_missing() {
        using var h = new Harness { NextDetail = new(null, NotFound: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
        await Assert.That(h.Lane.Tails).IsEmpty();
    }

    [Test]
    public async Task A_denied_stream_reads_as_failed_and_is_not_retried() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("not authorized");
        await Assert.That(h.Feed.WaitingToRetryForTesting).IsFalse();
    }

    [Test]
    public async Task A_re_established_access_resumes_the_tail_from_its_position_without_a_second_seed() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the first tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantTextGenerated, """{"content":"more"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        h.Access.OnNext(SessionAccessState.Unavailable);
        h.Access.OnNext(SessionAccessState.Establishing);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "the second tail");
        await Assert.That(h.Lane.Tails[1].From).IsEqualTo((ulong?)2);
        await Assert.That(h.DetailReads).IsEqualTo(1);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    [Test]
    public async Task A_tail_that_ends_while_access_stands_resumes_after_the_retry_gap() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the first tail");
        h.Lane.CloseTail(h.Stream);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the retry armed");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "the second tail");
        await Assert.That(h.Lane.Tails[1].From).IsEqualTo((ulong?)1);
    }

    [Test]
    public async Task A_lost_access_stops_the_tail_and_a_disposed_feed_tails_nothing_more() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Access.OnNext(SessionAccessState.Denied);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run stopped");
        h.Feed.Dispose();
        h.Access.OnNext(SessionAccessState.Established);
        await Task.Delay(50);
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteTranscriptFeedTests/*"`
Expected: compile error — `RemoteTranscriptFeed` not defined.

- [ ] **Step 3: Implement the feed**

`src/Capacitor.App/ViewModels/RemoteTranscriptFeed.cs`:

```csharp
using System.Collections.Concurrent;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.App.ViewModels;

/// The chat rows of a session on another machine: a seed from the session detail route, then a
/// live tail of the session's stream from the position the seed ended at. Every access
/// establishment restarts the tail from the last position seen; the seed is fetched only until
/// one lands, so a reconnect resumes rather than replays. Rows queue here and the pane's poll
/// drains them.
internal sealed class RemoteTranscriptFeed : IChatTranscriptFeed {
    /// Gaps between tails that ended while access still stood; each further one is the next entry.
    internal static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    readonly string _sessionId;
    readonly IChatDisplayRules? _rules;
    readonly SessionDetailReader _readDetail;
    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Action<string> _log;
    readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    readonly List<ProjectedLine> _pending = new();
    readonly IDisposable _access;
    readonly CancellationTokenSource _lifetime = new();
    FeedStatus _pendingStatus = FeedStatus.Ok;
    string? _failure;
    /// The last event number applied; null until the seed lands.
    long? _position;
    int _attempt;
    CancellationTokenSource? _tailCts;
    volatile bool _waitingToRetry;

    internal Task? PendingRunForTesting { get; private set; }
    internal bool WaitingToRetryForTesting => _waitingToRetry;

    public RemoteTranscriptFeed(
            string sessionId, string vendor, IObservable<SessionAccessState> access, SessionDetailReader readDetail,
            IServerLane lane, TimeProvider time, Action<string> log) {
        _sessionId = sessionId;
        _rules = TranscriptChat.RulesFor(vendor);
        _readDetail = readDetail;
        _lane = lane;
        _time = time;
        _log = log;
        _access = access.Subscribe(state => {
            if (state == SessionAccessState.Established) Restart();
            else StopTail();
        });
    }

    public long? CurrentOffset {
        get { lock (_lock) return _position is { } p ? p + 1 : null; }
    }

    public FeedRead ReadAppended() {
        lock (_lock) {
            var status = _pendingStatus;
            var failure = _failure;
            var lines = _pending.Count == 0 ? [] : _pending.ToArray();
            _pending.Clear();
            _pendingStatus = FeedStatus.Ok;
            _failure = null;
            return new(status, lines, status == FeedStatus.Reset ? CurrentOffsetLocked() : null, failure);
        }
    }

    long? CurrentOffsetLocked() => _position is { } p ? p + 1 : null;

    void Restart() {
        int attempt;
        CancellationTokenSource cts;
        lock (_lock) {
            if (_lifetime.IsCancellationRequested) return;
            _tailCts?.Cancel();
            _tailCts = cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            attempt = ++_attempt;
        }
        PendingRunForTesting = Task.Run(() => RunAsync(attempt, cts.Token));
    }

    void StopTail() {
        lock (_lock) {
            _tailCts?.Cancel();
            _tailCts = null;
            _attempt++;
        }
    }

    bool IsCurrent(int attempt) { lock (_lock) return _attempt == attempt; }

    async Task RunAsync(int attempt, CancellationToken ct) {
        var failures = 0;
        while (!ct.IsCancellationRequested && IsCurrent(attempt)) {
            try {
                if (!await EnsureSeededAsync(attempt, ct).ConfigureAwait(false)) return;
                await TailAsync(attempt, ct).ConfigureAwait(false);
                failures = 0;
            } catch (OperationCanceledException) {
                return;
            } catch (HubException ex) when (ex.Message.Contains(WireTokens.StreamNotAuthorized, StringComparison.Ordinal)) {
                Enqueue(FeedStatus.Failed, [], "not authorized to read this session's stream");
                return;
            } catch (Exception ex) {
                LogOnce($"remote transcript: {ex.Message}");
            }
            // The tail ended without a cancel: the connection dropped, or a newer subscribe on the
            // same stream replaced it. A superseded attempt stops; the current one waits and resumes.
            if (!IsCurrent(attempt)) return;
            var delay = Retry[Math.Min(failures++, Retry.Length - 1)];
            _waitingToRetry = true;
            try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            finally { _waitingToRetry = false; }
        }
    }

    /// True once a seed is in place. A hidden session is Missing and stops the run; an
    /// unauthorized read leaves the pane waiting for the sign-in the host asks for.
    async Task<bool> EnsureSeededAsync(int attempt, CancellationToken ct) {
        lock (_lock) if (_position is not null) return true;
        var fetch = await _readDetail(_sessionId, ct).ConfigureAwait(false);
        if (!IsCurrent(attempt)) return false;
        if (fetch.NotFound) { Enqueue(FeedStatus.Missing, []); return false; }
        if (fetch.Unauthorized) return false;
        if (fetch.Detail is not { } detail) throw new InvalidOperationException("session detail unavailable");

        var lines = new List<ProjectedLine>();
        foreach (var evt in detail.Events ?? [])
            if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.EventNumber) is { } line) lines.Add(line);
        lock (_lock) {
            if (_attempt != attempt) return false;
            _position = detail.LastEventNumber;
            _pending.Clear();
            _pending.AddRange(lines);
            _pendingStatus = FeedStatus.Reset;
            _failure = null;
        }
        return true;
    }

    async Task TailAsync(int attempt, CancellationToken ct) {
        ulong? from;
        lock (_lock) from = _position is >= 0 ? (ulong)_position : null;
        await foreach (var envelope in _lane.TailStreamAsync(StreamNames.AgentSession(_sessionId), from, ct).ConfigureAwait(false)) {
            var line = Project(envelope.EventType, envelope.JsonPayload, (long)envelope.StreamPosition);
            lock (_lock) {
                if (_attempt != attempt) return;
                _position = (long)envelope.StreamPosition;
                if (line is { } l) _pending.Add(l);
            }
        }
    }

    ProjectedLine? Project(string eventType, string json, long offset) {
        var payload = CanonicalEventJson.TryParse(eventType, json);
        if (payload is null) return null;
        var projected = TranscriptChat.Project(new CanonicalEvent(eventType, payload, Guid.Empty, _time.GetUtcNow()), _rules);
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 ? null : new(projected, offset);
    }

    void Enqueue(FeedStatus status, IReadOnlyList<ProjectedLine> lines, string? failure = null) {
        lock (_lock) {
            _pendingStatus = status;
            _failure = failure;
            _pending.AddRange(lines);
        }
    }

    void LogOnce(string reason) {
        if (_logged.TryAdd(reason, 0)) _log(reason);
    }

    public void Dispose() {
        _access.Dispose();
        StopTail();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteTranscriptFeedTests/*"`
Expected: all seven PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/RemoteTranscriptFeed.cs test/Capacitor.App.Tests.Unit/RemoteFixtures.cs test/Capacitor.App.Tests.Unit/RemoteTranscriptFeedTests.cs
git commit -m "Seed a remote chat from the session detail and tail its stream (#806)"
```

---

### Task 6: `ServerChatInput` — the composer sends over the hub

**Files:**
- Create: `src/Capacitor.App/ViewModels/ServerChatInput.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerChatInputTests.cs`

**Interfaces:**
- Consumes: `ChatInput`, `SendAvailability`, `ChatSendOutcome` (existing); `ChatSessionInfo` (Task 3); `IServerLane.SendUserInputAsync`, `SendSpecialKeyAsync` (Task 4); `SessionAccessState`, `ServerLaneStatus`.
- Produces: `internal sealed class ServerChatInput(string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<ChatSessionInfo> session, bool hasTerminal) : ChatInput`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/ServerChatInputTests.cs`:

```csharp
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The hub composer channel: ready only with a live lane, an established session and a running
/// agent; accepted when the hub takes the send; the transcript's echo is what confirms delivery.
[NotInParallel(nameof(AvaloniaSession))]
public class ServerChatInputTests {
    sealed class Harness {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly BehaviorSubject<ChatSessionInfo> Session = new(Info("Running"));
        public readonly ServerChatInput Input;

        public Harness(bool hasTerminal = false) => Input = new ServerChatInput("a1", Lane, Access, Session, hasTerminal);

        public static ChatSessionInfo Info(string status, bool ended = false) =>
            new(status, status, "gemini", null, null, null, ended, "", "s1");

        public void Ready() {
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            Access.OnNext(SessionAccessState.Established);
        }
    }

    [Test]
    public async Task Ready_needs_a_live_lane_an_established_session_and_a_running_agent() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            h.Access.OnNext(SessionAccessState.Established);
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Ready);
            await Assert.That(h.Input.CanAcceptText).IsTrue();
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Session.OnNext(Harness.Info("Starting"));
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Connecting);
            h.Session.OnNext(Harness.Info("Completed", ended: true));
            await Assert.That(h.Input.Availability).IsEqualTo(SendAvailability.Ended);
            await Assert.That(h.Input.Hint).IsEqualTo("This session has ended");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_the_hub_takes_is_accepted_and_a_refused_lane_leaves_it_unconfirmed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Ready();
            await Assert.That(await h.Input.SendAsync("fix it", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Accepted);
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "fix it"));
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            await Assert.That(await h.Input.SendAsync("again", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Unconfirmed);
            await Assert.That(h.Input.Hint).Contains("delivery unconfirmed");
            h.Input.ConfirmLastSend();
            await Assert.That(h.Input.Hint).IsEqualTo("Enter sends · Shift+Enter for a new line");

            h.Lane.UserInputHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            await Assert.That(await h.Input.SendAsync("nope", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Input.Hint).IsEqualTo("you cannot message this session");
            h.Input.Dispose();
        });
    }

    [Test]
    public async Task Interrupt_sends_escape_only_for_a_terminal_harness() {
        await RunOnUiAsync(async () => {
            var pty = new Harness(hasTerminal: true);
            pty.Ready();
            await Assert.That(pty.Input.CanInterrupt).IsTrue();
            await pty.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(pty.Lane.SpecialKeys).Contains(("a1", SpecialKeys.Escape));
            pty.Input.Dispose();

            var frame = new Harness(hasTerminal: false);
            frame.Ready();
            await Assert.That(frame.Input.CanInterrupt).IsFalse();
            await frame.Input.InterruptAsync(CancellationToken.None);
            await Assert.That(frame.Lane.SpecialKeys).IsEmpty();
            frame.Input.Dispose();
        });
    }

    [Test]
    public async Task A_send_while_not_ready_is_rejected_before_it_reaches_the_lane() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(await h.Input.SendAsync("early", CancellationToken.None)).IsEqualTo(ChatSendOutcome.Rejected);
            await Assert.That(h.Lane.UserInputs).IsEmpty();
            h.Input.Dispose();
        });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerChatInputTests/*"`
Expected: compile error — `ServerChatInput` not defined.

- [ ] **Step 3: Implement**

`src/Capacitor.App/ViewModels/ServerChatInput.cs`:

```csharp
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer channel for a session on another machine: one hub call per send, accepted when
/// the hub takes it. A refused send answers nothing on the wire, so acceptance is the hub's and
/// delivery is the transcript's — the same unconfirmed-until-echoed rule as the local frame.
internal sealed class ServerChatInput : ChatInput {
    const string Unconfirmed = "delivery unconfirmed — check the chat before sending again";

    readonly string _agentId;
    readonly IServerLane _lane;
    readonly bool _hasTerminal;
    readonly CompositeDisposable _subscriptions = new();
    ServerLaneState _laneState = ServerLaneState.Dormant;
    SessionAccessState _access = SessionAccessState.Establishing;
    ChatSessionInfo? _session;
    bool _sending;
    bool _disposed;
    string? _notice;

    public ServerChatInput(string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<ChatSessionInfo> session, bool hasTerminal) {
        _agentId = agentId;
        _lane = lane;
        _hasTerminal = hasTerminal;
        _subscriptions.Add(lane.Status.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => Apply(() => _laneState = s.State)));
        _subscriptions.Add(access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(a => Apply(() => _access = a)));
        _subscriptions.Add(session.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(i => Apply(() => _session = i)));
    }

    void Apply(Action update) {
        var before = Availability;
        update();
        if (Availability != before) _notice = null;
        Raise();
    }

    public override SendAvailability Availability =>
        _disposed || _session?.Ended == true ? SendAvailability.Ended
        : _laneState != ServerLaneState.Connected || _access != SessionAccessState.Established ? SendAvailability.Connecting
        : _sending ? SendAvailability.Sending
        : _session?.Status == "Running" ? SendAvailability.Ready
        : SendAvailability.Connecting;

    public override bool CanAcceptText => Availability == SendAvailability.Ready;

    /// Escape is a PTY keystroke; a frame-driven harness has nothing it would reach.
    public override bool CanInterrupt => _hasTerminal && CanAcceptText;

    public override string Hint => _notice ?? Availability switch {
        SendAvailability.Ready   => "Enter sends · Shift+Enter for a new line",
        SendAvailability.Sending => "Sending…",
        SendAvailability.Ended   => "This session has ended",
        _                        => "Connecting to the session…",
    };

    public override async Task<ChatSendOutcome> SendAsync(string text, CancellationToken ct) {
        if (_disposed || !CanAcceptText || ct.IsCancellationRequested) return ChatSendOutcome.Rejected;
        _sending = true; _notice = null; Raise();
        HubCallOutcome outcome;
        try {
            outcome = await _lane.SendUserInputAsync(_agentId, text, ct);
        } catch (OperationCanceledException) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        } catch (Exception) {
            return Settle(ChatSendOutcome.Unconfirmed, Unconfirmed);
        }
        return outcome.Result switch {
            HubCallResult.Ok     => Settle(ChatSendOutcome.Accepted, null),
            HubCallResult.Denied => Settle(ChatSendOutcome.Rejected, "you cannot message this session"),
            _                    => Settle(ChatSendOutcome.Unconfirmed, Unconfirmed),
        };
    }

    public override async Task InterruptAsync(CancellationToken ct) {
        if (!CanInterrupt || ct.IsCancellationRequested) return;
        try { await _lane.SendSpecialKeyAsync(_agentId, SpecialKeys.Escape, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: composer interrupt failed: {ex.Message}"); }
    }

    ChatSendOutcome Settle(ChatSendOutcome outcome, string? notice) {
        if (_disposed) return outcome;
        _sending = false; _notice = notice; Raise();
        return outcome;
    }

    public override void ConfirmLastSend() {
        if (_disposed || _sending || _notice != Unconfirmed) return;
        _notice = null;
        Raise();
    }

    void Raise() {
        if (_disposed) return;
        this.RaisePropertyChanged(nameof(Availability));
        this.RaisePropertyChanged(nameof(CanAcceptText));
        this.RaisePropertyChanged(nameof(CanInterrupt));
        this.RaisePropertyChanged(nameof(Hint));
    }

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _subscriptions.Dispose();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerChatInputTests/*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ServerChatInput.cs test/Capacitor.App.Tests.Unit/ServerChatInputTests.cs
git commit -m "Send the composer's text to a remote agent over the hub (#806)"
```

---

### Task 7: The remote host grows a chat pane

The slice-2 card host becomes the remote workspace's shell: the chat pane (cards included, as in the local workspace) over the remote feed and the hub composer, a tab strip, and the composition root wiring. The terminal tab comes in Task 10.

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs` (full rewrite below)
- Modify: `src/Capacitor.App/Views/RemoteSessionView.axaml`, `src/Capacitor.App/Views/RemoteSessionView.axaml.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildRemote`)
- Modify: `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`, `RemoteSessionViewSmokeTests.cs`, `MainWindowViewModelTests.cs` (constructor calls)

**Interfaces:**
- Consumes: `ChatTabViewModel` primary ctor, `ChatSessionInfo.FromRemote` (Task 3); `RemoteTranscriptFeed` (Task 5); `ServerChatInput` (Task 6); `SessionDetailReader`, `SessionAccessService`, `IPermissionService`, `AgentActionService`, `IAgentDirectory`, `IUrlOpener` (existing).
- Produces: `RemoteSessionViewModel(AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions, AgentActionService actions, IServerLane lane, SessionDetailReader readDetail, IUrlOpener opener, TimeProvider time)` with `Chat : ChatTabViewModel`, `Cards => Chat.Cards`, `AccessStates : IObservable<SessionAccessState>`, `ActiveTab : RemoteTab`, `IsChatActive`, `ShowsPanes`, `ShowsChatPane`, `ShowsCards`, `ShowChatCommand`, `RaiseTabProjections()` (private; Task 10 extends it), `enum RemoteTab { Chat, Terminal }`.

- [ ] **Step 1: Update the harnesses and write the failing tests**

In `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`, add `using System.Reactive.Subjects;`, `using Capacitor.Remote.Models;`, `using static Capacitor.App.Tests.Unit.RemoteFixtures;`, and change the `Harness` to:

```csharp
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly SessionAccessService Access;
        public readonly FakePermissionService Permissions = new();
        public readonly FakeAgentDirectory Directory = new();
        public readonly AgentActionService Actions = NewActions();
        public readonly FakeTimeProvider Time = new();
        public SessionDetailFetch Detail = new(RemoteFixtures.Detail());

        public Harness() {
            Access = new SessionAccessService(Lane, Time);
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        }

        public static AgentRow Row(string id = "a1", string? sessionId = "s1", string status = "Running", string vendor = "gemini") =>
            AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = id, SessionId = sessionId, Status = status, DaemonName = "work-mac",
                Vendor = vendor, OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            });

        public RemoteSessionViewModel Build(AgentRow row) {
            Directory.Rows.AddOrUpdate(row);
            return new RemoteSessionViewModel(row, Directory, Access, Permissions, Actions, Lane, (_, _) => Task.FromResult(Detail), new RecordingOpener(), Time);
        }

        /// One chat poll: the pane reads its feed on the timer this harness owns.
        public async Task TickAsync(RemoteSessionViewModel vm) {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (vm.Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        /// Polls the condition, ticking the chat between checks, since rows land on the poll.
        public async Task UntilAsync(RemoteSessionViewModel vm, Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
                await TickAsync(vm);
                await Task.Delay(10);
            }
        }

        public void Dispose() {
            Access.Dispose();
            Permissions.Dispose();
            Directory.Dispose();
        }
    }
```

Every existing test in the file stays as it is. Append these:

```csharp
    [Test]
    public async Task The_chat_seeds_from_the_session_detail_and_follows_the_stream() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
                Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.Items.Count == 2, "the seeded rows");
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(vm.Chat.Items[0]).IsTypeOf<UserTurnItem>();
            await Assert.That(vm.Chat.Items[1]).IsTypeOf<AssistantTextItem>();

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
            await h.UntilAsync(vm, () => vm.Chat.Items.Count == 3, "the live row");
            await Assert.That(vm.Chat.Items[2]).IsTypeOf<ToolGroupItem>();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_sent_prompt_waits_in_the_queue_until_the_stream_echoes_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Reading, "the empty seed");
            await WaitUntilAsync(() => vm.Chat.ShowsComposer && vm.Chat.ComposerHint.StartsWith("Enter sends"), what: "the composer");

            vm.Chat.ComposerText = "do it";
            await vm.Chat.SendCommand.Execute();
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "do it"));
            await Assert.That(vm.Chat.QueuedMessages.Count).IsEqualTo(1);
            await Assert.That(vm.Chat.ComposerText).IsEqualTo("");

            h.Lane.PushStreamEvent(Envelope("s1", 0, CanonicalEventTypes.UserMessageReceived, """{"content":"do it"}"""));
            await h.UntilAsync(vm, () => vm.Chat.QueuedMessages.Count == 0, "the echo");
            await Assert.That(vm.Chat.Items.Single()).IsTypeOf<UserTurnItem>();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_hidden_transcript_reads_as_not_available_and_a_lost_lane_keeps_the_rows() {
        await RunOnUiAsync(async () => {
            using var h = new Harness { Detail = new(null, NotFound: true) };
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Missing, "missing");
            await Assert.That(vm.Chat.PhaseNote).IsEqualTo(RemoteSessionViewModel.MissingNote);

            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Offline, what: "offline");
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("Not connected to the server");
            await vm.TeardownAsync();
        });
    }
```

In `test/Capacitor.App.Tests.Unit/RemoteSessionViewSmokeTests.cs`, add `using Capacitor.Remote.Models;` and `using static Capacitor.App.Tests.Unit.RemoteFixtures;`; in `Host`, add `public readonly FakeTimeProvider Time = new();` and build the VM as:

```csharp
            _access = new SessionAccessService(Lane, Time);
            ...
            Vm = new RemoteSessionViewModel(row, _directory, _access, Permissions, NewActions(), Lane,
                (_, _) => Task.FromResult(new SessionDetailFetch(Detail())), new RecordingOpener(), Time);
```

Replace the two tests with:

```csharp
    [Test]
    public async Task An_acp_question_renders_in_the_chat_pane_through_the_shared_card_template() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(chat.IsVisible).IsTrue();

            var card = PendingPermissionRequest.FromServer(
                new ServerElicitationRequest("s1", "q1", "Pick a branch",
                    [new() { OptionId = "a", Label = "main" }, new() { OptionId = "b", Label = "next" }], false),
                DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            host.Permissions.Add(card);
            await host.SettleUntilAsync(() => host.Vm.Cards.HasPendingCards, "the card");

            await Assert.That(chat.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "AcpQuestionCard")).IsTrue();
            var labels = chat.GetVisualDescendants().OfType<Button>()
                .Where(b => b is not ToggleButton && b.Classes.Contains("acpOption"))
                .Select(b => b.Content as string ?? "")
                .ToList();
            await Assert.That(labels).IsEquivalentTo(new[] { "main", "next" });

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    /// Denied is a stated verdict, not an empty pane: the chat goes away and the banner says why.
    [Test]
    public async Task A_denied_session_replaces_the_chat_with_the_access_note() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");

            var banner = host.View.FindControl<Border>("AccessBanner")!;
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(chat.IsVisible).IsTrue();

            host.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            host.Lane.SessionAccessChangedSubject.OnNext("s1");
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Denied, "denied");

            await Assert.That(chat.IsVisible).IsFalse();
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(host.View.FindControl<TextBlock>("AccessNoteText")!.Text)
                .IsEqualTo("You no longer have access to this session");

            await host.Vm.TeardownAsync();
            return true;
        });
    }
```

In `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs`, `RemoteHost.New(string agentId, string? sessionId)` builds the VM as:

```csharp
            return new RemoteSessionViewModel(row, Directory, _access, _permissions, WorkspaceFixtures.NewActions(), _lane,
                (_, _) => Task.FromResult(new SessionDetailFetch(null)), new RecordingOpener(), new FakeTimeProvider());
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteSession*/*"`
Expected: compile error — the constructor has no such overload; `Chat`, `ShowsChatPane` not defined.

- [ ] **Step 3: Rewrite the view model**

`src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Media;
using Capacitor.App.Services;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteSessionAccess { Connecting, Ready, Denied, Offline, NoSession }
public enum RemoteTab { Chat, Terminal }

/// The workspace for a row the app has no socket to: the header, the chat pane over the server's
/// stream — its cards included — Stop and Open in web. Access is the server's explicit signal,
/// Ready only after the watch and the chat join both succeeded, so an empty pane is "no rows
/// yet", never "not allowed". An ended session keeps its transcript while the lease's last
/// verdict stands.
public sealed class RemoteSessionViewModel : ReactiveObject, ISessionWorkspace {
    internal const string OriginChangedNote = "This agent is now hosted locally — open it from the rail";
    internal const string MissingNote = "The transcript is not available";

    readonly SessionAccessService _access;
    readonly CompositeDisposable _disposables = new();
    readonly SerialDisposable _lease = new();
    // Never disposed, so a row revision landing after teardown cannot throw; the chat and its
    // cards unsubscribe from these when the pane is torn down.
    readonly BehaviorSubject<string?> _sessionIds;
    readonly BehaviorSubject<ChatSessionInfo> _session;
    /// The current lease's verdict, re-pointed whenever the lease moves; Unavailable in between.
    readonly BehaviorSubject<SessionAccessState> _accessStates = new(SessionAccessState.Unavailable);
    string? _leasedSession;
    AgentRow _row;

    public string AgentId { get; }
    public ChatTabViewModel Chat { get; }
    public PendingCardsViewModel Cards => Chat.Cards;
    public IObservable<SessionAccessState> AccessStates => _accessStates.AsObservable();
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowChatCommand { get; }

    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    string _repoLabelText = "";
    public string RepoLabelText { get => _repoLabelText; private set => this.RaiseAndSetIfChanged(ref _repoLabelText, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }

    RemoteTab _activeTab = RemoteTab.Chat;
    public RemoteTab ActiveTab {
        get => _activeTab;
        private set {
            this.RaiseAndSetIfChanged(ref _activeTab, value);
            RaiseTabProjections();
        }
    }
    public bool IsChatActive => ActiveTab == RemoteTab.Chat;

    // Stop's canExecute reads the ended flag here, not through this.WhenAnyValue: that call routes
    // through ReactiveUI's ObservableForProperty/RxAppBuilder global init, which only some other
    // code having built the app primes. Never disposed, so a set after teardown cannot throw.
    readonly BehaviorSubject<bool> _sessionEndedChanges = new(false);

    bool _sessionEnded;
    public bool SessionEnded {
        get => _sessionEnded;
        private set {
            if (_sessionEnded == value) return;
            this.RaiseAndSetIfChanged(ref _sessionEnded, value);
            this.RaisePropertyChanged(nameof(ShowsCards));
            _sessionEndedChanges.OnNext(value);
        }
    }

    // Same reason as _sessionEndedChanges above: Stop's canExecute reads the flag through a subject.
    readonly BehaviorSubject<bool> _originChangedChanges = new(false);
    public IObservable<bool> OriginChangedChanges => _originChangedChanges.AsObservable();

    bool _originChangedToLocal;
    /// The local daemon has proven this row is its twin: the remote row is gone but the agent is
    /// not, so the window replaces this host with the local workspace for the same id.
    public bool OriginChangedToLocal {
        get => _originChangedToLocal;
        private set {
            if (_originChangedToLocal == value) return;
            this.RaiseAndSetIfChanged(ref _originChangedToLocal, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            RaiseTabProjections();
            _originChangedChanges.OnNext(value);
        }
    }

    RemoteSessionAccess _accessState = RemoteSessionAccess.NoSession;
    public RemoteSessionAccess Access {
        get => _accessState;
        private set {
            this.RaiseAndSetIfChanged(ref _accessState, value);
            this.RaisePropertyChanged(nameof(AccessNote));
            RaiseTabProjections();
        }
    }

    /// The tabs' content lives while the lease's last verdict stands; an ended session keeps
    /// its transcript, an agent that moved to this machine shows the note instead.
    public bool ShowsPanes => Access == RemoteSessionAccess.Ready && !OriginChangedToLocal;
    public bool ShowsChatPane => ShowsPanes && IsChatActive;
    /// An ended session's cards are unanswerable — nobody is waiting on them any more.
    public bool ShowsCards => ShowsPanes && !SessionEnded;

    void RaiseTabProjections() {
        this.RaisePropertyChanged(nameof(IsChatActive));
        this.RaisePropertyChanged(nameof(ShowsPanes));
        this.RaisePropertyChanged(nameof(ShowsChatPane));
        this.RaisePropertyChanged(nameof(ShowsCards));
    }

    public string AccessNote => OriginChangedToLocal ? OriginChangedNote : Access switch {
        RemoteSessionAccess.Connecting => "Connecting to the session…",
        RemoteSessionAccess.Denied => "You no longer have access to this session",
        RemoteSessionAccess.Offline => "Not connected to the server",
        RemoteSessionAccess.NoSession => "Waiting for the session to start",
        _ => "",
    };

    public RemoteSessionViewModel(
            AgentRow row, IAgentDirectory directory, SessionAccessService access, IPermissionService permissions,
            AgentActionService actions, IServerLane lane, SessionDetailReader readDetail, IUrlOpener opener, TimeProvider time) {
        _row = row;
        _access = access;
        AgentId = row.Id;
        _sessionIds = new BehaviorSubject<string?>(row.SessionId);
        _session = new BehaviorSubject<ChatSessionInfo>(ChatSessionInfo.FromRemote(row, ended: false));

        var input = new ServerChatInput(row.Id, lane, _accessStates, _session, HostedHarnessCatalog.ShowsTerminal(null, row.Vendor));
        Chat = new ChatTabViewModel(
            row.Id, AgentOrigin.Remote, _session, Observable.Return<string[]?>(null), input,
            key => new RemoteTranscriptFeed(key, row.Vendor, _accessStates, readDetail, lane, time, Log),
            opener, time, permissions, missingNote: MissingNote, sessionId: _sessionIds);
        Apply(row);

        directory.Rows.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    if (change.Key != row.Key) continue;
                    if (change.Reason == ChangeReason.Remove) {
                        // The local daemon took the agent over: the row ended, the session did not.
                        // A shared agent id is not evidence of that on its own — the dedup fails
                        // open, so two unrelated agents can carry one id. The proof is the
                        // directory's: this agent runs on the daemon it proved is the local one's
                        // twin, and both rows carry the same session id. The directory publishes
                        // the local add and this removal in one edit, so that row is already in the
                        // cache here; a later one would read as an ended session.
                        // Never once a terminal status has ended this session: that verdict came
                        // from the authority that was winning, and the local row reappearing behind
                        // the retired remote one is retained history rather than a takeover.
                        if (!SessionEnded
                            && directory.IsProvenLocalTwin(row.Id)
                            && directory.Rows.Lookup($"local:{row.Id}") is { HasValue: true, Value: var twin }
                            && twin.SessionId is { Length: > 0 } && twin.SessionId == _row.SessionId) {
                            OriginChangedToLocal = true;
                            // Nothing is answerable through a released lease, and the agent is not
                            // this host's to stop any more.
                            Access = RemoteSessionAccess.NoSession;
                        } else {
                            SessionEnded = true;
                            PublishSession(ended: true);
                        }
                        Release();
                        continue;
                    }
                    Apply(change.Current);
                }
            })
            .DisposeWith(_disposables);

        OpenInWebCommand = ReactiveCommand.Create(() => actions.OpenInWebRemote(row.Id));
        var stopKey = AgentActionService.StopKey(AgentOrigin.Remote, row.Id);
        var canStop = _sessionEndedChanges
            .CombineLatest(_originChangedChanges, actions.StopsInFlight,
                (ended, movedLocal, inFlight) => !ended && !movedLocal && !inFlight.Contains(stopKey));
        StopCommand = ReactiveCommand.Create(
            () => actions.RequestStop(row.Id, $"{_row.Vendor} · {_row.RepoGroupLabel}", _row.Kind, AgentOrigin.Remote),
            canStop);
        ShowChatCommand = ReactiveCommand.Create(() => { ActiveTab = RemoteTab.Chat; });
        _disposables.Add(OpenInWebCommand);
        _disposables.Add(StopCommand);
        _disposables.Add(ShowChatCommand);
        _disposables.Add(_lease);
    }

    void Apply(AgentRow row) {
        _row = row;
        if (_sessionIds.Value != row.SessionId) _sessionIds.OnNext(row.SessionId);
        Title = row.Title ?? row.Vendor;
        RepoLabelText = $"{row.RepoGroupLabel} · on {row.MachineBadge}";
        StatusText = row.Status;
        StatusDot = SessionStatusDots.For(row.Status);
        if (SessionStatusDots.IsTerminal(row.Status)) {
            SessionEnded = true;
            PublishSession(ended: true);
            Release();
            return;
        }
        // The row can come back: a transient empty registry snapshot removes it and the refresh
        // that follows re-adds the same live session, which must not stay hidden behind the
        // removal's verdict. Release() cleared the leased id, so the lease is re-acquired below.
        SessionEnded = false;
        OriginChangedToLocal = false;
        PublishSession(ended: false);

        var sessionId = row.SessionId;
        if (sessionId == _leasedSession) return;
        Release();
        if (sessionId is null) { Access = RemoteSessionAccess.NoSession; return; }

        _leasedSession = sessionId;
        // The lease's first state arrives asynchronously; without this the pane keeps the previous
        // session's verdict until it does.
        Access = RemoteSessionAccess.Connecting;
        var lease = _access.Acquire(sessionId);
        var states = lease.State.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(s => {
            _accessStates.OnNext(s);
            Access = s switch {
                SessionAccessState.Established => RemoteSessionAccess.Ready,
                SessionAccessState.Denied => RemoteSessionAccess.Denied,
                SessionAccessState.Unavailable => RemoteSessionAccess.Offline,
                _ => RemoteSessionAccess.Connecting,
            };
        });
        _lease.Disposable = new CompositeDisposable(states, lease);
    }

    void PublishSession(bool ended) => _session.OnNext(ChatSessionInfo.FromRemote(_row, ended));

    void Release() {
        _leasedSession = null;
        _lease.Disposable = Disposable.Empty;
        _accessStates.OnNext(SessionAccessState.Unavailable);
    }

    static void Log(string note) => Console.Error.WriteLine($"kcap: remote chat: {note}");

    public async Task TeardownAsync() {
        _disposables.Dispose();
        await Chat.TeardownAsync();
    }
}
```

- [ ] **Step 4: Rewrite the view and its code-behind**

`src/Capacitor.App/Views/RemoteSessionView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             xmlns:views="clr-namespace:Capacitor.App.Views"
             x:Class="Capacitor.App.Views.RemoteSessionView"
             x:DataType="vm:RemoteSessionViewModel"
             Background="{StaticResource KcapCanvasBrush}">
    <UserControl.Styles>
        <Style Selector="Button.tab">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0,0,0,2" />
            <Setter Property="BorderBrush" Value="Transparent" />
            <Setter Property="CornerRadius" Value="0" />
            <Setter Property="Foreground" Value="{StaticResource KcapMutedBrush}" />
            <Setter Property="FontSize" Value="12.5" />
            <Setter Property="Padding" Value="3,9" />
            <Setter Property="Margin" Value="0,0,16,0" />
        </Style>
        <Style Selector="Button.tab.active">
            <Setter Property="BorderBrush" Value="{StaticResource KcapPurpleBrush}" />
            <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
            <Setter Property="FontWeight" Value="SemiBold" />
        </Style>
        <!-- Fluent hover paints PART_ContentPresenter; keep tab chrome on our tokens. -->
        <Style Selector="Button.tab:pointerover /template/ ContentPresenter#PART_ContentPresenter,
                         Button.tab:pressed /template/ ContentPresenter#PART_ContentPresenter">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderBrush" Value="Transparent" />
            <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
        </Style>
        <Style Selector="Button.tab.active:pointerover /template/ ContentPresenter#PART_ContentPresenter,
                         Button.tab.active:pressed /template/ ContentPresenter#PART_ContentPresenter">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderBrush" Value="{StaticResource KcapPurpleBrush}" />
            <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
        </Style>
    </UserControl.Styles>

    <!-- The session's process lives on another machine: the header, the tabs over the server's
         streams, and the two actions that reach across. -->
    <Grid RowDefinitions="56,42,Auto,*">
        <Grid Grid.Row="0" ColumnDefinitions="*,Auto,Auto" Margin="20,0,16,0">
            <StackPanel Grid.Column="0" Spacing="2" VerticalAlignment="Center" Margin="0,0,16,0">
                <TextBlock x:Name="RemoteTitle" Text="{Binding Title}" FontWeight="SemiBold" FontSize="14.5"
                           Foreground="{StaticResource KcapTextBrush}" TextTrimming="CharacterEllipsis" />
                <StackPanel Orientation="Horizontal" Spacing="7">
                    <Ellipse Width="7" Height="7" VerticalAlignment="Center" Fill="{Binding StatusDot}" />
                    <TextBlock x:Name="RemoteStatus" Text="{Binding StatusText}" FontSize="11.5"
                               Foreground="{StaticResource KcapMutedBrush}" />
                    <TextBlock x:Name="RemoteSubtitle" Text="{Binding RepoLabelText}" FontSize="11.5"
                               Foreground="{StaticResource KcapMutedBrush}" TextTrimming="CharacterEllipsis" />
                </StackPanel>
            </StackPanel>

            <Button Grid.Column="1" Content="Open in web" Command="{Binding OpenInWebCommand}"
                    Background="Transparent" BorderBrush="Transparent" CornerRadius="7"
                    Foreground="{StaticResource KcapMutedBrush}" Padding="10,5" FontSize="12.5" Margin="0,0,4,0" />
            <Button Grid.Column="2" Content="Stop" Command="{Binding StopCommand}"
                    Background="Transparent" BorderBrush="Transparent" CornerRadius="7"
                    Foreground="{StaticResource KcapDangerBrush}" Padding="10,5" FontSize="12.5" />
        </Grid>

        <Border Grid.Row="1" BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="0,1,0,1">
            <StackPanel Orientation="Horizontal" Spacing="6" Margin="20,0" HorizontalAlignment="Left" VerticalAlignment="Center">
                <Button x:Name="ChatTabButton" Content="Chat" Classes="tab" Classes.active="{Binding IsChatActive}"
                        Command="{Binding ShowChatCommand}" />
            </StackPanel>
        </Border>

        <Border x:Name="AccessBanner" Grid.Row="2" Margin="20,10,20,0"
                Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}"
                BorderThickness="1" CornerRadius="10" Padding="13,10"
                IsVisible="{Binding AccessNote, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
            <TextBlock x:Name="AccessNoteText" Text="{Binding AccessNote}" TextWrapping="Wrap"
                       FontSize="12.5" Foreground="{StaticResource KcapMutedBrush}" />
        </Border>

        <Grid Grid.Row="3">
            <!-- The active-tab flag lives on this VM, which is not the pane's DataContext. -->
            <views:ChatTabView x:Name="ChatHost" DataContext="{Binding Chat}"
                               IsVisible="{Binding $parent[views:RemoteSessionView].((vm:RemoteSessionViewModel)DataContext).ShowsChatPane}" />
        </Grid>
    </Grid>
</UserControl>
```

`src/Capacitor.App/Views/RemoteSessionView.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

/// The workspace for a session on another machine. DataContext is a RemoteSessionViewModel,
/// supplied by MainWindow's workspace slot; this view builds nothing of its own.
public partial class RemoteSessionView : UserControl {
    IDisposable? _tabFocus;

    public RemoteSessionView() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            var model = DataContext as RemoteSessionViewModel;
            _tabFocus = model?
                .WhenAnyValue(vm => vm.ActiveTab, vm => vm.ShowsPanes)
                .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(model, DataContext) || !pair.Item2) return;
                    if (pair.Item1 == RemoteTab.Chat) ChatHost.FocusComposer();
                }, DispatcherPriority.Loaded));
        };
    }
}
```

- [ ] **Step 5: Wire the composition root**

In `src/Capacitor.App/App.axaml.cs`, `BuildRemote` becomes:

```csharp
        RemoteSessionViewModel? BuildRemote(string agentId) =>
            directory.Rows.Lookup($"remote:{agentId}") is { HasValue: true, Value: var row }
                ? new RemoteSessionViewModel(row, directory, sessionAccess, permissions, actions, serverLane, readDetail, opener, TimeProvider.System)
                : null;
```

- [ ] **Step 6: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings (AVLN included).

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteSession*/*"`
Expected: every `RemoteSessionViewModelTests` and `RemoteSessionViewSmokeTests` test PASSES, the three new ones included.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindow*/*"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs src/Capacitor.App/Views/RemoteSessionView.axaml src/Capacitor.App/Views/RemoteSessionView.axaml.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewSmokeTests.cs test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs
git commit -m "Show a remote session's chat in the desktop app (#806)"
```

---

### Task 8: Special keys from keystrokes, and a source-sized surface

**Files:**
- Create: `src/Capacitor.App/Services/SpecialKeyMapper.cs`
- Modify: `src/Capacitor.App/Services/ITerminalSurface.cs`, `src/Capacitor.App/Services/XtermTerminalSurface.cs`
- Modify: `test/Capacitor.App.Tests.Unit/FakeTerminalSurface.cs`
- Test: `test/Capacitor.App.Tests.Unit/SpecialKeyMapperTests.cs`

**Interfaces:**
- Consumes: `SpecialKeys` (Remote.Models).
- Produces: `SpecialKeyMapper.Map(ReadOnlySpan<byte> bytes) : string?`; `record SpecialKeyChoice(string Key, string Label)` and `SpecialKeyMapper.Choices : IReadOnlyList<SpecialKeyChoice>`; `ITerminalSurface.Resize(int cols, int rows)`; `FakeTerminalSurface.Resizes : List<(int Cols, int Rows)>`.

- [ ] **Step 1: Write the failing test**

`test/Capacitor.App.Tests.Unit/SpecialKeyMapperTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// Only the daemon's seven keys cross the wire; every other keystroke stays local to a read-only pane.
public class SpecialKeyMapperTests {
    [Test]
    [Arguments(new byte[] { 0x1b }, "Escape")]
    [Arguments(new byte[] { 0x09 }, "Tab")]
    [Arguments(new byte[] { 0x0d }, "Enter")]
    [Arguments(new byte[] { 0x0a }, "Enter")]
    [Arguments(new byte[] { 0x03 }, "CtrlC")]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x41 }, "ArrowUp")]
    [Arguments(new byte[] { 0x1b, 0x4f, 0x41 }, "ArrowUp")]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x42 }, "ArrowDown")]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x5a }, "ShiftTab")]
    public async Task Each_wire_key_has_its_terminal_bytes(byte[] bytes, string key) =>
        await Assert.That(SpecialKeyMapper.Map(bytes)).IsEqualTo(key);

    [Test]
    [Arguments(new byte[] { 0x78 })]
    [Arguments(new byte[] { 0x1b, 0x5b })]
    [Arguments(new byte[] { 0x1b, 0x5b, 0x43 })]
    [Arguments(new byte[] { })]
    public async Task Anything_else_is_dropped(byte[] bytes) =>
        await Assert.That(SpecialKeyMapper.Map(bytes)).IsNull();

    [Test]
    public async Task The_choices_cover_the_vocabulary_once_each() {
        await Assert.That(SpecialKeyMapper.Choices.Select(c => c.Key)).IsEquivalentTo(SpecialKeys.All);
        await Assert.That(SpecialKeyMapper.Choices.Select(c => c.Label).Distinct().Count()).IsEqualTo(SpecialKeys.All.Length);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SpecialKeyMapperTests/*"`
Expected: compile error — `SpecialKeyMapper` not defined.

- [ ] **Step 3: Implement**

`src/Capacitor.App/Services/SpecialKeyMapper.cs`:

```csharp
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// One of the daemon's special keys as a button: the wire token and what the button says.
public sealed record SpecialKeyChoice(string Key, string Label);

/// Maps a keystroke's terminal bytes onto the daemon's special-key vocabulary. Only an exact
/// sequence matches; a read-only pane sends nothing else through.
public static class SpecialKeyMapper {
    public static readonly IReadOnlyList<SpecialKeyChoice> Choices = [
        new(SpecialKeys.Escape, "Esc"), new(SpecialKeys.Tab, "Tab"), new(SpecialKeys.ShiftTab, "Shift+Tab"),
        new(SpecialKeys.Enter, "Enter"), new(SpecialKeys.CtrlC, "Ctrl+C"),
        new(SpecialKeys.ArrowUp, "↑"), new(SpecialKeys.ArrowDown, "↓"),
    ];

    public static string? Map(ReadOnlySpan<byte> bytes) => bytes switch {
        [0x1b] => SpecialKeys.Escape,
        [0x09] => SpecialKeys.Tab,
        [0x0d] or [0x0a] => SpecialKeys.Enter,
        [0x03] => SpecialKeys.CtrlC,
        [0x1b, (byte)'[', (byte)'A'] or [0x1b, (byte)'O', (byte)'A'] => SpecialKeys.ArrowUp,
        [0x1b, (byte)'[', (byte)'B'] or [0x1b, (byte)'O', (byte)'B'] => SpecialKeys.ArrowDown,
        [0x1b, (byte)'[', (byte)'Z'] => SpecialKeys.ShiftTab,
        _ => null,
    };
}
```

In `src/Capacitor.App/Services/ITerminalSurface.cs`, add to the interface:

```csharp
    /// Sizes the pane to the source PTY — a remote viewer follows the source, it never drives it.
    void Resize(int cols, int rows);
```

In `src/Capacitor.App/Services/XtermTerminalSurface.cs`, add:

```csharp
    public void Resize(int cols, int rows) => Model.Terminal.Resize(cols, rows);
```

In `test/Capacitor.App.Tests.Unit/FakeTerminalSurface.cs`, add:

```csharp
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public void Resize(int cols, int rows) { Resizes.Add((cols, rows)); CurrentSize = (cols, rows); }
```

Check the resize call compiles against `SvcSystems.UI.Terminal`: `Model.Terminal.Resize(int, int)` is the call the local pane's own comment names; if the build reports it does not exist, open the package's `TerminalControlModel` with the `dotnet-skills:ilspy-decompile` skill and use the member that sets the terminal's columns and rows.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SpecialKeyMapperTests/*"`
Expected: all PASS.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/Terminal*/*"`
Expected: PASS (the fake grew a member; nothing else moved).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/SpecialKeyMapper.cs src/Capacitor.App/Services/ITerminalSurface.cs src/Capacitor.App/Services/XtermTerminalSurface.cs test/Capacitor.App.Tests.Unit/FakeTerminalSurface.cs test/Capacitor.App.Tests.Unit/SpecialKeyMapperTests.cs
git commit -m "Map keystrokes to the daemon's special keys and size a pane from its source (#806)"
```

---

### Task 9: `RemoteTerminalViewModel` — the read-only terminal

**Files:**
- Create: `src/Capacitor.App/ViewModels/RemoteTerminalViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/RemoteTerminalViewModelTests.cs`

**Interfaces:**
- Consumes: `IServerLane.TerminalOutput`, `TerminalDimensions`, `SubscribeToTerminalAsync`, `UnsubscribeFromTerminalAsync`, `RequestResizeTerminalAsync`, `ReleaseResizeTerminalAsync`, `SendSpecialKeyAsync` (Task 4); `ITerminalSurface` with `Resize` (Task 8); `SpecialKeyMapper`; `Utf8StreamDecoder`; `SessionAccessState`.
- Produces: `RemoteTerminalViewModel(string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<bool> sessionEnded, Func<ITerminalSurface> surfaceFactory)` with `Surface : ITerminalSurface?`, `Phase : RemoteTerminalPhase`, `PhaseNote`, `ShowsBanner`, `SizeNote`, `Keys : IReadOnlyList<SpecialKeyChoice>`, `SendKeyCommand : ReactiveCommand<string, Unit>`, `TeardownAsync()`; `enum RemoteTerminalPhase { Waiting, Connecting, Live, Offline, Ended }`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/RemoteTerminalViewModelTests.cs`:

```csharp
using System.Reactive.Subjects;
using System.Text;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The read-only terminal: subscribed only once access stands, sized by the source, its viewport
/// reported while live and released on close, and a fresh surface per subscription.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteTerminalViewModelTests {
    sealed class Harness {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly BehaviorSubject<bool> Ended = new(false);
        public readonly List<FakeTerminalSurface> Surfaces = [];
        public readonly RemoteTerminalViewModel Vm;

        public Harness() {
            Vm = new RemoteTerminalViewModel("a1", Lane, Access, Ended, () => { var s = new FakeTerminalSurface(); Surfaces.Add(s); return s; });
        }

        public static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    }

    [Test]
    public async Task Subscribes_only_once_access_is_established_then_shows_the_replay_and_live_frames() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Waiting);
            await Assert.That(h.Lane.TerminalSubscribes).IsEmpty();

            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            await Assert.That(h.Lane.TerminalSubscribes).IsEquivalentTo(new[] { "a1" });
            await Assert.That(h.Lane.Resizes).Contains(("a1", 80, 24));

            h.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 120, 40));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("hel")));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("other", Harness.B64("nope")));
            h.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Harness.B64("lo")));
            var surface = h.Surfaces.Single();
            await Assert.That(surface.Resizes).Contains((120, 40));
            await Assert.That(surface.Fed).IsEquivalentTo(new[] { "hel", "lo" });
            await Assert.That(h.Vm.SizeNote).IsEqualTo("120×40 · sized by the source");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_keystroke_that_is_a_wire_key_is_sent_and_anything_else_is_dropped() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var surface = h.Surfaces.Single();

            surface.RaiseInput([0x1b]);
            await WaitUntilAsync(() => h.Lane.SpecialKeys.Contains(("a1", SpecialKeys.Escape)), what: "escape");
            surface.RaiseInput("x"u8.ToArray());
            await h.Vm.SendKeyCommand.Execute(SpecialKeys.CtrlC);
            await Assert.That(h.Lane.SpecialKeys).IsEquivalentTo(new[] { ("a1", SpecialKeys.Escape), ("a1", SpecialKeys.CtrlC) });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task The_viewport_is_reported_while_live_and_released_on_teardown() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            h.Surfaces.Single().RaiseResize(100, 30);
            await WaitUntilAsync(() => h.Lane.Resizes.Contains(("a1", 100, 30)), what: "the viewport");

            await h.Vm.TeardownAsync();
            await Assert.That(h.Lane.TerminalUnsubscribes).Contains("a1");
            await Assert.That(h.Lane.ResizeReleases).Contains("a1");
            await Assert.That(h.Vm.Surface).IsNull();
        });
    }

    [Test]
    public async Task A_re_established_access_subscribes_again_onto_a_fresh_surface() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live");
            var first = h.Vm.Surface;

            h.Access.OnNext(SessionAccessState.Unavailable);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Offline);
            await WaitUntilAsync(() => h.Lane.ResizeReleases.Contains("a1"), what: "the viewport released");

            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Lane.TerminalSubscribes.Count == 2, what: "the second subscribe");
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Live, what: "live again");
            await Assert.That(h.Vm.Surface).IsNotSameReferenceAs(first);
            await Assert.That(h.Surfaces.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_refused_subscribe_reads_as_offline_and_an_ended_session_as_ended() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Lane.TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.NotConnected);
            h.Access.OnNext(SessionAccessState.Established);
            await WaitUntilAsync(() => h.Vm.Phase == RemoteTerminalPhase.Offline, what: "offline");
            await Assert.That(h.Lane.Resizes).IsEmpty();

            h.Ended.OnNext(true);
            await Assert.That(h.Vm.Phase).IsEqualTo(RemoteTerminalPhase.Ended);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo("This session has ended.");
            h.Access.OnNext(SessionAccessState.Established);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteTerminalViewModelTests/*"`
Expected: compile error — `RemoteTerminalViewModel` not defined.

- [ ] **Step 3: Implement**

`src/Capacitor.App/ViewModels/RemoteTerminalViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum RemoteTerminalPhase { Waiting, Connecting, Live, Offline, Ended }

/// A read-only view of a terminal on another machine: the server replays its buffer, then
/// streams. The pane takes the source's size, and the viewport it reports back is released when
/// it stops driving — the server's (0,0) is a clear sentinel a client never sends. Every access
/// establishment subscribes again onto a fresh surface, so a replay never stacks on scrollback.
/// Subscribing is attempted only once access stands: the server refuses with silence, so an
/// empty pane after that is "no output yet", never "not allowed".
public sealed class RemoteTerminalViewModel : ReactiveObject {
    readonly string _agentId;
    readonly IServerLane _lane;
    readonly Func<ITerminalSurface> _surfaceFactory;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly CancellationToken _token;
    int _generation;
    Utf8StreamDecoder? _decoder;
    bool _subscribed;
    bool _ended;
    (int Cols, int Rows)? _sourceSize;

    ITerminalSurface? _surface;
    public ITerminalSurface? Surface { get => _surface; private set => this.RaiseAndSetIfChanged(ref _surface, value); }

    RemoteTerminalPhase _phase = RemoteTerminalPhase.Waiting;
    public RemoteTerminalPhase Phase {
        get => _phase;
        private set {
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(ShowsBanner));
        }
    }

    public string PhaseNote => Phase switch {
        RemoteTerminalPhase.Waiting    => "Waiting for the session…",
        RemoteTerminalPhase.Connecting => "Connecting…",
        RemoteTerminalPhase.Offline    => "Not connected to the session",
        RemoteTerminalPhase.Ended      => "This session has ended.",
        _                              => "",
    };
    public bool ShowsBanner => Phase != RemoteTerminalPhase.Live;
    public string SizeNote => _sourceSize is { } s ? $"{s.Cols}×{s.Rows} · sized by the source" : "";
    public IReadOnlyList<SpecialKeyChoice> Keys => SpecialKeyMapper.Choices;
    public ReactiveCommand<string, Unit> SendKeyCommand { get; }

    public RemoteTerminalViewModel(
            string agentId, IServerLane lane, IObservable<SessionAccessState> access, IObservable<bool> sessionEnded,
            Func<ITerminalSurface> surfaceFactory) {
        _agentId = agentId;
        _lane = lane;
        _surfaceFactory = surfaceFactory;
        _token = _lifetime.Token;
        SendKeyCommand = ReactiveCommand.CreateFromTask<string>(SendKeyAsync);
        _disposables.Add(SendKeyCommand);

        access.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(state => {
            if (_ended) return;
            if (state == SessionAccessState.Established) Attach();
            else Detach(RemoteTerminalPhase.Offline);
        }).DisposeWith(_disposables);
        sessionEnded.Where(ended => ended).Take(1).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => {
            _ended = true;
            Detach(RemoteTerminalPhase.Ended);
        }).DisposeWith(_disposables);
        lane.TerminalOutput.Where(f => f.AgentId == agentId).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(f => {
            if (Surface is not { } surface || _decoder is not { } decoder) return;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(f.Base64); } catch (FormatException) { return; }
            surface.Feed(decoder.Decode(bytes));
        }).DisposeWith(_disposables);
        lane.TerminalDimensions.Where(d => d.AgentId == agentId).ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(d => {
            _sourceSize = (d.Cols, d.Rows);
            Surface?.Resize(d.Cols, d.Rows);
            this.RaisePropertyChanged(nameof(SizeNote));
        }).DisposeWith(_disposables);
    }

    void Attach() {
        var generation = ++_generation;
        var surface = _surfaceFactory();
        var decoder = new Utf8StreamDecoder();
        surface.InputProduced += bytes => {
            if (generation != _generation || SpecialKeyMapper.Map(bytes) is not { } key) return;
            _ = SendKeyAsync(key);
        };
        surface.Resized += (cols, rows) => {
            if (generation != _generation || !_subscribed) return;
            _ = Report(_lane.RequestResizeTerminalAsync(_agentId, cols, rows, _token), "resize");
        };
        if (_sourceSize is { } size) surface.Resize(size.Cols, size.Rows);
        _decoder = decoder;
        Surface = surface;
        Phase = RemoteTerminalPhase.Connecting;
        _ = SubscribeAsync(generation, surface);
    }

    async Task SubscribeAsync(int generation, ITerminalSurface surface) {
        HubCallOutcome outcome;
        try {
            outcome = await _lane.SubscribeToTerminalAsync(_agentId, _token);
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) {
            outcome = HubCallOutcome.Failed(ex.Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() => {
            if (generation != _generation) return;
            if (outcome.Result != HubCallResult.Ok) { Phase = RemoteTerminalPhase.Offline; return; }
            _subscribed = true;
            Phase = RemoteTerminalPhase.Live;
            var (cols, rows) = surface.CurrentSize;
            _ = Report(_lane.RequestResizeTerminalAsync(_agentId, cols, rows, _token), "resize");
        });
    }

    void Detach(RemoteTerminalPhase phase) {
        _generation++;
        Phase = phase;
        if (!_subscribed) return;
        _subscribed = false;
        _ = ReleaseAsync();
    }

    /// Unsubscribe, then release the viewport: the server keeps a viewer's size in its aggregate
    /// until told otherwise or until the whole connection drops.
    async Task ReleaseAsync() {
        try {
            await _lane.UnsubscribeFromTerminalAsync(_agentId, CancellationToken.None);
            await _lane.ReleaseResizeTerminalAsync(_agentId, CancellationToken.None);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: remote terminal release: {ex.Message}");
        }
    }

    async Task SendKeyAsync(string key) {
        if (!_subscribed || Phase != RemoteTerminalPhase.Live) return;
        await Report(_lane.SendSpecialKeyAsync(_agentId, key, _token), $"key {key}");
    }

    static async Task Report(Task<HubCallOutcome> call, string what) {
        try {
            var outcome = await call;
            if (outcome.Result is HubCallResult.Denied or HubCallResult.Failed)
                Console.Error.WriteLine($"kcap: remote terminal {what}: {outcome.Reason}");
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: remote terminal {what}: {ex.Message}");
        }
    }

    public async Task TeardownAsync() {
        _generation++;
        _disposables.Dispose();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        if (_subscribed) {
            _subscribed = false;
            await ReleaseAsync();
        }
        Surface = null;
        _decoder = null;
        _lifetime.Dispose();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteTerminalViewModelTests/*"`
Expected: all five PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/RemoteTerminalViewModel.cs test/Capacitor.App.Tests.Unit/RemoteTerminalViewModelTests.cs
git commit -m "Watch a remote agent's terminal read-only, sized by its source (#806)"
```

---

### Task 10: The terminal tab in the remote host

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`
- Modify: `src/Capacitor.App/Views/RemoteSessionView.axaml`, `src/Capacitor.App/Views/RemoteSessionView.axaml.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildRemote`)
- Test: `test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs`, `RemoteSessionViewSmokeTests.cs`

**Interfaces:**
- Consumes: `RemoteTerminalViewModel` (Task 9); `HostedHarnessCatalog.ShowsTerminal(bool?, string)`, `TerminalSurfaceModelConverter` (existing).
- Produces on `RemoteSessionViewModel`: a trailing ctor parameter `Func<ITerminalSurface>? surfaceFactory = null`; `Terminal : RemoteTerminalViewModel?`, `ShowsTerminalTab`, `IsTerminalActive`, `ShowsTerminalPane`, `ShowTerminalCommand`.

- [ ] **Step 1: Write the failing tests**

Append to `RemoteSessionViewModelTests`:

```csharp
    [Test]
    public async Task A_pty_harness_offers_a_terminal_tab_that_subscribes_once_access_stands_and_a_frame_harness_does_not() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var pty = h.Build(Harness.Row(vendor: "claude"));
            await Assert.That(pty.ShowsTerminalTab).IsTrue();
            await Assert.That(pty.IsChatActive).IsTrue();
            await WaitUntilAsync(() => pty.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => pty.Terminal!.Phase == RemoteTerminalPhase.Live, what: "the terminal live");
            await Assert.That(h.Lane.TerminalSubscribes).Contains("a1");

            await pty.ShowTerminalCommand.Execute();
            await Assert.That(pty.IsTerminalActive).IsTrue();
            await Assert.That(pty.ShowsTerminalPane).IsTrue();
            await Assert.That(pty.ShowsChatPane).IsFalse();
            await pty.TeardownAsync();
            await Assert.That(h.Lane.ResizeReleases).Contains("a1");

            var frame = h.Build(Harness.Row(id: "a2", sessionId: "s2", vendor: "gemini"));
            await Assert.That(frame.ShowsTerminalTab).IsFalse();
            await Assert.That(frame.Terminal).IsNull();
            await frame.TeardownAsync();
        });
    }
```

In the same file's `Harness.Build`, pass a surface factory as the trailing argument: `…, new RecordingOpener(), Time, () => new FakeTerminalSurface());`. In `RemoteSessionViewSmokeTests.Host`, build the row with `Vendor = "claude"` and pass `() => new FakeTerminalSurface()` the same way, and append:

```csharp
    /// The tab strip swaps the panes; the terminal control is in the tree only for a PTY harness.
    [Test]
    public async Task The_terminal_tab_swaps_the_pane_and_offers_the_special_keys() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            var terminalTab = host.View.FindControl<Button>("TerminalTabButton")!;
            var terminalHost = host.View.FindControl<Control>("TerminalPane")!;
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(terminalTab.IsVisible).IsTrue();
            await Assert.That(terminalHost.IsVisible).IsFalse();

            await host.Vm.ShowTerminalCommand.Execute();
            await host.SettleUntilAsync(() => terminalHost.IsVisible, "the terminal pane");
            await Assert.That(chat.IsVisible).IsFalse();
            var keys = host.View.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("specialKey")).Select(b => b.Content as string).ToList();
            await Assert.That(keys).Contains("Esc");
            await Assert.That(keys).Contains("Ctrl+C");

            await host.Vm.TeardownAsync();
            return true;
        });
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteSession*/*"`
Expected: compile error — `Terminal`, `ShowTerminalCommand`, the surface-factory argument.

- [ ] **Step 3: Extend the view model**

In `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`:

Add the members:

```csharp
    /// Null for a harness with no PTY; the server registry carries no terminal flag, so the
    /// vendor's family decides, exactly as it does for a local dto without one.
    public RemoteTerminalViewModel? Terminal { get; }
    public bool ShowsTerminalTab => Terminal is not null;
    public bool IsTerminalActive => ActiveTab == RemoteTab.Terminal;
    public bool ShowsTerminalPane => ShowsPanes && IsTerminalActive;
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }
```

Add `this.RaisePropertyChanged(nameof(IsTerminalActive));` and `this.RaisePropertyChanged(nameof(ShowsTerminalPane));` to `RaiseTabProjections`.

Give the constructor the trailing parameter `Func<ITerminalSurface>? surfaceFactory = null` and, right after `Chat = new ChatTabViewModel(…)`:

```csharp
        Terminal = surfaceFactory is not null && HostedHarnessCatalog.ShowsTerminal(null, row.Vendor)
            ? new RemoteTerminalViewModel(row.Id, lane, _accessStates, _sessionEndedChanges, surfaceFactory)
            : null;
```

Next to `ShowChatCommand`:

```csharp
        ShowTerminalCommand = ReactiveCommand.Create(() => { if (ShowsTerminalTab) ActiveTab = RemoteTab.Terminal; });
        _disposables.Add(ShowTerminalCommand);
```

`TeardownAsync` becomes:

```csharp
    public async Task TeardownAsync() {
        _disposables.Dispose();
        await Chat.TeardownAsync();
        if (Terminal is { } terminal) await terminal.TeardownAsync();
    }
```

- [ ] **Step 4: Extend the view**

In `src/Capacitor.App/Views/RemoteSessionView.axaml`, add `xmlns:terminal="clr-namespace:SvcSystems.UI.Terminal;assembly=SvcSystems.UI.Terminal"` to the root element, add after the Chat tab button:

```xml
                <Button x:Name="TerminalTabButton" Content="Terminal" Classes="tab" Classes.active="{Binding IsTerminalActive}"
                        Command="{Binding ShowTerminalCommand}" IsVisible="{Binding ShowsTerminalTab}" />
```

and replace the content `Grid` (row 3) with:

```xml
        <Grid Grid.Row="3">
            <!-- The active-tab flag lives on this VM, which is not the pane's DataContext. -->
            <views:ChatTabView x:Name="ChatHost" DataContext="{Binding Chat}"
                               IsVisible="{Binding $parent[views:RemoteSessionView].((vm:RemoteSessionViewModel)DataContext).ShowsChatPane}" />

            <Grid x:Name="TerminalPane" RowDefinitions="*,Auto" IsVisible="{Binding ShowsTerminalPane}">
                <!-- FontFamily MUST be set: the control's default is "Cascadia Mono", which only
                     exists on Windows — elsewhere it silently falls back to ESTIMATED cell metrics
                     while drawing with a real typeface, smearing every styled run off its column. -->
                <terminal:TerminalControl x:Name="TerminalHost" Grid.Row="0"
                                           FontFamily="Menlo,Monaco,Consolas,Cascadia Mono,DejaVu Sans Mono,monospace"
                                           CaretBrush="{StaticResource KcapTextBrush}"
                                           Model="{Binding Terminal.Surface, Converter={x:Static views:TerminalSurfaceModelConverter.Instance}}" />
                <Border Grid.Row="0" IsVisible="{Binding Terminal.ShowsBanner}"
                        Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="1"
                        CornerRadius="8" Padding="14,8" HorizontalAlignment="Center" VerticalAlignment="Top" Margin="0,14,0,0">
                    <TextBlock x:Name="TerminalPhaseNote" Text="{Binding Terminal.PhaseNote}" Foreground="{StaticResource KcapMutedBrush}" />
                </Border>
                <!-- Read-only: the keys the daemon accepts are the only input that crosses. -->
                <Border Grid.Row="1" BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="0,1,0,0" Padding="20,8">
                    <Grid ColumnDefinitions="*,Auto">
                        <ItemsControl Grid.Column="0" ItemsSource="{Binding Terminal.Keys}">
                            <ItemsControl.ItemsPanel>
                                <ItemsPanelTemplate>
                                    <StackPanel Orientation="Horizontal" Spacing="6" />
                                </ItemsPanelTemplate>
                            </ItemsControl.ItemsPanel>
                            <ItemsControl.ItemTemplate>
                                <DataTemplate x:DataType="services:SpecialKeyChoice">
                                    <Button Classes="specialKey" Content="{Binding Label}" Padding="10,4" FontSize="12" CornerRadius="7"
                                            Command="{Binding $parent[views:RemoteSessionView].((vm:RemoteSessionViewModel)DataContext).Terminal.SendKeyCommand}"
                                            CommandParameter="{Binding Key}" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <TextBlock Grid.Column="1" Text="{Binding Terminal.SizeNote}" FontSize="11.5" VerticalAlignment="Center"
                                   Foreground="{StaticResource KcapFaintBrush}" />
                    </Grid>
                </Border>
            </Grid>
        </Grid>
```

with `xmlns:services="clr-namespace:Capacitor.App.Services"` added to the root element.

In `RemoteSessionView.axaml.cs`, the focus subscription becomes:

```csharp
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null
                && DataContext is RemoteSessionViewModel { IsTerminalActive: true })
                TerminalHost.Focus();
        };
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            var model = DataContext as RemoteSessionViewModel;
            _tabFocus = model?
                .WhenAnyValue(vm => vm.ActiveTab, vm => vm.ShowsPanes)
                .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(model, DataContext) || !pair.Item2) return;
                    if (pair.Item1 == RemoteTab.Chat) ChatHost.FocusComposer();
                    else TerminalHost.Focus();
                }, DispatcherPriority.Loaded));
        };
```

- [ ] **Step 5: Wire the composition root**

In `App.axaml.cs`, `BuildRemote` passes the same surface factory the local workspace uses as its trailing argument: `…, opener, TimeProvider.System, () => new XtermTerminalSurface(80, 24, PtyDumpPath))`.

- [ ] **Step 6: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteSession*/*"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs src/Capacitor.App/Views/RemoteSessionView.axaml src/Capacitor.App/Views/RemoteSessionView.axaml.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewSmokeTests.cs
git commit -m "Offer a remote PTY session's terminal as a tab (#806)"
```

---

### Task 11: An open workspace follows its row across lanes

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (`BuildAndShowMainWindow` forwards the directory)
- Test: `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs`

**Interfaces:**
- Consumes: `RemoteSessionViewModel.OriginChangedChanges`, `IsTerminalActive`, `ShowTerminalCommand` (Tasks 7, 10); `WorkspaceViewModel.IsTerminalActive`, `ShowTerminalCommand` (existing); `IAgentDirectory.Rows`; `SessionStatusDots.IsTerminal`.
- Produces: `MainWindowViewModel` trailing ctor parameter `IAgentDirectory? directory = null`.

- [ ] **Step 1: Update the harness and write the failing tests**

In `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs`: give `NewVm` a trailing `IAgentDirectory? directory = null` parameter forwarded as `directory: directory`; make `RemoteHost.New(string agentId, string? sessionId)` pass `() => new FakeTerminalSurface()` as the remote host's trailing argument. Replace the test `An_open_remote_host_whose_row_moved_to_this_machine_reopens_as_the_local_workspace` with:

```csharp
    /// The directory's own twin verdict plus one session id across both rows prove the move;
    /// the window then swaps to the local workspace on its own, keeping the tab in use.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_remote_host_whose_row_moved_to_this_machine_becomes_the_local_workspace_on_its_own() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Remote,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);

            vm.OpenSession("r1");
            var remote = (RemoteSessionViewModel)vm.CurrentWorkspace!;
            await remote.ShowTerminalCommand.Execute();
            await Assert.That(remote.IsTerminalActive).IsTrue();

            host.Directory.ProvenTwins.Add("r1");
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("r1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            host.Directory.Rows.Remove("remote:r1");

            await Assert.That(remote.OriginChangedToLocal).IsTrue();
            await Assert.That(vm.CurrentWorkspace).IsTypeOf<WorkspaceViewModel>();
            await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).AgentId).IsEqualTo("r1");
            await Assert.That(((WorkspaceViewModel)vm.CurrentWorkspace!).IsTerminalActive).IsTrue();
        });
    }

    /// The daemon dropping a row is not the end of the session while the server still shows the
    /// same agent live under the user's other daemon: the workspace follows it there.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_open_local_workspace_whose_row_the_daemon_dropped_becomes_the_remote_host_while_the_server_shows_it_live() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: id => host.Directory.Rows.Lookup($"local:{id}").HasValue ? AgentOrigin.Local
                    : host.Directory.Rows.Lookup($"remote:{id}").HasValue ? AgentOrigin.Remote : null,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));

            vm.OpenSession("a1");
            var local = (WorkspaceViewModel)vm.CurrentWorkspace!;
            await local.ShowTerminalCommand.Execute();

            host.Directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RegisteredAt = DateTime.UtcNow,
            }));
            host.Directory.Rows.Remove("local:a1");

            await Assert.That(vm.CurrentWorkspace).IsTypeOf<RemoteSessionViewModel>();
            await Assert.That(((RemoteSessionViewModel)vm.CurrentWorkspace!).IsTerminalActive).IsTrue();
        });
    }

    /// With no live server row, a dropped local row is what it always was: the session ended,
    /// and the workspace stays to say so.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dropped_local_row_with_no_server_row_keeps_the_local_workspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var host = new RemoteHost();
            var service = new FakeDaemonClientService();
            var vm = NewVm(service,
                workspaceFactory: id => NewWorkspace(service, id),
                originOf: _ => AgentOrigin.Local,
                remoteWorkspaceFactory: id => host.New(id, "s1"),
                trackWorkspaceTeardown: teardown => _ = teardown(),
                directory: host.Directory);
            host.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            vm.OpenSession("a1");
            var local = vm.CurrentWorkspace;

            host.Directory.Rows.Remove("local:a1");

            await Assert.That(vm.CurrentWorkspace).IsSameReferenceAs(local);
        });
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindowViewModelTests/*"`
Expected: compile error — no `directory` parameter.

- [ ] **Step 3: Implement the rebinding**

In `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`:

Add the field and the ctor parameter (trailing, after `remoteWorkspaceFactory`): `IAgentDirectory? directory = null` stored as `_directory = directory;`, plus:

```csharp
    readonly IAgentDirectory? _directory;
    readonly SerialDisposable _rebind = new();
```

In `SwapTo`, after `CurrentWorkspace = next;` add `WatchOrigin(next);`. In `LatchShutdown`, after `CurrentWorkspace = null;` add `_rebind.Disposable = Disposable.Empty;`. Add:

```csharp
    /// An open workspace follows its row across lanes: a remote host whose agent the local daemon
    /// proved it hosts becomes the local workspace, and a local workspace whose row the daemon
    /// dropped while the server still shows the agent live becomes the remote host. The tab in
    /// use carries over. Nothing here ends a session — the row that wins says whether it did.
    void WatchOrigin(ISessionWorkspace? workspace) {
        _rebind.Disposable = workspace switch {
            RemoteSessionViewModel remote => remote.OriginChangedChanges
                .Where(moved => moved)
                .Take(1)
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => Rebind(remote, AgentOrigin.Local, remote.IsTerminalActive)),
            WorkspaceViewModel local when _directory is { } directory => directory.Rows.Connect()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Where(changes => changes.Any(c => c.Key == $"local:{local.AgentId}" && c.Reason == ChangeReason.Remove))
                .Where(_ => directory.Rows.Lookup($"remote:{local.AgentId}") is { HasValue: true, Value: var row } && !SessionStatusDots.IsTerminal(row.Status))
                .Take(1)
                .Subscribe(_ => Rebind(local, AgentOrigin.Remote, local.IsTerminalActive)),
            _ => Disposable.Empty,
        };
    }

    void Rebind(ISessionWorkspace open, AgentOrigin origin, bool terminal) {
        if (!ReferenceEquals(CurrentWorkspace, open)) return;
        OpenSession(open.AgentId, origin);
        if (!terminal || ReferenceEquals(CurrentWorkspace, open)) return;
        switch (CurrentWorkspace) {
            case WorkspaceViewModel local: local.ShowTerminalCommand.Execute().Subscribe(); break;
            case RemoteSessionViewModel remote: remote.ShowTerminalCommand.Execute().Subscribe(); break;
        }
    }
```

Add `using System.Reactive.Disposables;`, `using System.Reactive.Linq;` and `using DynamicData;` to the file if they are not there.

In `src/Capacitor.App/App.axaml.cs`, inside `BuildAndShowMainWindow`, the `new MainWindowViewModel(…)` call gains `directory: resolvedDirectory`.

- [ ] **Step 4: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/MainWindow*/*"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/MainWindowViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs
git commit -m "Follow an open workspace's row when its agent changes lane (#806)"
```

---

### Task 12: The server's queued prompts in the remote chat

**Files:**
- Create: `src/Capacitor.Remote.Models/QueuedInputItem.cs`
- Modify: `src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`
- Create: `src/Capacitor.App/Services/PendingInputUpdate.cs`
- Modify: `src/Capacitor.App/Services/IServerLane.cs`, `ServerConnectionService.cs`, `NoRemoteAgents.cs`
- Modify: `src/Capacitor.App/ViewModels/QueuedChatMessage.cs`, `ChatTabViewModel.cs`, `RemoteSessionViewModel.cs`, `src/Capacitor.App/Views/ChatTabView.axaml`
- Modify: `test/Capacitor.App.Tests.Unit/FakeServerLane.cs`, `HubTestHost.cs`
- Test: `test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs`, `test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs`, `ChatTabViewModelTests.cs`, `RemoteSessionViewModelTests.cs`

**Interfaces:**
- Produces: `record QueuedInputItem { Guid DispatchId; string? SenderUserId; string Text; DateTimeOffset DispatchedAt }` (`dispatch_id`, `sender_user_id`, `text`, `dispatched_at`); `record PendingInputUpdate(string SessionId, IReadOnlyList<QueuedInputItem> Items)`; `IServerLane.PendingInputChanged : IObservable<PendingInputUpdate>` (the chat join's snapshot is published on it too); `ChatTabViewModel` primary ctor trailing parameter `IObservable<IReadOnlyList<QueuedInputItem>>? serverQueue = null`; `QueuedChatMessage.IsForeign`, `Sender`.

- [ ] **Step 1: Write the failing tests**

Append to `WireShapeTests`:

```csharp
[Test]
public async Task QueuedInputRoundTripsSnakeCaseAndTolerantlyReadsAThinItem() {
    const string json = """[{"dispatch_id":"3f2c1b1e-1111-4a2b-9c3d-000000000001","sender_user_id":"u2","text":"next","attachments":[],"dispatched_at":"2026-09-14T10:00:00Z"},{"text":"bare"}]""";
    var items = JsonSerializer.Deserialize(json, RemoteModelsJsonContext.Default.QueuedInputItemArray)!;
    await Assert.That(items[0].DispatchId).IsEqualTo(Guid.Parse("3f2c1b1e-1111-4a2b-9c3d-000000000001"));
    await Assert.That(items[0].SenderUserId).IsEqualTo("u2");
    await Assert.That(items[1].Text).IsEqualTo("bare");
    await Assert.That(items[1].DispatchId).IsEqualTo(Guid.Empty);
}
```

Append to `ServerConnectionServiceTests`:

```csharp
[Test]
public async Task TheChatJoinSnapshotAndPendingInputPushesSurfaceAsQueueUpdates() {
    await using var host = await HubTestHost.StartAsync();
    HubTestHost.ChatSnapshot.Add(new QueuedInputItem { DispatchId = Guid.NewGuid(), SenderUserId = "u2", Text = "queued one" });
    await using var lane = Lane(host);
    lane.Start();
    await Next(lane.Status, s => s.State == ServerLaneState.Connected);

    var updates = lane.PendingInputChanged.Take(2).ToList().ToTask();
    await Assert.That((await lane.SubscribeToChatAsync("s1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
    await host.BroadcastAsync(HubBroadcasts.PendingInputChanged, "a1", "s1",
        new[] { new QueuedInputItem { DispatchId = Guid.NewGuid(), Text = "queued two" } });
    var received = await updates.WaitAsync(TimeSpan.FromSeconds(10));
    await Assert.That(received[0].SessionId).IsEqualTo("s1");
    await Assert.That(received[0].Items.Single().Text).IsEqualTo("queued one");
    await Assert.That(received[1].Items.Single().Text).IsEqualTo("queued two");
}
```

Append to `ChatTabViewModelTests` (add `using System.Reactive.Subjects;`, `using Capacitor.Remote.Models;`):

```csharp
    /// A channel that takes every send, for the queue tests: the transcript, not the channel,
    /// is what retires a message.
    sealed class AcceptingChatInput : ChatInput {
        public override SendAvailability Availability => SendAvailability.Ready;
        public override bool CanAcceptText => true;
        public override string Hint => "";
        public override Task<ChatSendOutcome> SendAsync(string text, CancellationToken ct) => Task.FromResult(ChatSendOutcome.Accepted);
        public override void Dispose() { }
    }

    sealed class EmptyFeed : IChatTranscriptFeed {
        public FeedRead ReadAppended() => new(FeedStatus.Ok, []);
        public long? CurrentOffset => 0;
        public void Dispose() { }
    }

    static QueuedInputItem Item(string text, Guid id, string? sender = "u2") => new() { DispatchId = id, Text = text, SenderUserId = sender };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_servers_queue_confirms_an_own_send_and_lists_and_retires_the_others() {
        await RunOnUiAsync(async () => {
            var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
            var session = new BehaviorSubject<ChatSessionInfo>(new("Running", "Running", "gemini", null, null, null, false, "", "s1"));
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null), new AcceptingChatInput(), _ => new EmptyFeed(),
                new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), serverQueue: queue);

            chat.ComposerText = "do it";
            await chat.SendCommand.Execute();
            var own = chat.QueuedMessages.Single();
            await Assert.That(own.IsForeign).IsFalse();

            var mine = Guid.NewGuid();
            var theirs = Guid.NewGuid();
            queue.OnNext([Item("do it", mine, sender: "u1"), Item("and this", theirs)]);
            await Assert.That(chat.QueuedMessages.Count).IsEqualTo(2);
            await Assert.That(own.IsUnconfirmed).IsFalse();
            var foreign = chat.QueuedMessages.Single(q => q.IsForeign);
            await Assert.That(foreign.Text).IsEqualTo("and this");
            await Assert.That(foreign.Sender).IsEqualTo("u2");
            await Assert.That(chat.QueueSummary).IsEqualTo("2 messages queued");

            queue.OnNext([Item("do it", mine, sender: "u1")]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);

            // The own message leaves with the transcript's echo, never with the queue alone.
            queue.OnNext([]);
            await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(own);
            await chat.TeardownAsync();
        });
    }
```

Append to `RemoteSessionViewModelTests`:

```csharp
    [Test]
    public async Task A_prompt_queued_by_another_client_shows_in_the_chats_queue() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            h.Lane.PendingInputSubject.OnNext(new("s1", [new QueuedInputItem { DispatchId = Guid.NewGuid(), SenderUserId = "u2", Text = "after this one" }]));
            await WaitUntilAsync(() => vm.Chat.QueuedMessages.Count == 1, what: "the foreign row");
            await Assert.That(vm.Chat.QueuedMessages[0].IsForeign).IsTrue();
            h.Lane.PendingInputSubject.OnNext(new("other-session", []));
            await Assert.That(vm.Chat.QueuedMessages.Count).IsEqualTo(1);
            await vm.TeardownAsync();
        });
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj -- --treenode-filter "/*/*/WireShapeTests/*"`
Expected: compile error — `QueuedInputItem` not defined.

- [ ] **Step 3: Implement**

`src/Capacitor.Remote.Models/QueuedInputItem.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// One prompt the server holds for an agent's next turn: what SubscribeToChat returns and
/// PendingInputChanged pushes. Nothing is required, so one thin item cannot drop a whole push.
public sealed record QueuedInputItem {
    [JsonPropertyName("dispatch_id")]    public Guid DispatchId { get; init; }
    [JsonPropertyName("sender_user_id")] public string? SenderUserId { get; init; }
    [JsonPropertyName("text")]           public string Text { get; init; } = "";
    [JsonPropertyName("dispatched_at")]  public DateTimeOffset DispatchedAt { get; init; }
}
```

Add `[JsonSerializable(typeof(QueuedInputItem[]))]` to `RemoteModelsJsonContext`.

`src/Capacitor.App/Services/PendingInputUpdate.cs`:

```csharp
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// The server's whole queue for one session, as of one push or one chat join.
public sealed record PendingInputUpdate(string SessionId, IReadOnlyList<QueuedInputItem> Items);
```

`IServerLane`: add `IObservable<PendingInputUpdate> PendingInputChanged { get; }`.

`ServerConnectionService`: add the subject and property

```csharp
    readonly Subject<PendingInputUpdate> _pendingInput = new();
    public IObservable<PendingInputUpdate> PendingInputChanged => _pendingInput.AsObservable();
```

in `Build()`:

```csharp
        hub.On<string, string, JsonElement?>(HubBroadcasts.PendingInputChanged, (_, sessionId, items) => _pendingInput.OnNext(new(sessionId, ParseQueue(items))));
```

replace `SubscribeToChatAsync` with:

```csharp
    /// The join answers with the session's queue; it rides the same stream as the pushes so a
    /// consumer sees one shape.
    public async Task<HubCallOutcome> SubscribeToChatAsync(string sessionId, CancellationToken ct) {
        var (outcome, snapshot) = await InvokeAsync<JsonElement?>(HubMethods.SubscribeToChat, ct, sessionId).ConfigureAwait(false);
        if (outcome.Result == HubCallResult.Ok) _pendingInput.OnNext(new(sessionId, ParseQueue(snapshot)));
        return outcome;
    }

    // Lenient on purpose: binding the push to the typed array would drop the whole push over one
    // item that does not read as one.
    static IReadOnlyList<QueuedInputItem> ParseQueue(JsonElement? items) {
        if (items is not { ValueKind: JsonValueKind.Array } array) return [];
        try { return array.Deserialize(RemoteModelsJsonContext.Default.QueuedInputItemArray) ?? []; }
        catch (JsonException) { return []; }
    }

    async Task<(HubCallOutcome Outcome, T? Result)> InvokeAsync<T>(string method, CancellationToken ct, params object?[] args) {
        var hub = _hub;
        if (hub is not { State: HubConnectionState.Connected }) return (HubCallOutcome.NotConnected, default);
        try {
            return (HubCallOutcome.Ok, await hub.InvokeCoreAsync<T>(method, args, ct).ConfigureAwait(false));
        } catch (OperationCanceledException) {
            throw;
        } catch (HubException ex) when (ex.Message.Contains(WireTokens.SessionNotVisible, StringComparison.Ordinal)) {
            return (HubCallOutcome.Denied(ex.Message), default);
        } catch (Exception ex) {
            return (HubCallOutcome.Failed(ex.Message), default);
        }
    }
```

`NoServerLane`: `public IObservable<PendingInputUpdate> PendingInputChanged => Observable.Never<PendingInputUpdate>();`

`FakeServerLane`: `public readonly Subject<PendingInputUpdate> PendingInputSubject = new();` and `public IObservable<PendingInputUpdate> PendingInputChanged => PendingInputSubject;`

`HubTestHost`: add `public static List<QueuedInputItem> ChatSnapshot { get; } = [];` (cleared in `StartAsync`), and change `SubscribeToChat` to return `QueuedInputItem[]`: `return ChatSnapshot.ToArray();`.

`QueuedChatMessage`: add

```csharp
    /// The server's id for this prompt once it has listed it; null until then.
    internal Guid? DispatchId { get; private set; }
    /// Queued by another client: shown, never acknowledged here, retired when the server drops it.
    public bool IsForeign { get; private init; }
    public string Sender { get; private init; } = "";

    internal static QueuedChatMessage FromServer(QueuedInputItem item) =>
        new(item.Text, composerEdits: -1, generation: -1, offset: null) { DispatchId = item.DispatchId, IsForeign = true, Sender = item.SenderUserId ?? "" };

    internal void MarkQueued(Guid dispatchId) {
        DispatchId = dispatchId;
        IsUnconfirmed = false;
    }

    internal bool MatchesText(string text) => Normalize(Text) == Normalize(text);
```

`ChatTabViewModel`: add the trailing primary-ctor parameter `IObservable<IReadOnlyList<QueuedInputItem>>? serverQueue = null` (the local overload passes nothing), and at the end of the primary ctor:

```csharp
        serverQueue?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(ApplyServerQueue).DisposeWith(_disposables);
```

Add the method:

```csharp
    /// The server's queue for this session, whoever queued it. An own send it lists is queued
    /// for certain; a prompt of nobody's here is another client's; a prompt it no longer lists
    /// was delivered or withdrawn. Own sends still leave with the transcript's echo.
    void ApplyServerQueue(IReadOnlyList<QueuedInputItem> items) {
        var listed = new HashSet<Guid>();
        foreach (var item in items) {
            if (!listed.Add(item.DispatchId)) continue;
            if (_queuedMessages.Any(q => q.DispatchId == item.DispatchId)) continue;
            var own = _queuedMessages.FirstOrDefault(q => q.DispatchId is null && !q.Acknowledged && q.MatchesText(item.Text));
            if (own is not null) own.MarkQueued(item.DispatchId);
            else _queuedMessages.Add(QueuedChatMessage.FromServer(item));
        }
        foreach (var gone in _queuedMessages.Where(q => q.IsForeign && q.DispatchId is { } id && !listed.Contains(id)).ToList())
            _queuedMessages.Remove(gone);
        RefreshQueue();
    }
```

In `Apply`, the rebase of baseline-less messages becomes `foreach (var queued in _queuedMessages.Where(q => !q.HasBaseline && !q.IsForeign))`; in `RebaseQueuedMessages`, `foreach (var queued in _queuedMessages)` becomes `foreach (var queued in _queuedMessages.Where(q => !q.IsForeign))`; in `OnSession`, the ended loop becomes `foreach (var queued in _queuedMessages.Where(q => !q.IsForeign))`.

`RemoteSessionViewModel`: pass to the chat's constructor, as its trailing argument:

```csharp
            serverQueue: _sessionIds
                .Select(sid => sid is null
                    ? Observable.Empty<IReadOnlyList<QueuedInputItem>>()
                    : lane.PendingInputChanged.Where(u => u.SessionId == sid).Select(u => u.Items))
                .Switch()
```

`src/Capacitor.App/Views/ChatTabView.axaml`: in the queued-message template, next to the `Delivery unconfirmed` TextBlock, add one with the same styling:

```xml
<TextBlock Text="Queued by another client" IsVisible="{Binding IsForeign}" FontSize="11" Foreground="{StaticResource KcapFaintBrush}" />
```

- [ ] **Step 4: Build and run**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj`
Expected: success, zero warnings.

Run: `dotnet run --project test/Capacitor.Remote.Models.Tests.Unit/Capacitor.Remote.Models.Tests.Unit.csproj`
Expected: PASS.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerConnectionServiceTests/*"`, then `"/*/*/Chat*/*"`, then `"/*/*/RemoteSession*/*"`
Expected: all PASS.

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: prints nothing (Remote.Models changed).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Remote.Models/QueuedInputItem.cs src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs src/Capacitor.App/Services/PendingInputUpdate.cs src/Capacitor.App/Services/IServerLane.cs src/Capacitor.App/Services/ServerConnectionService.cs src/Capacitor.App/Services/NoRemoteAgents.cs src/Capacitor.App/ViewModels/QueuedChatMessage.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs src/Capacitor.App/Views/ChatTabView.axaml test/Capacitor.App.Tests.Unit/FakeServerLane.cs test/Capacitor.App.Tests.Unit/HubTestHost.cs test/Capacitor.Remote.Models.Tests.Unit/WireShapeTests.cs test/Capacitor.App.Tests.Unit/ServerConnectionServiceTests.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs
git commit -m "Show the server's queued prompts in a remote chat (#806)"
```

---

### Task 13: Docs and the verification pass

**Files:**
- Modify: `docs/CHANGES.md`, `README.md`

- [ ] **Step 1: Record the invariants**

Insert into `docs/CHANGES.md`, directly after the "Desktop shell: remote control — stop, permission and question cards" entry:

```markdown
## Desktop shell: remote workspace — chat and read-only terminal

A session on another machine opens as a workspace: its transcript as chat, a composer, and for a
PTY harness a read-only terminal, all over the server. Three rules hold it together.

**One chat pane, two feeds.** The chat reads rows through a feed seam: locally a tail of the
transcript file, remotely a seed from the session detail route followed by a live tail of the
session's stream from the position the seed ended at. Every access establishment restarts the
tail from the last position seen and the seed is fetched only until one lands, so a reconnect
resumes rather than replays rows under the user. Server events reach the same envelope mapping
and vendor rules the file path applies, so the two paths cannot disagree about a row.

**Authorization is the server's word, never inferred from silence.** The seed fetch and the stream
subscribe both refuse loudly; the terminal subscribe, which the server refuses with silence, is
attempted only once the session's access lease reads Established. An empty terminal after that is
"no output yet", and a lane loss keeps the rows it already has.

**A reported viewport is released, and (0,0) is never sent.** The server folds every viewer's size
into the PTY's clamp until told otherwise, so a viewer that stops driving releases its size, and
each establishment subscribes onto a fresh surface so the replay never stacks on old scrollback.
Keystrokes cross only as the daemon's seven special keys; everything else stays local.
```

In `README.md`, under `### Desktop app (macOS)`, add one sentence to the paragraph that describes what the app shows: `Sessions running on your other machines' daemons open in the app too — their chat, their prompts, and for a terminal harness a read-only view of the terminal — over the server, without a local daemon.`

- [ ] **Step 2: The whole solution, every suite, the AOT publish, the id check**

Run: `dotnet build Capacitor.slnx`
Expected: success, zero warnings (a single-project build can pass while the solution fails on a narrowed type; this is the gate).

Run: `dotnet test --solution Capacitor.slnx`
Expected: every suite green. A daemon-suite timing test that fails alone-passes is environmental; re-run that class alone before blaming the diff.

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: prints nothing.

Run: `bash scripts/check-linear-ids.sh`
Expected: clean.

- [ ] **Step 3: Live smoke check (manual, read-only)**

With the app signed in to a server that lists a daemon on another machine: open one of its sessions from the rail. Expected: the Chat tab shows the transcript, sends reach the agent and echo back, a Claude/Codex session shows a Terminal tab whose pane replays and follows the source at the source's size, the seven keys act, and closing the workspace releases the viewport (the web viewer's terminal grows back). Nothing here posts to a real PR or mutates a session beyond the prompt you type.

- [ ] **Step 4: Commit**

```bash
git add docs/CHANGES.md README.md
git commit -m "Record the remote workspace's invariants (#806)"
```

Then open the PR per `.github/PULL_REQUEST_TEMPLATE.md`: title `Open a remote session's chat and terminal in the desktop app`, description reference line `Closes #806 — AI-2554`, and note that AI-2537 items 2 (`has_terminal` on the registry) and 1 (the pending-interrupts seed) stay open: the terminal tab is offered by vendor family until the registry says otherwise.

---

## Self-review notes

- **Spec coverage.** §5 chat read path (Tasks 2, 3, 5, 7), chat write path (6, 7), terminal with resize reporting and release (8, 9, 10), the `SubscribeToChat` snapshot plus `PendingInputChanged` queue (12); §6 authorization signals — throwing seed/stream, silent terminal attempted only after Established, re-establishment per reconnect, lane loss retains rows (5, 7, 9); §3 origin transitions with adapters rebuilt and no false end (11). `SubscribeToAcpEphemeral` live deltas are the spec's optional polish and are not planned. Attribution via `ResolveAttribution` is not planned: the chat rows carry no sender label today locally either, so nothing renders it; add it with the row that will.
- **Out of scope, stated:** `has_terminal` on the server registry (AI-2537 item 2) — the terminal tab is offered by vendor family, exactly like a local dto without the flag.
- **Type consistency.** `FeedRead.SnapshotOffset`, `ProjectedLine.Offset`, `IChatTranscriptFeed.CurrentOffset` are used with those names in Tasks 3, 5, 7 and 12; `IServerLane` members named in Task 4 are the ones Tasks 5, 6, 9 and 12 call; `RemoteSessionViewModel`'s constructor grows only at its tail (Task 7 base, Task 10 `surfaceFactory`), and every test harness passes arguments positionally in that order.

<!-- END OF PLAN -->
