# Subagents in the desktop chat — display (PR 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show a Claude session's subagents in the desktop app — a "N subagents running" strip above the composer and a SUBAGENTS section in the work-context pane — derived from the transcript alone, on the local and the remote lane alike.

**Architecture:** The Claude transcript leaf copies a single-result line's root `toolUseResult` into the `claude_code` extension (the shape kcap-server already persists); `Capacitor.Cli.Core` grows a `SubagentSignal` side-band on `ChatProjectionResult` that `ClaudeChatRules` fills from `Agent`/`Task` calls, `async_launched` results, task-notifications and `TaskStop` results. In the app, one `SessionSubagents` tracker per workspace folds those signals plus `tool_result` envelopes into `SubagentRow`s; `ChatTabViewModel` feeds it and shows the strip, `WorkContextViewModel` shares the same instance and lists the rows.

**Tech Stack:** .NET 10, NativeAOT for the CLI (`Capacitor.Models.Transcripts` and `Capacitor.Cli.Core` ship in it), Avalonia 11 + ReactiveUI for the app, TUnit on Microsoft Testing Platform, protobuf canonical events from `Kurrent.Agent.Schema` (`Struct`/`Value` extensions).

**Spec:** docs/superpowers/specs/2026-09-17-ai2840-desktop-subagents-design.md (decisions D2–D6). D7/D8 (daemon `LiveSubagents`, relay, bridge, clock, orchestrator, `IsWorking`, rail) are a separate plan: nothing here touches `RefreshActivityNote`, `SessionStatusDots`, `AgentStatusDto`, `AgentRow`, `ChatSessionInfo`, the rail, `src/Capacitor.Cli.Daemon` or `src/Capacitor.Cli/Commands`.

## Global Constraints

- **Worktree:** `/Users/alexey/dev/temp/kcap-cli/.capacitor/worktrees/agent-e90d8dfa163045`, branch `capacitor/agent-e90d8dfa163045`. Run every command from there; never `cd` out.
- **Names, exactly:** `ClaudeCodeExtension.ToolUseResult = "tool_use_result"`; `SubagentOutcome { Done, Failed, Stopped }`; `SubagentSignal` with nested `Started(CallId, Name, Description, At)`, `Detached(CallId, AgentId)`, `Finished(CallId?, AgentId?, Outcome, At)`; `ChatProjectionResult.Subagents`; `IChatDisplayRules.Subagents(CanonicalEvent evt, AcpEventEnvelope raw)`; `SessionSubagents`, `SubagentRow`, `SubagentState { Running, Done, Failed, Stopped }`; the XAML `Border` named `SubagentsBanner`; `ChatTabViewModel.HasRunningSubagents` / `SubagentSummary`; `WorkContextViewModel.Subagents` / `HasSubagents` / `SubagentsHeader` / `SubagentsExpanded` / `ToggleSubagentsCommand`.
- **Copy, verbatim:** strip text `1 subagent running` / `N subagents running`; section header `N running · M total` or `M total` when none run; row state texts `running · 6m 18s`, `2m 41s` (done), `failed · 48s`, `stopped · 1m 02s`, and a bare `stopped` for a running row under an ended session; duration is `Ns` under a minute, else `Mm SSs` with two-digit seconds; the faint tag reads `background`; spawn name is `subagent_type`, `agent` when absent.
- **Task-notification predicate:** `origin_kind == "task-notification"` **or** text opening (after leading whitespace) with `<task-notification>`; one static predicate serves `Filter`, `SubmittedInput` and `Subagents`. `TaskStop` gate is `task_type == "local_agent"`, a `task_id`, and a `message` opening `Successfully stopped task` — word for word.
- **No change to `AcpEventEnvelope`, `AcpToolKind`, `AcpEventKind`, local-control frames or capabilities.** No `SubagentStarted`/`SubagentCompleted` reading. Subagent facts ride `ChatProjectionResult.Subagents` only.
- **One type per file, named after the type.** The one exception used: `SubagentSignal.cs` holds the abstract record and its three nested records (a closely-related hierarchy). `SubagentOutcome`, `SubagentState`, `SubagentRow`, `SessionSubagents` each get their own file.
- **Comments are scarce and never historical:** no decision ids, no ticket ids, no "previously"/"now"/"moved". A comment names a trap or a constraint the source alone does not show, or is not written. When touching a neighbouring comment, shorten it under the same rule.
- **Commits:** subject is one imperative clause, at most 80 characters **including** the trailing ` (#966)`; optional body of at most five lines naming a constraint; last line `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Stage by explicit path. Never commit on `main`; never use bare `git stash`.
- **Build warning-free** every touched project after each task: `dotnet build src/Capacitor.Models.Transcripts/Capacitor.Models.Transcripts.csproj`, `dotnet build src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj`, `dotnet build src/Capacitor.App/Capacitor.App.csproj` (Avalonia XAML `AVLN` warnings count as failures), and the matching test project. After touching `Capacitor.Models.Transcripts` or `Capacitor.Cli.Core`: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` must print nothing.
- **Tests:** TUnit on MTP, one project at a time, always filtered: `dotnet run --project test/<Project>/<Project>.csproj -- --treenode-filter "/*/*/<ClassName>/*"` (never `--filter`). App tests that touch the dispatcher or a view wrap the body in `AvaloniaSession.RunOnUiAsync(async () => { … })` (imported via `using static Capacitor.App.Tests.Unit.AvaloniaSession;`) and carry `[NotInParallel("AvaloniaSession")]` on the method (or on the class, as `RemoteSessionViewModelTests` does with `[NotInParallel(nameof(AvaloniaSession))]`). A plain `ReactiveObject` needs neither (see `AttachmentTrayTests`). `TempDir` comes from Helpers via `[TempDir] public required TempDir Tmp { get; init; }`; files come from `Tmp.CreateFile(name, lines)`. Fake time is `Microsoft.Extensions.Time.Testing.FakeTimeProvider`.
- **Internals:** `Capacitor.App` grants `InternalsVisibleTo` to `Capacitor.App.Tests.Unit`; `Capacitor.Cli.Core` to `Capacitor.Cli.Core.Tests.Unit` and `Capacitor.App.Tests.Unit`; `Capacitor.Models.Transcripts` to its own test project. New members here are public anyway.
- **Global usings** in every test project: `Capacitor.Tests.Helpers`, `Capacitor.Models.Transcripts`. `Capacitor.Cli.Core` and `Capacitor.App` themselves import `Capacitor.Models.Transcripts` globally too, so `JsonElementExtensions` (`Str`, `Obj`, `Arr`, `Bool`) and `SchemaExtensions` need no import in Core.
- **Fixture shapes, verified from a live session** (reuse verbatim, only the ids vary):
  - spawning call: `{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}`
  - background launch: `{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}`
  - finish: `{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}` — the remote lane's copy has no `origin`.
  - `TaskStop` success: `toolUseResult` = `{"task_id":"a9f262478e032f427","task_type":"local_agent","message":"Successfully stopped task: a9f262478e032f427"}`; a `TaskOutput` probe carries the same `task_id`/`task_type` with another `message`.
  - server-written events (remote lane): `ToolResultReceived` payload `{"call_id":"toolu_A","result":"Async agent launched successfully.","extensions":{"claude_code":{"tool_use_result":{…the same object…}}}}`; the notification is a `UserMessageReceived` with the text above and no `origin_kind`.

---

## File Structure

**`src/Capacitor.Models.Transcripts/Harness/Claude/`**
- `ClaudeCodeExtension.cs` — modify: the `ToolUseResult` key; `Flags` takes the optional `Struct`.
- `ClaudeTranscriptEvents.cs` — modify: a single-result line's root `toolUseResult` object rides its result's extension.

**`src/Capacitor.Cli.Core/`**
- `SubagentOutcome.cs` — create: `enum SubagentOutcome { Done, Failed, Stopped }`.
- `SubagentSignal.cs` — create: the abstract record and its `Started`/`Detached`/`Finished` cases.
- `ChatProjectionResult.cs` — modify: third member `Subagents`.
- `IChatTranscriptProjection.cs` — modify: the default `ProjectWithInputs` builds the three-member result with no signals.
- `TranscriptChat.cs` — modify: `IChatDisplayRules.Subagents` default; `TranscriptChat.Project` and `TranscriptChatProjection.ProjectWithInputs` collect signals.
- `Harness/Claude/ClaudeChatRules.cs` — modify: the shared task-notification predicate; the four derivations; `<tool-use-id>`, `<task-id>`, `<status>` readers.

**`src/Capacitor.App/`**
- `ViewModels/SubagentState.cs` — create: the presented state enum.
- `ViewModels/SubagentRow.cs` — create: one subagent as the sidebar shows it (`ReactiveObject`).
- `ViewModels/SessionSubagents.cs` — create: the per-workspace tracker.
- `ViewModels/ChatTabViewModel.cs` — modify: takes the tracker; feeds it in `Apply`; clears on reset and feed switch; `SessionOver` from `OnSession`; ticks; `HasRunningSubagents`, `SubagentSummary`.
- `ViewModels/RemoteTranscriptFeed.cs` — modify: keeps a signal-only result.
- `ViewModels/WorkspaceViewModel.cs` — modify: creates the tracker, hands it to the chat and the pane.
- `ViewModels/RemoteSessionViewModel.cs` — modify: creates one for its chat.
- `Views/ChatTabView.axaml` — modify: the `SubagentsBanner` row.
- `ViewModels/WorkContextViewModel.cs`, `ViewModels/WorkContextViewModel.Projections.cs` — modify: the section's properties and toggle.
- `Views/WorkContextView.axaml` — modify: the SUBAGENTS section above SESSION.

**Tests**
- `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs` — modify.
- `test/Capacitor.Cli.Core.Tests.Unit/TranscriptChatCanonicalTests.cs`, `Harness/Claude/ClaudeChatRulesTests.cs`, `Harness/Codex/CodexChatRulesTests.cs` — modify.
- `test/Capacitor.App.Tests.Unit/SessionSubagentsTests.cs` — create.
- `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, `ChatTabViewSmokeTests.cs`, `ChatComposerTests.cs`, `ChatAttachmentsTests.cs`, `RemoteTranscriptFeedTests.cs`, `RemoteSessionViewModelTests.cs`, `WorkContextViewModelTests.cs`, `WorkContextViewSmokeTests.cs` — modify.

**Docs** — `docs/CHANGES.md` (new entry at the top, existing format), `README.md` desktop section.

---

### Task 1: The leaf carries a single-result line's `toolUseResult`

**Files:**
- Modify: `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeCodeExtension.cs` (whole file), `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeTranscriptEvents.cs` (lines 89–103, the `ProjectUser` block loop, plus one helper beside `HasTextBlock`)
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs`

**Interfaces:**
- Consumes: `TranscriptText.StructOf(JsonElement obj)` (existing), `SchemaExtensions.Slug(object payload, string slug)` (existing).
- Produces: `public const string ClaudeCodeExtension.ToolUseResult = "tool_use_result"`; `public static Struct? ClaudeCodeExtension.Flags(bool isSidechain, bool isMeta = false, string? originKind = null, bool isError = false, Struct? toolUseResult = null)`. On a `ToolResultReceived` projected from a line with exactly one `tool_result` block and an object-valued root `toolUseResult`, `SchemaExtensions.Slug(payload, "claude_code")!.Fields["tool_use_result"].StructValue` is that object.

- [ ] **Step 1: Write the failing tests**

Append inside `ClaudeTranscriptEventsTests` (before the closing brace of the class):

```csharp
    const string LaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";

    [Test]
    public async Task A_single_result_lines_toolUseResult_object_rides_the_extension_unchanged() {
        var e = E(LaunchLine);
        await Assert.That(e).Count().IsEqualTo(1);
        var slug = SchemaExtensions.Slug(e[0].Payload, "claude_code");
        await Assert.That(slug).IsNotNull();
        var result = slug!.Fields["tool_use_result"].StructValue;
        await Assert.That(result.Fields["status"].StringValue).IsEqualTo("async_launched");
        await Assert.That(result.Fields["agentId"].StringValue).IsEqualTo("a9f262478e032f427");
        await Assert.That(result.Fields["isAsync"].BoolValue).IsTrue();
        await Assert.That(result.Fields["description"].StringValue).IsEqualTo("Map desktop chat UI surfaces");
        await Assert.That(result.Fields["prompt"].StringValue).IsEqualTo("go");
        await Assert.That(result.Fields.Count).IsEqualTo(5);
        await Assert.That(((ToolResultReceived)e[0].Payload).Result).IsEqualTo("Async agent launched successfully.");
    }

    [Test]
    public async Task A_line_without_toolUseResult_or_with_a_non_object_one_adds_no_key() {
        var none = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");
        await Assert.That(SchemaExtensions.Slug(none[0].Payload, "claude_code")).IsNull();

        foreach (var value in new[] { "\"text\"", "42", "[1]", "null", "true" }) {
            var e = E($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{{{value}}}}""");
            await Assert.That(SchemaExtensions.Slug(e[0].Payload, "claude_code")).IsNull().Because(value);
        }

        var error = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"x","is_error":true}]},"toolUseResult":"Error: x"}""");
        var slug = SchemaExtensions.Slug(error[0].Payload, "claude_code");
        await Assert.That(SchemaExtensions.Flag(slug, "is_error")).IsTrue();
        await Assert.That(slug!.Fields.ContainsKey("tool_use_result")).IsFalse();
    }

    [Test]
    public async Task A_multi_result_line_puts_toolUseResult_on_no_result() {
        var e = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"a"},{"type":"tool_result","tool_use_id":"t2","content":"b"}]},"toolUseResult":{"status":"async_launched","agentId":"x"}}""");
        await Assert.That(e).Count().IsEqualTo(2);
        await Assert.That(SchemaExtensions.Slug(e[0].Payload, "claude_code")).IsNull();
        await Assert.That(SchemaExtensions.Slug(e[1].Payload, "claude_code")).IsNull();
    }

    /// The ids hash the record id and block index only, so the extension can never move one.
    [Test]
    public async Task The_tool_use_result_extension_leaves_event_ids_alone() {
        var withUuid = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":[{"type":"text","text":"x"},{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{"status":"async_launched","agentId":"x"}}""");
        await Assert.That(withUuid[0].EventId).IsEqualTo(Guid.Parse(Uuid));
        await Assert.That(SchemaExtensions.Slug(withUuid[0].Payload, "claude_code")).IsNotNull();

        const string fallback = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{"status":"async_launched","agentId":"x"}}""";
        await Assert.That(E(fallback, 9)[0].EventId).IsEqualTo(TranscriptIds.ClaudeFallback(9, fallback));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeTranscriptEventsTests/*"`
Expected: `A_single_result_lines_toolUseResult_object_rides_the_extension_unchanged` and `The_tool_use_result_extension_leaves_event_ids_alone` fail (slug is null / `tool_use_result` key missing); the other two pass already (they pin what must not change).

- [ ] **Step 3: Add the key and the optional Struct to `ClaudeCodeExtension`**

Replace the whole file:

```csharp
// src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeCodeExtension.cs
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Models.Transcripts.Harness.Claude;

/// The claude_code extension slug: the coding-agent fields the schema keeps out of the canonical
/// payloads. Only the fields this projection writes are named here.
public static class ClaudeCodeExtension {
    public const string Slug          = "claude_code";
    public const string IsMeta        = "is_meta";
    public const string IsSidechain   = "is_sidechain";
    public const string OriginKind    = "origin_kind";
    public const string IsError       = "is_error";
    /// A tool-result line's root `toolUseResult` object, verbatim; the key the server writes.
    public const string ToolUseResult = "tool_use_result";

    /// The block for one event, or null when nothing is set so the slug stays absent.
    public static Struct? Flags(bool isSidechain, bool isMeta = false, string? originKind = null, bool isError = false, Struct? toolUseResult = null) {
        if (!isSidechain && !isMeta && originKind is null && !isError && toolUseResult is null) return null;
        var s = new Struct();
        if (isSidechain) s.Fields[IsSidechain] = Value.ForBool(true);
        if (isMeta) s.Fields[IsMeta]           = Value.ForBool(true);
        if (originKind is not null) s.Fields[OriginKind] = Value.ForString(originKind);
        if (isError) s.Fields[IsError]         = Value.ForBool(true);
        if (toolUseResult is not null) s.Fields[ToolUseResult] = Value.ForStruct(toolUseResult);
        return s;
    }
}
```

- [ ] **Step 4: Attribute the object to the line's only result in `ClaudeTranscriptEvents.ProjectUser`**

Replace lines 89–103 (from `var texts = new List<string>();` through the end of the `foreach` over blocks) with:

```csharp
        var texts = new List<string>();
        var index = 0;
        var sawResult = false;
        // The root object describes one result and names none: on a line with several it is
        // attributed to none of them.
        var toolUseResult = CountResults(blocks) == 1 ? root.Obj("toolUseResult") : null;
        foreach (var block in blocks.EnumerateArray()) {
            switch (block.Str("type")) {
                case "tool_result":
                    sawResult = true;
                    emitter.Add(index, ToolResult(block, record), ClaudeCodeExtension.Flags(
                        record.IsSidechain, isError: block.Bool("is_error") == true,
                        toolUseResult: toolUseResult is { } obj ? StructOf(obj) : null));
                    break;
                case "text":
                    if (block.Str("text") is { } t) texts.Add(t);
                    break;
            }
            index++;
        }
```

Add the helper next to `HasTextBlock`:

```csharp
    static int CountResults(JsonElement blocks) {
        var count = 0;
        foreach (var block in blocks.EnumerateArray()) if (block.Str("type") == "tool_result") count++;
        return count;
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit/Capacitor.Models.Transcripts.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeTranscriptEventsTests/*"`
Expected: all pass (the existing 17 plus 4 new).

- [ ] **Step 6: AOT check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeCodeExtension.cs \
        src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeTranscriptEvents.cs \
        test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeTranscriptEventsTests.cs
git commit -m "Carry a tool-result line's toolUseResult in the claude_code extension (#966)" \
  -m "Only a line with one tool_result block gets it: the root object names no result, so on a line with several it belongs to none." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Subagent signals ride beside the rows

**Files:**
- Create: `src/Capacitor.Cli.Core/SubagentOutcome.cs`, `src/Capacitor.Cli.Core/SubagentSignal.cs`
- Modify: `src/Capacitor.Cli.Core/ChatProjectionResult.cs` (whole file), `src/Capacitor.Cli.Core/IChatTranscriptProjection.cs` (lines 8–11), `src/Capacitor.Cli.Core/TranscriptChat.cs` (lines 7–14, 23–34, 56–70)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/TranscriptChatCanonicalTests.cs`

**Interfaces:**
- Consumes: `TranscriptEnvelopes.From(CanonicalEvent)`, `AcpEventEnvelope`, `CanonicalEvent` (existing).
- Produces:
  - `public enum SubagentOutcome { Done, Failed, Stopped }`
  - `public abstract record SubagentSignal` with `public sealed record Started(string CallId, string Name, string Description, DateTimeOffset At) : SubagentSignal`, `public sealed record Detached(string CallId, string AgentId) : SubagentSignal`, `public sealed record Finished(string? CallId, string? AgentId, SubagentOutcome Outcome, DateTimeOffset At) : SubagentSignal` — used as `SubagentSignal.Started` etc.
  - `public sealed record ChatProjectionResult(IReadOnlyList<AcpEventEnvelope> Envelopes, IReadOnlyList<string> SubmittedInputs, IReadOnlyList<SubagentSignal> Subagents)`
  - `IReadOnlyList<SubagentSignal> IChatDisplayRules.Subagents(CanonicalEvent evt, AcpEventEnvelope raw)` — default `[]`; called for every envelope of an event, whatever `Filter` decided.
  - `TranscriptChat.Project(CanonicalEvent, IChatDisplayRules?)` and `TranscriptChatProjection.ProjectWithInputs(...)` fill `Subagents`; the default `IChatTranscriptProjection.ProjectWithInputs` yields `[]`.

- [ ] **Step 1: Write the failing tests**

Append inside `TranscriptChatCanonicalTests`:

```csharp
    sealed class SignallingRules : IChatDisplayRules {
        public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) =>
            envelope.Kind == AcpEventKind.ToolCall ? null : envelope;

        public IReadOnlyList<SubagentSignal> Subagents(CanonicalEvent evt, AcpEventEnvelope raw) =>
            raw.Kind == AcpEventKind.ToolCall ? [new SubagentSignal.Started(raw.ToolCallId!, "explore", "look", evt.Timestamp)] : [];
    }

    sealed class SilentRules : IChatDisplayRules {
        public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) => envelope;
    }

    static AssistantToolCallsGenerated Calls(params string[] ids) {
        var calls = new AssistantToolCallsGenerated();
        foreach (var id in ids) calls.ToolCalls.Add(new ToolCallInfo { CallId = id, ToolName = "Agent", Arguments = new Struct() });
        return calls;
    }

    [Test]
    public async Task Signals_ride_beside_the_rows_and_a_hidden_row_still_yields_its_signal() {
        var evt = Event(CanonicalEventTypes.AssistantToolCallsGenerated, Calls("t1"));
        var result = TranscriptChat.Project(evt, new SignallingRules());
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.Subagents).Count().IsEqualTo(1);
        var started = (SubagentSignal.Started)result.Subagents[0];
        await Assert.That(started.CallId).IsEqualTo("t1");
        await Assert.That(started.Name).IsEqualTo("explore");
        await Assert.That(started.At).IsEqualTo(evt.Timestamp);
    }

    [Test]
    public async Task Rules_without_an_override_and_no_rules_yield_no_signals() {
        var evt = Event(CanonicalEventTypes.AssistantToolCallsGenerated, Calls("t1"));
        await Assert.That(TranscriptChat.Project(evt, new SilentRules()).Subagents).IsEmpty();
        await Assert.That(TranscriptChat.Project(evt, rules: null).Subagents).IsEmpty();
        await Assert.That(TranscriptChat.Project(evt, rules: null).Envelopes).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ProjectWithInputs_collects_the_signals_of_every_event_on_a_line() {
        var chat = new TranscriptChatProjection(ClaudeTranscriptEvents.Instance, new SignallingRules());
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Agent","input":{}},{"type":"text","text":"hi"},{"type":"tool_use","id":"t2","name":"Agent","input":{}}]}}""";
        var result = chat.ProjectWithInputs(line, 1, DateTimeOffset.UnixEpoch, chat.CreateContext("s", null));
        await Assert.That(result.Envelopes.Select(e => e.Kind)).IsEquivalentTo(new[] { AcpEventKind.AssistantText });
        await Assert.That(result.Subagents.Cast<SubagentSignal.Started>().Select(s => s.CallId)).IsEquivalentTo(new[] { "t1", "t2" });
    }

    [Test]
    public async Task The_default_ProjectWithInputs_and_the_journal_projection_yield_no_signals() {
        var journal = TranscriptChat.Journal;
        var line = EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: "c1", ToolName: "Agent", ToolInputJson: "{}"));
        var result = journal.ProjectWithInputs(line, 1, DateTimeOffset.UnixEpoch, journal.CreateContext("s", null));
        await Assert.That(result.Envelopes).Count().IsEqualTo(1);
        await Assert.That(result.SubmittedInputs).IsEmpty();
        await Assert.That(result.Subagents).IsEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/TranscriptChatCanonicalTests/*"`
Expected: build error — `SubagentSignal`, `IChatDisplayRules.Subagents` and `ChatProjectionResult.Subagents` do not exist.

- [ ] **Step 3: Create the outcome and the signals**

```csharp
// src/Capacitor.Cli.Core/SubagentOutcome.cs
namespace Capacitor.Cli.Core;

public enum SubagentOutcome { Done, Failed, Stopped }
```

```csharp
// src/Capacitor.Cli.Core/SubagentSignal.cs
namespace Capacitor.Cli.Core;

/// A subagent fact a vendor's rules read off one envelope, carried beside the rows rather than on
/// the wire envelope. Vendor-neutral: which tool names spawn a subagent stays in the rules.
public abstract record SubagentSignal {
    /// A tool call that spawns a subagent.
    public sealed record Started(string CallId, string Name, string Description, DateTimeOffset At) : SubagentSignal;

    /// The call's result was only a launch acknowledgement; the agent id is the subagent's handle
    /// from then on.
    public sealed record Detached(string CallId, string AgentId) : SubagentSignal;

    /// The subagent ended outside its tool result. At least one key is set.
    public sealed record Finished(string? CallId, string? AgentId, SubagentOutcome Outcome, DateTimeOffset At) : SubagentSignal;
}
```

- [ ] **Step 4: Widen the result and its producers**

```csharp
// src/Capacitor.Cli.Core/ChatProjectionResult.cs  (replace the whole file)
namespace Capacitor.Cli.Core;

/// What a feed hands the chat per projected line: the rows, plus the display facts that are not
/// rows. A local slash command can acknowledge input while its wrappers stay hidden, and a
/// subagent signal can come from a row the chat hides.
public sealed record ChatProjectionResult(
        IReadOnlyList<AcpEventEnvelope> Envelopes,
        IReadOnlyList<string>           SubmittedInputs,
        IReadOnlyList<SubagentSignal>   Subagents
    );
```

In `IChatTranscriptProjection.cs`, replace the default method body:

```csharp
    ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var envelopes = Project(line, lineNumber, receivedAt, context);
        return new(envelopes, envelopes.Where(e => e.Kind == AcpEventKind.UserMessage && e.Text is not null).Select(e => e.Text!).ToArray(), []);
    }
```

In `TranscriptChat.cs`, replace the interface (lines 6–14):

```csharp
/// A vendor's say over how its stored events read in the chat: drop one, or rewrite the envelope.
public interface IChatDisplayRules {
    AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope);

    /// Most receipts are visible user turns. Vendors may also record hidden command receipts;
    /// injected prompts and background notifications must never acknowledge submitted input.
    string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed) =>
        displayed is { Kind: AcpEventKind.UserMessage } user ? user.Text : null;

    /// Subagent facts read off the raw envelope, whatever Filter decides for it.
    IReadOnlyList<SubagentSignal> Subagents(CanonicalEvent evt, AcpEventEnvelope raw) => [];
}
```

Replace `TranscriptChatProjection.ProjectWithInputs` (lines 23–34):

```csharp
    public ChatProjectionResult ProjectWithInputs(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
        var result = projection.Project(line, lineNumber, receivedAt, context);
        if (result.Events.Count == 0) return new([], [], []);
        var shown = new List<AcpEventEnvelope>(result.Events.Count);
        var submitted = new List<string>();
        var subagents = new List<SubagentSignal>();
        foreach (var evt in result.Events) {
            var projected = TranscriptChat.Project(evt, rules);
            shown.AddRange(projected.Envelopes);
            submitted.AddRange(projected.SubmittedInputs);
            subagents.AddRange(projected.Subagents);
        }
        return new(shown, submitted, subagents);
    }
```

Replace `TranscriptChat.Project` (lines 56–70):

```csharp
    public static ChatProjectionResult Project(CanonicalEvent evt, IChatDisplayRules? rules) {
        var envelopes = TranscriptEnvelopes.From(evt);
        if (envelopes.Count == 0) return new([], [], []);
        var shown = new List<AcpEventEnvelope>(envelopes.Count);
        var submitted = new List<string>();
        var subagents = new List<SubagentSignal>();
        foreach (var envelope in envelopes) {
            var kept = rules is null ? envelope : rules.Filter(evt, envelope);
            if (kept is { } visible) shown.Add(visible);
            var text = rules is null
                ? kept is { Kind: AcpEventKind.UserMessage } user ? user.Text : null
                : rules.SubmittedInput(evt, envelope, kept);
            if (text is { Length: > 0 }) submitted.Add(text);
            if (rules is not null) subagents.AddRange(rules.Subagents(evt, envelope));
        }
        return new(shown, submitted, subagents);
    }
```

`RemoteTranscriptFeed` and `LocalTranscriptFeed` in the app read only `Envelopes`/`SubmittedInputs` and still compile; the remote feed's drop rule is widened in Task 5.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/TranscriptChatCanonicalTests/*"`
Expected: 8 passed.

Then: `dotnet build src/Capacitor.App/Capacitor.App.csproj` — expected: no errors, no warnings.

- [ ] **Step 6: AOT check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/SubagentOutcome.cs src/Capacitor.Cli.Core/SubagentSignal.cs \
        src/Capacitor.Cli.Core/ChatProjectionResult.cs src/Capacitor.Cli.Core/IChatTranscriptProjection.cs \
        src/Capacitor.Cli.Core/TranscriptChat.cs test/Capacitor.Cli.Core.Tests.Unit/TranscriptChatCanonicalTests.cs
git commit -m "Carry subagent signals beside the chat rows a line projects to (#966)" \
  -m "The signal is read off the raw envelope for every event, so a row the chat hides still yields it." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Claude's rules derive the four signals and recognise a notification by its text

**Files:**
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeChatRules.cs` (whole file)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeChatRulesTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexChatRulesTests.cs`

**Interfaces:**
- Consumes: `SubagentSignal.Started/Detached/Finished`, `SubagentOutcome` (Task 2); `ClaudeCodeExtension.ToolUseResult` (Task 1); `SchemaExtensions.Slug/Flag/Text`; `JsonElementExtensions.Str`.
- Produces: `ClaudeChatRules.Subagents(CanonicalEvent evt, AcpEventEnvelope raw)` yielding, for a non-sidechain event: `Started(callId, subagent_type ?? "agent", description ?? "", evt.Timestamp)` for a `tool_call` named `Agent` or `Task`; `Detached(callId, agentId)` for a `tool_result` whose `tool_use_result.status == "async_launched"` carries a non-empty `agentId`; `Finished(<tool-use-id>?, <task-id>?, Done|Failed, evt.Timestamp)` for a task-notification (`Failed` unless `<status>` is `completed`); `Finished(null, task_id, Stopped, evt.Timestamp)` for a `tool_result` whose `tool_use_result` has `task_type == "local_agent"`, a `task_id` and a `message` opening `Successfully stopped task`. `Filter` and `SubmittedInput` recognise a notification by `origin_kind` or by text.

- [ ] **Step 1: Write the failing rules tests**

Append inside `ClaudeChatRulesTests`:

```csharp
    static ChatProjectionResult R(string line) {
        var chat = TranscriptChat.For("claude")!;
        return chat.ProjectWithInputs(line, 1, Received, chat.CreateContext("a1", null));
    }

    const string AgentCall = """{"type":"assistant","timestamp":"2026-09-17T10:00:00Z","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string LaunchResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string NotificationText = "<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>";

    static string Notification(bool originKind, string status = "completed", string flags = "") =>
        $$$"""{"type":"user","timestamp":"2026-09-17T10:05:00Z",{{{(originKind ? "\"origin\":{\"kind\":\"task-notification\"}," : "")}}}{{{flags}}}"message":{"role":"user","content":"{{{NotificationText.Replace("\n", "\\n").Replace("\"", "\\\"").Replace("completed", status)}}}"}}""";

    [Test]
    public async Task Started_for_Agent_and_Task_calls_names_the_type_and_falls_back_to_agent() {
        var agent = R(AgentCall);
        await Assert.That(agent.Envelopes).Count().IsEqualTo(1);
        var started = (SubagentSignal.Started)agent.Subagents.Single();
        await Assert.That(started.CallId).IsEqualTo("toolu_A");
        await Assert.That(started.Name).IsEqualTo("Explore");
        await Assert.That(started.Description).IsEqualTo("Map desktop chat UI surfaces");
        await Assert.That(started.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));

        var task = R("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_B","name":"Task","input":{"description":"Review","prompt":"go"}}]}}""");
        var bare = (SubagentSignal.Started)task.Subagents.Single();
        await Assert.That(bare.CallId).IsEqualTo("toolu_B");
        await Assert.That(bare.Name).IsEqualTo("agent");
        await Assert.That(bare.Description).IsEqualTo("Review");
    }

    [Test]
    public async Task No_signal_for_a_sidechain_call_a_sidechain_notification_or_any_other_tool() {
        await Assert.That(R(AgentCall.Replace("\"type\":\"assistant\",", "\"type\":\"assistant\",\"isSidechain\":true,")).Subagents).IsEmpty();
        await Assert.That(R(Notification(originKind: true, flags: "\"isSidechain\":true,")).Subagents).IsEmpty();
        await Assert.That(R(LaunchResult.Replace("\"type\":\"user\",", "\"type\":\"user\",\"isSidechain\":true,")).Subagents).IsEmpty();
        await Assert.That(R("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Bash","input":{"command":"ls","subagent_type":"Explore"}}]}}""").Subagents).IsEmpty();
    }

    [Test]
    public async Task Detached_with_the_agent_id_only_for_an_async_launched_result() {
        var launch = R(LaunchResult);
        await Assert.That(launch.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.ToolResult);
        var detached = (SubagentSignal.Detached)launch.Subagents.Single();
        await Assert.That(detached.CallId).IsEqualTo("toolu_A");
        await Assert.That(detached.AgentId).IsEqualTo("a9f262478e032f427");

        await Assert.That(R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"done"}]},"toolUseResult":{"status":"completed","agentId":"a9f262478e032f427"}}""").Subagents).IsEmpty();
        await Assert.That(R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"done"}]},"toolUseResult":{"status":"async_launched"}}""").Subagents).IsEmpty();
        await Assert.That(R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"done"}]}}""").Subagents).IsEmpty();
    }

    /// Runs twice: identified by origin_kind as the local leaf writes it, and by text alone as the
    /// server's events present it. Both yield the note row, the finish and no submitted input.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Finished_from_a_notification_keyed_by_both_ids_and_failed_unless_completed(bool originKind) {
        var done = R(Notification(originKind));
        await Assert.That(done.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(done.Envelopes.Single().Text).IsEqualTo("**Agent \"Map desktop chat UI surfaces\" finished**");
        await Assert.That(done.SubmittedInputs).IsEmpty();
        var finished = (SubagentSignal.Finished)done.Subagents.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(finished.Outcome).IsEqualTo(SubagentOutcome.Done);
        await Assert.That(finished.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 5, 0, TimeSpan.Zero));

        var failed = (SubagentSignal.Finished)R(Notification(originKind, status: "killed")).Subagents.Single();
        await Assert.That(failed.Outcome).IsEqualTo(SubagentOutcome.Failed);
    }

    [Test]
    public async Task A_notification_marked_meta_yields_its_finish_but_neither_row_nor_input() {
        var meta = R(Notification(originKind: true, flags: "\"isMeta\":true,"));
        await Assert.That(meta.Envelopes).IsEmpty();
        await Assert.That(meta.SubmittedInputs).IsEmpty();
        await Assert.That(meta.Subagents.Single()).IsTypeOf<SubagentSignal.Finished>();
    }

    [Test]
    public async Task A_notification_recognised_by_text_alone_is_not_a_user_turn() {
        var byText = R(Notification(originKind: false));
        await Assert.That(byText.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(byText.SubmittedInputs).IsEmpty();
        var indented = R("""{"type":"user","message":{"content":"  \n<task-notification>\n<summary>done</summary>\n</task-notification>"}}""");
        await Assert.That(indented.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(indented.Subagents).IsEmpty();
        var mention = R("""{"type":"user","message":{"content":"what does <task-notification> mean?"}}""");
        await Assert.That(mention.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(mention.SubmittedInputs).IsEquivalentTo(new[] { "what does <task-notification> mean?" });
    }

    [Test]
    public async Task Stopped_from_a_TaskStop_success_result_and_nothing_from_a_TaskOutput_probe() {
        var stop = R("""{"type":"user","timestamp":"2026-09-17T10:07:00Z","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_S","content":"Successfully stopped task: a9f262478e032f427"}]},"toolUseResult":{"task_id":"a9f262478e032f427","task_type":"local_agent","message":"Successfully stopped task: a9f262478e032f427"}}""");
        var stopped = (SubagentSignal.Finished)stop.Subagents.Single();
        await Assert.That(stopped.CallId).IsNull();
        await Assert.That(stopped.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(stopped.Outcome).IsEqualTo(SubagentOutcome.Stopped);
        await Assert.That(stopped.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 7, 0, TimeSpan.Zero));

        var probe = R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_O","content":"…"}]},"toolUseResult":{"task_id":"a9f262478e032f427","task_type":"local_agent","message":"Task output (last 10 lines)","retrieval_status":"partial"}}""");
        await Assert.That(probe.Subagents).IsEmpty();
        var shell = R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_S","content":"Successfully stopped task: b1"}]},"toolUseResult":{"task_id":"b1","task_type":"local_bash","message":"Successfully stopped task: b1"}}""");
        await Assert.That(shell.Subagents).IsEmpty();
    }
```

Append inside `CodexChatRulesTests`:

```csharp
    [Test]
    public async Task Codex_rules_yield_no_subagent_signals() {
        var chat = TranscriptChat.For("codex")!;
        var result = chat.ProjectWithInputs(Item("""{"type":"function_call","name":"spawn_agent","call_id":"c1","arguments":"{\"task\":\"t\"}"}"""), 1, Received, chat.CreateContext("a1", null));
        await Assert.That(result.Envelopes).Count().IsEqualTo(1);
        await Assert.That(result.Subagents).IsEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeChatRulesTests/*"`
Expected: the six new Claude tests fail — `Subagents` is empty, and the text-only notification renders as a `UserMessage` with a submitted input. `Codex_rules_yield_no_subagent_signals` passes already (the default).

- [ ] **Step 3: Implement the rules**

Replace the whole file:

```csharp
// src/Capacitor.Cli.Core/Harness/Claude/ClaudeChatRules.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf.WellKnownTypes;

namespace Capacitor.Cli.Core.Harness.Claude;

/// What the chat hides or rewrites in Claude records: meta and sidechain records, the blocks
/// Claude Code injects around a user turn, and the finished-background-task record it injects as
/// if the user had spoken. Also where a subagent's launch, detachment and end are read.
public sealed partial class ClaudeChatRules : IChatDisplayRules {
    public static readonly ClaudeChatRules Instance = new();

    const string StoppedTaskMessage = "Successfully stopped task";

    ClaudeChatRules() { }

    public string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed) {
        if (raw.Kind != AcpEventKind.UserMessage) return null;
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)
            || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)
            || IsTaskNotification(slug, raw)) return null;

        var text = raw.Text ?? "";
        var name = CommandName().Match(text).Groups[1].Value.Trim();
        if (name.StartsWith('/')) {
            var args = CommandArgs().Match(text).Groups[1].Value.Trim();
            return args.Length == 0 ? name : $"{name} {args}";
        }
        return displayed is { Kind: AcpEventKind.UserMessage } user ? user.Text : null;
    }

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain) || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)) return null;

        switch (envelope.Kind) {
            case AcpEventKind.UserMessage: {
                if (IsTaskNotification(slug, envelope)) return TaskNotificationNote(envelope);
                var text = StripWrappers(envelope.Text ?? "");
                return text.Length == 0 ? null : envelope with { Text = text };
            }
            case AcpEventKind.ToolCall:
                return envelope with { ToolKind = ClaudeToolKinds.Of(envelope.ToolName) };
            case AcpEventKind.ToolResult:
                return envelope with { ToolIsError = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsError) };
            default:
                return envelope;
        }
    }

    /// A sidechain event is a subagent's own record, and a nested subagent is not the parent's
    /// row. The meta flag is not consulted: a hidden row still ends its subagent.
    public IReadOnlyList<SubagentSignal> Subagents(CanonicalEvent evt, AcpEventEnvelope raw) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain)) return [];

        switch (raw.Kind) {
            case AcpEventKind.ToolCall when raw.ToolName is "Agent" or "Task" && raw.ToolCallId is { Length: > 0 } callId: {
                var (name, description) = SpawnFacts(raw.ToolInputJson);
                return [new SubagentSignal.Started(callId, name, description, evt.Timestamp)];
            }
            case AcpEventKind.ToolResult when raw.ToolCallId is { Length: > 0 } callId && ToolUseResult(slug) is { } result: {
                if (SchemaExtensions.Text(result, "status") == "async_launched" && SchemaExtensions.Text(result, "agentId") is { Length: > 0 } agentId)
                    return [new SubagentSignal.Detached(callId, agentId)];
                // The message gate keeps a TaskGet or TaskOutput probe, which carries the same
                // task_id, from ending a running subagent.
                if (SchemaExtensions.Text(result, "task_type") == "local_agent"
                    && SchemaExtensions.Text(result, "task_id") is { Length: > 0 } taskId
                    && (SchemaExtensions.Text(result, "message") ?? "").StartsWith(StoppedTaskMessage, StringComparison.Ordinal))
                    return [new SubagentSignal.Finished(null, taskId, SubagentOutcome.Stopped, evt.Timestamp)];
                return [];
            }
            case AcpEventKind.UserMessage when IsTaskNotification(slug, raw): {
                var text = raw.Text ?? "";
                var callId = Tag(TaskToolUseId(), text);
                var agentId = Tag(TaskId(), text);
                if (callId is null && agentId is null) return [];
                var outcome = Tag(TaskStatus(), text) == "completed" ? SubagentOutcome.Done : SubagentOutcome.Failed;
                return [new SubagentSignal.Finished(callId, agentId, outcome, evt.Timestamp)];
            }
            default:
                return [];
        }
    }

    /// The server's events carry no origin_kind, so the text is the other way to know.
    static bool IsTaskNotification(Struct? slug, AcpEventEnvelope raw) =>
        SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind) == "task-notification"
        || (raw.Text ?? "").AsSpan().TrimStart().StartsWith("<task-notification>");

    static Struct? ToolUseResult(Struct? slug) =>
        slug is not null && slug.Fields.TryGetValue(ClaudeCodeExtension.ToolUseResult, out var v) && v.KindCase == Value.KindOneofCase.StructValue
            ? v.StructValue : null;

    static (string Name, string Description) SpawnFacts(string? inputJson) {
        if (inputJson is null) return ("agent", "");
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var input = doc.RootElement;
            return (input.Str("subagent_type") is { Length: > 0 } type ? type : "agent", input.Str("description") ?? "");
        } catch (JsonException) {
            return ("agent", "");
        }
    }

    static string? Tag(Regex tag, string text) =>
        tag.Match(text) is { Success: true } m && m.Groups[1].Value.Trim() is { Length: > 0 } value ? value : null;

    // System-attributed: the summary in bold, then the result as markdown; a notification with
    // neither shows whatever is left once the wrapper tags are gone.
    static AcpEventEnvelope? TaskNotificationNote(AcpEventEnvelope envelope) {
        var raw     = envelope.Text ?? "";
        var summary = Tag(TaskSummary(), raw) ?? "";
        var body    = Tag(TaskResult(), raw) ?? "";
        var parts   = new List<string>(2);
        if (summary.Length > 0) parts.Add($"**{summary}**");
        if (body.Length > 0) parts.Add(body);
        var text = parts.Count > 0 ? string.Join("\n\n", parts) : TaskWrapper().Replace(raw, "").Trim();
        return text.Length == 0 ? null : envelope with { Kind = AcpEventKind.SystemNote, Text = text };
    }

    /// Removes the blocks Claude Code injects around a user turn: reminders and slash-command
    /// echoes.
    internal static string StripWrappers(string text) => Wrappers().Replace(text, "").Trim();

    [GeneratedRegex(@"<command-name>(.*?)</command-name>", RegexOptions.Singleline)]
    private static partial Regex CommandName();

    [GeneratedRegex(@"<command-args>(.*?)</command-args>", RegexOptions.Singleline)]
    private static partial Regex CommandArgs();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex TaskSummary();

    [GeneratedRegex(@"<result>(.*?)</result>", RegexOptions.Singleline)]
    private static partial Regex TaskResult();

    [GeneratedRegex(@"<task-id>(.*?)</task-id>", RegexOptions.Singleline)]
    private static partial Regex TaskId();

    [GeneratedRegex(@"<tool-use-id>(.*?)</tool-use-id>", RegexOptions.Singleline)]
    private static partial Regex TaskToolUseId();

    [GeneratedRegex(@"<status>(.*?)</status>", RegexOptions.Singleline)]
    private static partial Regex TaskStatus();

    [GeneratedRegex(@"</?task-notification>")]
    private static partial Regex TaskWrapper();

    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-caveat)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Wrappers();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeChatRulesTests/*"`
Expected: all pass (the existing 13 plus 7 new, the `[Arguments]` one counted twice).

Then: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexChatRulesTests/*"` — expected: all pass.

- [ ] **Step 5: AOT check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Harness/Claude/ClaudeChatRules.cs \
        test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeChatRulesTests.cs \
        test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexChatRulesTests.cs
git commit -m "Read subagent launches, detachments and ends off Claude's records (#966)" \
  -m "A task-notification is known by origin_kind or by its opening tag: the server's events carry no origin_kind, and the remote lane otherwise shows the notification as the user's own turn." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: The `SessionSubagents` tracker

**Files:**
- Create: `src/Capacitor.App/ViewModels/SubagentState.cs`, `src/Capacitor.App/ViewModels/SubagentRow.cs`, `src/Capacitor.App/ViewModels/SessionSubagents.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionSubagentsTests.cs` (new)

**Interfaces:**
- Consumes: `ChatProjectionResult(Envelopes, SubmittedInputs, Subagents)`, `SubagentSignal.*`, `SubagentOutcome` (Task 2); `AcpEventEnvelope` (`Kind`, `ToolCallId`, `ToolIsError`, `TimestampIso`).
- Produces:
  - `public enum SubagentState { Running, Done, Failed, Stopped }`
  - `public sealed class SubagentRow : ReactiveObject` — `string CallId`, `string Name`, `string Description`, `DateTimeOffset StartedAt`, `DateTimeOffset? EndedAt`, `SubagentState Outcome` (the row's own state), `bool IsEnded`, `bool IsBackground`, `SubagentState State` (presented), `string StateText`, `bool IsRunning/IsDone/IsFailed/IsStopped`; `internal static string Duration(TimeSpan)`.
  - `public sealed class SessionSubagents(TimeProvider time)` — `IAvaloniaReadOnlyList<SubagentRow> Rows`, `int RunningCount`, `bool SessionOver { get; set; }`, `event Action? Changed`, `void Apply(ChatProjectionResult projection)`, `void Clear()`, `void Tick()`. `Changed` fires after any call that changed `RunningCount` or `Rows.Count`.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.App.Tests.Unit/SessionSubagentsTests.cs
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// The per-workspace tracker as a pure state machine over projection results: signals first,
/// then envelopes; an ended row is never reopened or re-ended.
public class SessionSubagentsTests {
    static readonly DateTimeOffset T0 = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);

    static FakeTimeProvider Clock() => new(T0);

    static ChatProjectionResult Signals(params SubagentSignal[] signals) => new([], [], signals);

    static ChatProjectionResult Result(string callId, bool isError = false, DateTimeOffset? at = null) =>
        new([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: callId, ToolIsError: isError, TimestampIso: at?.ToString("O"))], [], []);

    static ChatProjectionResult Mixed(IReadOnlyList<AcpEventEnvelope> envelopes, params SubagentSignal[] signals) => new(envelopes, [], signals);

    static SubagentSignal.Started Started(string callId, DateTimeOffset? at = null, string name = "Explore") =>
        new(callId, name, "Map the UI", at ?? T0);

    static SubagentSignal.Detached Detached(string callId, string agentId) => new(callId, agentId);

    static SubagentSignal.Finished Finished(string? callId, string? agentId, SubagentOutcome outcome = SubagentOutcome.Done, DateTimeOffset? at = null) =>
        new(callId, agentId, outcome, at ?? T0.AddMinutes(2));

    static SubagentRow Only(SessionSubagents s) => s.Rows.Single();

    [Test]
    public async Task A_foreground_start_and_its_result_make_one_done_row() {
        var s = new SessionSubagents(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Started("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");
        await Assert.That(Only(s).Description).IsEqualTo("Map the UI");
        await Assert.That(Only(s).IsBackground).IsFalse();
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Result("c1", at: T0.AddSeconds(48)));
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("48s");
        await Assert.That(changes).IsEqualTo(2);
    }

    [Test]
    public async Task A_background_launch_stays_running_past_its_result_until_the_notification() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(Only(s).IsBackground).IsTrue();
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "a1", at: T0.AddMinutes(2).AddSeconds(41))));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(s).StateText).IsEqualTo("2m 41s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_failed_result_and_a_failed_notification_read_as_failed() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Result("c1", isError: true, at: T0.AddSeconds(48)));
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Failed);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("failed · 48s");

        s.Apply(Signals(Detached("c2", "a2")));
        s.Apply(Signals(Finished("c2", "a2", SubagentOutcome.Failed, at: T0.AddSeconds(62))));
        await Assert.That(s.Rows[1].State).IsEqualTo(SubagentState.Failed);
        await Assert.That(s.Rows[1].StateText).IsEqualTo("failed · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task A_background_row_is_stopped_by_its_agent_id_alone() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Signals(Finished(null, "a1", SubagentOutcome.Stopped, at: T0.AddSeconds(62))));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped · 1m 02s");
        await Assert.That(s.RunningCount).IsEqualTo(0);
    }

    [Test]
    public async Task Duplicate_started_detached_and_finished_change_nothing() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c1", name: "Other")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).Name).IsEqualTo("Explore");

        s.Apply(Signals(Detached("c1", "a1"), Detached("c1", "a1")));
        s.Apply(Signals(Finished("c1", "a1", at: T0.AddSeconds(10))));
        s.Apply(Signals(Finished("c1", "a1", SubagentOutcome.Failed, at: T0.AddSeconds(20))));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(s).EndedAt).IsEqualTo(T0.AddSeconds(10));

        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Detached("c1", "a1")));
        s.Apply(Result("c1", isError: true));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Done);
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_signal_or_result_for_an_unknown_id_changes_nothing() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("zz", "a9"), Finished("zz", null), Finished(null, "a9"), Finished(null, null, SubagentOutcome.Stopped)));
        s.Apply(Result("zz"));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
        await Assert.That(Only(s).IsBackground).IsFalse();
    }

    static SessionSubagents SecondLaunch() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1", T0)));
        s.Apply(Signals(Detached("c1", "a")));
        s.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(1))));
        s.Apply(Signals(Started("c2", T0.AddMinutes(2))));
        s.Apply(Signals(Detached("c2", "a")));
        return s;
    }

    [Test]
    public async Task A_second_launch_of_the_same_agent_is_a_second_row_and_the_id_follows_it() {
        var byCall = SecondLaunch();
        await Assert.That(byCall.Rows).Count().IsEqualTo(2);
        await Assert.That(byCall.Rows[0].State).IsEqualTo(SubagentState.Done);
        await Assert.That(byCall.Rows[1].State).IsEqualTo(SubagentState.Running);
        byCall.Apply(Signals(Finished("c2", "a", at: T0.AddMinutes(3))));
        await Assert.That(byCall.Rows[1].State).IsEqualTo(SubagentState.Done);

        var byAgent = SecondLaunch();
        byAgent.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(byAgent.Rows[0].State).IsEqualTo(SubagentState.Done);
        await Assert.That(byAgent.Rows[0].EndedAt).IsEqualTo(T0.AddMinutes(1));
        await Assert.That(byAgent.Rows[1].State).IsEqualTo(SubagentState.Done);
        await Assert.That(byAgent.Rows[1].EndedAt).IsEqualTo(T0.AddMinutes(3));
    }

    [Test]
    public async Task After_a_second_launch_stale_signals_for_the_first_leave_the_second_running() {
        var repeated = SecondLaunch();
        repeated.Apply(Signals(Finished("c1", "a", at: T0.AddMinutes(3))));
        await Assert.That(repeated.Rows[1].State).IsEqualTo(SubagentState.Running);

        var delayed = SecondLaunch();
        delayed.Apply(Signals(Detached("c1", "a")));
        delayed.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(3))));
        await Assert.That(delayed.Rows[1].State).IsEqualTo(SubagentState.Done);

        var older = SecondLaunch();
        older.Apply(Signals(Finished(null, "a", at: T0.AddMinutes(1).AddSeconds(30))));
        await Assert.That(older.Rows[1].State).IsEqualTo(SubagentState.Running);
        await Assert.That(older.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_later_result_for_a_detached_call_finishes_or_fails_the_row() {
        var done = new SessionSubagents(Clock());
        done.Apply(Signals(Started("c1")));
        done.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        await Assert.That(done.RunningCount).IsEqualTo(1);
        done.Apply(Result("c1", at: T0.AddSeconds(5)));
        await Assert.That(Only(done).State).IsEqualTo(SubagentState.Done);
        await Assert.That(Only(done).EndedAt).IsEqualTo(T0.AddSeconds(5));

        var failed = new SessionSubagents(Clock());
        failed.Apply(Signals(Started("c1")));
        failed.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "a1")));
        failed.Apply(Result("c1", isError: true));
        await Assert.That(Only(failed).State).IsEqualTo(SubagentState.Failed);
    }

    [Test]
    public async Task Two_results_in_one_projection_result_one_detached_and_one_not() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        s.Apply(Mixed(
            [new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1"), new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c2")],
            Detached("c1", "a1")));
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.Rows[0].IsBackground).IsTrue();
        await Assert.That(s.Rows[1].State).IsEqualTo(SubagentState.Done);
        await Assert.That(s.RunningCount).IsEqualTo(1);
    }

    [Test]
    public async Task Clear_drops_the_rows_and_the_bindings() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1")));
        s.Apply(Signals(Detached("c1", "a1")));
        var changes = 0;
        s.Changed += () => changes++;
        s.Clear();
        await Assert.That(s.Rows).IsEmpty();
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Started("c2")));
        s.Apply(Signals(Finished(null, "a1")));
        await Assert.That(Only(s).State).IsEqualTo(SubagentState.Running);
    }

    [Test]
    public async Task Session_over_presents_running_rows_as_stopped_without_ending_them() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c1"), Started("c2")));
        var changes = 0;
        s.Changed += () => changes++;

        s.SessionOver = true;
        await Assert.That(s.RunningCount).IsEqualTo(0);
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Stopped);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("stopped");
        await Assert.That(s.Rows[0].Outcome).IsEqualTo(SubagentState.Running);
        await Assert.That(changes).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", null, at: T0.AddSeconds(30))));
        await Assert.That(s.Rows[0].State).IsEqualTo(SubagentState.Done);
        await Assert.That(s.Rows[0].StateText).IsEqualTo("30s");

        s.Apply(Signals(Started("c3")));
        await Assert.That(s.Rows[2].State).IsEqualTo(SubagentState.Stopped);
        await Assert.That(s.RunningCount).IsEqualTo(0);

        s.SessionOver = false;
        await Assert.That(s.Rows[1].State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.Rows[2].State).IsEqualTo(SubagentState.Running);
        await Assert.That(s.RunningCount).IsEqualTo(2);
        s.SessionOver = false;
        await Assert.That(changes).IsEqualTo(3);
    }

    [Test]
    public async Task Running_count_follows_the_rows_and_rows_keep_arrival_order() {
        var s = new SessionSubagents(Clock());
        s.Apply(Signals(Started("c2", name: "second"), Started("c1", name: "first"), Started("c3", name: "third")));
        await Assert.That(s.RunningCount).IsEqualTo(3);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
        s.Apply(Result("c1"));
        s.Apply(Result("c3", isError: true));
        await Assert.That(s.RunningCount).IsEqualTo(1);
        await Assert.That(s.Rows.Select(r => r.Name)).IsEquivalentTo(new[] { "second", "first", "third" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Elapsed_runs_on_the_clock_for_a_running_row_and_freezes_at_its_end() {
        var clock = Clock();
        var s = new SessionSubagents(clock);
        s.Apply(Signals(Started("c1", T0)));
        await Assert.That(Only(s).StateText).IsEqualTo("running · 0s");

        clock.Advance(TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(18));
        await Assert.That(Only(s).StateText).IsEqualTo("running · 0s");
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("running · 6m 18s");

        var raised = new List<string?>();
        Only(s).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        s.Apply(Result("c1", at: T0.AddMinutes(7)));
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");
        await Assert.That(raised).Contains(nameof(SubagentRow.State));
        await Assert.That(raised).Contains(nameof(SubagentRow.StateText));

        clock.Advance(TimeSpan.FromHours(1));
        s.Tick();
        await Assert.That(Only(s).StateText).IsEqualTo("7m 00s");

        var unstamped = new SessionSubagents(clock);
        unstamped.Apply(Signals(Started("c9", clock.GetUtcNow())));
        clock.Advance(TimeSpan.FromSeconds(9));
        unstamped.Apply(Result("c9"));
        await Assert.That(Only(unstamped).StateText).IsEqualTo("9s");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionSubagentsTests/*"`
Expected: build error — `SessionSubagents`, `SubagentRow`, `SubagentState` do not exist.

- [ ] **Step 3: Create the state and the row**

```csharp
// src/Capacitor.App/ViewModels/SubagentState.cs
namespace Capacitor.App.ViewModels;

public enum SubagentState { Running, Done, Failed, Stopped }
```

```csharp
// src/Capacitor.App/ViewModels/SubagentRow.cs
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent as the sidebar shows it. Outcome is the row's own state; State is what it
/// presents, which reads Stopped for a still-running row once the session is over.
public sealed class SubagentRow : ReactiveObject {
    bool _isBackground;
    SubagentState _state = SubagentState.Running;
    string _stateText = "";

    public SubagentRow(string callId, string name, string description, DateTimeOffset startedAt) {
        CallId = callId;
        Name = name;
        Description = description;
        StartedAt = startedAt;
    }

    public string CallId { get; }
    public string Name { get; }
    public string Description { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public SubagentState Outcome { get; private set; } = SubagentState.Running;
    public bool IsEnded => Outcome != SubagentState.Running;

    public bool IsBackground { get => _isBackground; private set => this.RaiseAndSetIfChanged(ref _isBackground, value); }

    public SubagentState State {
        get => _state;
        private set {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsRunning));
            this.RaisePropertyChanged(nameof(IsDone));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsStopped));
        }
    }

    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }

    public bool IsRunning => State == SubagentState.Running;
    public bool IsDone    => State == SubagentState.Done;
    public bool IsFailed  => State == SubagentState.Failed;
    public bool IsStopped => State == SubagentState.Stopped;

    internal void MarkBackground() => IsBackground = true;

    internal void End(SubagentState outcome, DateTimeOffset at) {
        Outcome = outcome;
        EndedAt = at;
    }

    internal void Present(bool sessionOver, DateTimeOffset now) {
        var stoppedBySession = sessionOver && !IsEnded;
        State = stoppedBySession ? SubagentState.Stopped : Outcome;
        // Nothing dates an end the session imposed, so that row shows no duration.
        StateText = stoppedBySession ? "stopped" : Text(State, (EndedAt ?? now) - StartedAt);
    }

    static string Text(SubagentState state, TimeSpan elapsed) => state switch {
        SubagentState.Running => $"running · {Duration(elapsed)}",
        SubagentState.Failed  => $"failed · {Duration(elapsed)}",
        SubagentState.Stopped => $"stopped · {Duration(elapsed)}",
        _                     => Duration(elapsed),
    };

    internal static string Duration(TimeSpan elapsed) {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";
    }
}
```

- [ ] **Step 4: Create the tracker**

```csharp
// src/Capacitor.App/ViewModels/SessionSubagents.cs
using System.Globalization;
using Avalonia.Collections;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// The subagents of one session, folded from each projected line's signals and tool results.
/// Owned by the workspace and shared by the chat tab and the work-context pane; every call is
/// made on the UI thread. The rows rebuild from the log on a feed reset, so nothing here is
/// persisted.
public sealed class SessionSubagents(TimeProvider time) {
    readonly AvaloniaList<SubagentRow> _rows = new();
    readonly Dictionary<string, SubagentRow> _byCall = new(StringComparer.Ordinal);
    /// The row an agent id currently belongs to; the latest Detached wins.
    readonly Dictionary<string, SubagentRow> _byAgent = new(StringComparer.Ordinal);
    bool _sessionOver;
    int _rowsRaised;
    int _runningRaised;

    public IAvaloniaReadOnlyList<SubagentRow> Rows => _rows;
    public int RunningCount { get; private set; }

    /// Raised after any call that changed RunningCount or the row count.
    public event Action? Changed;

    /// The lane's verdict that nothing more will arrive: a view over the rows, not a transition,
    /// so a remote row coming back turns it off and its running rows read as running again.
    public bool SessionOver {
        get => _sessionOver;
        set {
            if (_sessionOver == value) return;
            _sessionOver = value;
            Refresh();
        }
    }

    public void Apply(ChatProjectionResult projection) {
        HashSet<string>? detachedHere = null;
        foreach (var signal in projection.Subagents) {
            switch (signal) {
                case SubagentSignal.Started started:
                    Start(started);
                    break;
                case SubagentSignal.Detached detached:
                    (detachedHere ??= new(StringComparer.Ordinal)).Add(detached.CallId);
                    Detach(detached);
                    break;
                case SubagentSignal.Finished finished:
                    Finish(finished);
                    break;
            }
        }
        foreach (var envelope in projection.Envelopes) {
            if (envelope.Kind != AcpEventKind.ToolResult || envelope.ToolCallId is not { } callId) continue;
            // The launch acknowledgement arrives beside its Detached and must not end the row; a
            // later result for the same call, an error included, does.
            if (detachedHere?.Contains(callId) == true) continue;
            if (_byCall.TryGetValue(callId, out var row) && !row.IsEnded)
                row.End(envelope.ToolIsError ? SubagentState.Failed : SubagentState.Done, Stamp(envelope.TimestampIso));
        }
        Refresh();
    }

    public void Clear() {
        _rows.Clear();
        _byCall.Clear();
        _byAgent.Clear();
        Refresh();
    }

    /// Re-reads the clock for the running rows' elapsed time.
    public void Tick() => Refresh();

    void Start(SubagentSignal.Started started) {
        if (_byCall.ContainsKey(started.CallId)) return;
        var row = new SubagentRow(started.CallId, started.Name, started.Description, started.At);
        _byCall[started.CallId] = row;
        _rows.Add(row);
    }

    void Detach(SubagentSignal.Detached detached) {
        if (!_byCall.TryGetValue(detached.CallId, out var row) || row.IsEnded) return;
        row.MarkBackground();
        _byAgent[detached.AgentId] = row;
    }

    /// A known call id decides alone and never falls through to the agent id: a repeated
    /// notification for a first execution must not end a second one holding the same id.
    void Finish(SubagentSignal.Finished finished) {
        var outcome = finished.Outcome switch {
            SubagentOutcome.Failed  => SubagentState.Failed,
            SubagentOutcome.Stopped => SubagentState.Stopped,
            _                       => SubagentState.Done,
        };
        if (finished.CallId is { } callId && _byCall.TryGetValue(callId, out var byCall)) {
            if (!byCall.IsEnded) byCall.End(outcome, finished.At);
            return;
        }
        // A completion dated before the row started belongs to an earlier execution.
        if (finished.AgentId is { } agentId && _byAgent.TryGetValue(agentId, out var byAgent)
            && !byAgent.IsEnded && finished.At >= byAgent.StartedAt)
            byAgent.End(outcome, finished.At);
    }

    DateTimeOffset Stamp(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at : time.GetUtcNow();

    void Refresh() {
        var now = time.GetUtcNow();
        var running = 0;
        foreach (var row in _rows) {
            row.Present(_sessionOver, now);
            if (row.State == SubagentState.Running) running++;
        }
        RunningCount = running;
        if (running == _runningRaised && _rows.Count == _rowsRaised) return;
        _runningRaised = running;
        _rowsRaised = _rows.Count;
        Changed?.Invoke();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionSubagentsTests/*"`
Expected: 14 passed. Then `dotnet build src/Capacitor.App/Capacitor.App.csproj` — no warnings.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/ViewModels/SubagentState.cs src/Capacitor.App/ViewModels/SubagentRow.cs \
        src/Capacitor.App/ViewModels/SessionSubagents.cs test/Capacitor.App.Tests.Unit/SessionSubagentsTests.cs
git commit -m "Track a session's subagents from its projected chat lines (#966)" \
  -m "Session end is a view over the rows rather than a transition: a late first read, a final-drain finish and a remote row that comes back all read right without a reopen." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: The chat tab feeds the tracker; feeds keep signal-only results

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (ctors at lines 313–327, fields near 42–44, `OnSession` 511–535, `SwitchFeed` 537–556, `OnTick` 586–596, `Apply` 609–707, `TeardownAsync` 802–819), `src/Capacitor.App/ViewModels/RemoteTranscriptFeed.cs` (line 197), `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (lines 113, 190–191), `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs` (lines 161–164)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, `RemoteTranscriptFeedTests.cs`, `RemoteSessionViewModelTests.cs`; construction-site fixes in `ChatTabViewSmokeTests.cs`, `ChatComposerTests.cs`, `ChatAttachmentsTests.cs`

**Interfaces:**
- Consumes: `SessionSubagents(TimeProvider)` with `Apply`, `Clear`, `Tick`, `SessionOver`, `RunningCount`, `Changed` (Task 4); `ChatProjectionResult.Subagents` (Task 2).
- Produces: `ChatTabViewModel(string agentId, IDaemonClientService daemon, ChatInput input, IAttachmentUploader uploader, IChatTranscriptProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions, SessionSubagents subagents, string? unavailableNote = null, IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null)` and `ChatTabViewModel(string agentId, AgentOrigin origin, IObservable<ChatSessionInfo> session, IObservable<string[]?> supportedVendors, ChatInput input, IAttachmentUploader uploader, Func<string, IChatTranscriptFeed>? openFeed, IUrlOpener opener, TimeProvider time, IPermissionService permissions, SessionSubagents subagents, string? unavailableNote = null, string? missingNote = null, IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null, IObservable<IReadOnlyList<QueuedInputItem>>? serverQueue = null)`; `public bool ChatTabViewModel.HasRunningSubagents`; `public string ChatTabViewModel.SubagentSummary` (`1 subagent running` / `N subagents running`), both raised on the tracker's `Changed`. `WorkspaceViewModel` holds one `SessionSubagents` local named `subagents` created before `WorkContext` and passed to the chat (Task 7 passes it to the pane).

- [ ] **Step 1: Write the failing chat-tab tests**

In `ChatTabViewModelTests`, add the fixtures after `ThinkingLine` (line 34):

```csharp
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string AgentLaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string AgentFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}""";
```

Change the `Harness` (lines 42–57) to own a tracker and pass it:

```csharp
    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public SessionSubagents Subagents { get; }
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(IChatTranscriptProjection? projection, Action<FakePermissionService>? seed = null,
                       ChatInput? input = null, string? unavailableNote = null) {
            seed?.Invoke(Permissions);
            Subagents = new SessionSubagents(Time);
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel(
                "a1", Daemon, input ?? new TerminalChatInput(Terminal, "a1", Daemon, new ScriptedLocalControlOps(), Observable.Never<AgentPresence>()), new NoAttachmentUploader(), projection, Opener, Time, Permissions, Subagents, unavailableNote);
        }
```

(the `PushAsync`/`TickAsync`/`TeardownAsync` members stay as they are.) Then append these tests before the `sealed class GatedProjection` (after `A_reset_and_a_path_switch_start_a_fresh_group_for_later_calls`):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fixture_transcripts_drive_the_strip_singular_and_plural() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var raised = new List<string?>();
            h.Chat.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();
            await Assert.That(h.Chat.SubagentSummary).IsEqualTo("1 subagent running");
            await Assert.That(h.Subagents.Rows.Single().IsBackground).IsTrue();
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.HasRunningSubagents));
            await Assert.That(raised).Contains(nameof(ChatTabViewModel.SubagentSummary));

            File.AppendAllText(path, AgentCallLine.Replace("toolu_A", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.SubagentSummary).IsEqualTo("2 subagents running");

            File.AppendAllText(path, AgentFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.SubagentSummary).IsEqualTo("1 subagent running");
            await Assert.That(h.Chat.Items.OfType<SystemNoteItem>().Count()).IsEqualTo(1);

            File.AppendAllText(path, ToolResultLine.Replace("t1", "toolu_B") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows.Select(r => r.State)).IsEquivalentTo(new[] { SubagentState.Done, SubagentState.Done }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_feed_reset_and_a_path_switch_empty_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();

            File.WriteAllLines(path, [UserLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows).IsEmpty();

            var other = Tmp.CreateFile("o.jsonl", [AgentCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.HasRunningSubagents).IsTrue();
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows).IsEmpty();
            await h.TeardownAsync();
        });
    }

    /// The status can land before the first read: rows projected after the session ended present
    /// as stopped at once, and a reset under the ended session rebuilds them stopped too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_terminal_status_before_the_first_read_and_a_reset_after_it_leave_no_running_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine, AgentLaunchLine]);
            await h.PushAsync(Dto(path) with { Status = "Completed" });
            await Assert.That(h.Subagents.Rows).Count().IsEqualTo(1);
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await Assert.That(h.Subagents.Rows.Single().StateText).IsEqualTo("stopped");

            File.WriteAllLines(path, [AgentCallLine.Replace("Map desktop chat UI surfaces", "Map")]);
            await h.TickAsync();
            await Assert.That(h.Subagents.Rows.Single().Description).IsEqualTo("Map");
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_generation_read_after_a_feed_switch_adds_no_subagent_rows() {
        await RunOnUiAsync(async () => {
            var gate = new TaskCompletionSource();
            var h = new Harness(new TranscriptChatProjection(new GatedProjection(ClaudeTranscriptEvents.Instance, "OLD", gate), ClaudeChatRules.Instance));
            var oldPath = Tmp.CreateFile("old.jsonl", [AgentCallLine.Replace("Map desktop chat UI surfaces", "OLD")]);
            var newPath = Tmp.CreateFile("new.jsonl", [AssistantLine]);

            h.Daemon.Agents.AddOrUpdate(Dto(oldPath));
            await (h.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var oldRead = h.Chat.PendingReadForTesting!;

            h.Daemon.Agents.AddOrUpdate(Dto(newPath));
            gate.SetResult();
            await oldRead;
            await (h.Chat.PendingReadForTesting ?? Task.CompletedTask);
            await h.TickAsync();

            await Assert.That(h.Subagents.Rows).IsEmpty();
            await Assert.That(h.Chat.HasRunningSubagents).IsFalse();
            await h.TeardownAsync();
        });
    }
```

- [ ] **Step 2: Write the failing feed tests**

In `RemoteTranscriptFeedTests.cs` add `using Capacitor.Cli.Core;` to the usings and append inside the class:

```csharp
    /// A notification marked meta yields no row and no input, only its finish; the feed must keep it.
    [Test]
    public async Task A_signal_only_projection_survives_the_feed() {
        using var h = new Harness(vendor: "claude");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>","extensions":{"claude_code":{"is_meta":true}}}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");

        var line = h.Feed.ReadAppended().Lines.Single();
        await Assert.That(line.Offset).IsEqualTo(2);
        await Assert.That(line.Projection.Envelopes).IsEmpty();
        await Assert.That(line.Projection.SubmittedInputs).IsEmpty();
        var finished = (SubagentSignal.Finished)line.Projection.Subagents.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(finished.Outcome).IsEqualTo(SubagentOutcome.Done);
    }
```

In `RemoteSessionViewModelTests.cs` append inside the class (the class carries `[NotInParallel(nameof(AvaloniaSession))]`, so the methods do not):

```csharp
    const string AgentCall = """{"tool_calls":[{"call_id":"toolu_A","tool_name":"Agent","arguments":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}""";
    const string AgentLaunch = """{"call_id":"toolu_A","result":"Async agent launched successfully.","extensions":{"claude_code":{"tool_use_result":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}}}""";
    const string AgentNotification = """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}""";
    const string MetaNotification = """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>","extensions":{"claude_code":{"is_meta":true}}}""";

    /// Events shaped as the server writes them: tool_use_result on the result, no origin_kind on
    /// the notification.
    [Test]
    public async Task Server_shaped_events_drive_the_strip_through_a_background_launch_and_its_finish() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningSubagents, "the strip");
            await Assert.That(vm.Chat.SubagentSummary).IsEqualTo("1 subagent running");
            await Assert.That(vm.Chat.Items.OfType<ToolGroupItem>().Single().Calls.Single().Outcome).IsEqualTo(ToolOutcome.Done);

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived, AgentNotification));
            await h.UntilAsync(vm, () => !vm.Chat.HasRunningSubagents, "the finish");
            await Assert.That(vm.Chat.Items.OfType<SystemNoteItem>().Count()).IsEqualTo(1);
            await Assert.That(vm.Chat.Items.OfType<UserTurnItem>().Any()).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_meta_notification_finishes_its_subagent_without_a_row() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningSubagents, "the strip");
            var rows = vm.Chat.Items.Count;

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived, MetaNotification));
            await h.UntilAsync(vm, () => !vm.Chat.HasRunningSubagents, "the finish");
            await Assert.That(vm.Chat.Items.Count).IsEqualTo(rows);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_row_removal_stops_the_strip_and_its_reappearance_restores_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningSubagents, "the strip");

            h.Directory.Rows.Remove("remote:a1");
            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.Chat.HasRunningSubagents).IsFalse();

            h.Directory.Rows.AddOrUpdate(Harness.Row(vendor: "claude"));
            await Assert.That(vm.SessionEnded).IsFalse();
            await Assert.That(vm.Chat.HasRunningSubagents).IsTrue();
            await Assert.That(vm.Chat.SubagentSummary).IsEqualTo("1 subagent running");
            await vm.TeardownAsync();
        });
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"`
Expected: build error — `ChatTabViewModel` has no `subagents` parameter, `HasRunningSubagents` or `SubagentSummary`.

- [ ] **Step 4: Take the tracker in `ChatTabViewModel`**

Add the field beside `_permissions` (line 44):

```csharp
    readonly IPermissionService _permissions;
    readonly SessionSubagents _subagents;
```

Replace both constructors' signatures and the delegation (lines 313–327):

```csharp
    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, ChatInput input, IAttachmentUploader uploader,
            IChatTranscriptProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions,
            SessionSubagents subagents, string? unavailableNote = null, IObservable<string?>? sessionId = null,
            IObservable<bool>? localDaemonOnAppServer = null)
        : this(agentId, AgentOrigin.Local, LocalSession(agentId, daemon), daemon.Snapshots.Select(s => s.Daemon.SupportedVendors),
               input, uploader, projection is null ? null : LocalFeed(agentId, projection, time), opener, time, permissions,
               subagents, unavailableNote, null, sessionId, localDaemonOnAppServer) { }

    public ChatTabViewModel(
            string agentId, AgentOrigin origin, IObservable<ChatSessionInfo> session, IObservable<string[]?> supportedVendors,
            ChatInput input, IAttachmentUploader uploader, Func<string, IChatTranscriptFeed>? openFeed, IUrlOpener opener, TimeProvider time,
            IPermissionService permissions, SessionSubagents subagents, string? unavailableNote = null, string? missingNote = null,
            IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null,
            IObservable<IReadOnlyList<QueuedInputItem>>? serverQueue = null) {
```

After `_permissions = permissions;` (line 336) add:

```csharp
        _subagents = subagents;
        _subagents.Changed += RefreshSubagents;
```

Add the two properties and the refresh after `RefreshQueue()` (line 104):

```csharp
    public bool HasRunningSubagents => _subagents.RunningCount > 0;
    public string SubagentSummary =>
        _subagents.RunningCount == 1 ? "1 subagent running" : $"{_subagents.RunningCount} subagents running";

    void RefreshSubagents() {
        this.RaisePropertyChanged(nameof(HasRunningSubagents));
        this.RaisePropertyChanged(nameof(SubagentSummary));
    }
```

In `OnSession`, after `_awaitingInput = info.AwaitingInput;` (line 524) add:

```csharp
        _subagents.SessionOver = info.Ended;
```

In `SwitchFeed`, after `_marked.Clear();` (line 542) add `_subagents.Clear();`.

In `OnTick`, after `RefreshActivityNote();` (line 592) add `_subagents.Tick();`.

In `Apply`, the `FeedStatus.Reset` case (lines 629–638) gains `_subagents.Clear();` after `_marked.Clear();`, and the per-line loop (line 660) feeds the tracker first:

```csharp
        foreach (var (projected, offset) in read.Lines) {
            _subagents.Apply(projected);
            foreach (var text in projected.SubmittedInputs) {
```

In `TeardownAsync`, before `_disposables.Dispose();` add `_subagents.Changed -= RefreshSubagents;`.

- [ ] **Step 5: Keep a signal-only result in the remote feed**

`RemoteTranscriptFeed.cs` line 197:

```csharp
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 && projected.Subagents.Count == 0 ? null : new(projected, offset);
```

- [ ] **Step 6: Create the tracker in the two hosts**

`WorkspaceViewModel.cs` — insert before line 113 (`WorkContext = new WorkContextViewModel(...)`):

```csharp
        var subagents = new SessionSubagents(time);
```

and replace lines 190–191:

```csharp
                Chat = new ChatTabViewModel(
                    agentId, daemon, input, uploader, projection, opener, time, permissions, subagents, note, sessionIds, localDaemonOnAppServer);
```

`RemoteSessionViewModel.cs` lines 161–164:

```csharp
        Chat = new ChatTabViewModel(
            row.Id, AgentOrigin.Remote, _session, Observable.Return<string[]?>(null), input, new NoAttachmentUploader(),
            key => new RemoteTranscriptFeed(key, row.Vendor, _accessStates, readDetail, lane, time, Log),
            opener, time, permissions, new SessionSubagents(time), missingNote: MissingNote, sessionId: _sessionIds,
```

- [ ] **Step 7: Fix every other construction site**

`test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`, `Host` (lines 89–117): add `public SessionSubagents Subagents { get; }` after `Permissions`, and in the ctor:

```csharp
        public Host(bool show = true) {
            Subagents = new SessionSubagents(Time);
            Terminal = new TerminalTabViewModel("a1", Daemon, Attach.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel(
                "a1", Daemon, new TerminalChatInput(Terminal, "a1", Daemon, new ScriptedLocalControlOps(), _presence), new NoAttachmentUploader(), TranscriptChat.For("claude"), Opener, Time, Permissions, Subagents);
```

`test/Capacitor.App.Tests.Unit/ChatComposerTests.cs` line 22–23:

```csharp
        var chat = new ChatTabViewModel(
            "a1", daemon, new TerminalChatInput(terminal, "a1", daemon, new ScriptedLocalControlOps(), Observable.Never<AgentPresence>()), new NoAttachmentUploader(), TranscriptChat.For("claude"), opener, time, new FakePermissionService(), new SessionSubagents(time));
```

and lines 152–154:

```csharp
            var chat = new ChatTabViewModel(
                "r1", daemon, new TerminalChatInput(terminal, "r1", daemon, new ScriptedLocalControlOps(), Observable.Never<AgentPresence>()), new NoAttachmentUploader(), TranscriptChat.For("claude"), new RecordingOpener(), time,
                new FakePermissionService(), new SessionSubagents(time));
```

`test/Capacitor.App.Tests.Unit/ChatAttachmentsTests.cs` lines 57–59:

```csharp
        public Harness(IChatTranscriptProjection? projection) =>
            Chat = new ChatTabViewModel(
                "a1", Daemon, Input, Uploader, projection, new RecordingOpener(), Time, new FakePermissionService(), new SessionSubagents(Time));
```

`test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, the four remote-style sites: at lines 1453–1455 and 1496–1498 replace `new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), serverQueue: queue);` with `new RecordingOpener(), new FakeTimeProvider(), new FakePermissionService(), new SessionSubagents(new FakeTimeProvider()), serverQueue: queue);`; at 1526–1528 replace `new RecordingOpener(), time, new FakePermissionService(), serverQueue: queue);` with `new RecordingOpener(), time, new FakePermissionService(), new SessionSubagents(time), serverQueue: queue);`; at 1559–1561 replace `new RecordingOpener(), time, new FakePermissionService());` with `new RecordingOpener(), time, new FakePermissionService(), new SessionSubagents(time));`.

- [ ] **Step 8: Run the tests to verify they pass**

Run, one at a time:
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"` — all pass (the four new among them).
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteTranscriptFeedTests/*"` — all pass.
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RemoteSessionViewModelTests/*"` — all pass.
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatComposerTests/*"`, `.../ChatAttachmentsTests/*`, `.../ChatTabViewSmokeTests/*`, `.../WorkspaceViewModelTests/*` — all still pass.
- `dotnet build src/Capacitor.App/Capacitor.App.csproj` and `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj` — no warnings.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatTabViewModel.cs src/Capacitor.App/ViewModels/RemoteTranscriptFeed.cs \
        src/Capacitor.App/ViewModels/WorkspaceViewModel.cs src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs \
        test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs test/Capacitor.App.Tests.Unit/RemoteTranscriptFeedTests.cs \
        test/Capacitor.App.Tests.Unit/RemoteSessionViewModelTests.cs test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs \
        test/Capacitor.App.Tests.Unit/ChatComposerTests.cs test/Capacitor.App.Tests.Unit/ChatAttachmentsTests.cs
git commit -m "Feed the chat tab's projected lines to the subagent tracker (#966)" \
  -m "The remote feed keeps a result that carries only signals: a notification marked meta yields no row, and dropping it would leave its subagent running forever." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: The strip above the composer

**Files:**
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml` (lines 21, 157–191)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ChatTabViewModel.HasRunningSubagents`, `SubagentSummary` (Task 5); the `toolRunning` pulse style and `toolStatus` class from `App.axaml`.
- Produces: a `Border` named `SubagentsBanner` in grid row 2 of `ChatTabView`, between `ChatActivityNote` (row 1) and `QueuedMessagesBanner` (row 3); `ComposerCard` moves to row 4.

- [ ] **Step 1: Write the failing smoke test**

Add the fixtures after `ReadResultLine` (line 42) in `ChatTabViewSmokeTests`:

```csharp
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string AgentLaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string AgentFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}}""";
```

Append inside the class:

```csharp
    /// The strip sits in its own row between the activity note and the queue banner, shows one
    /// line with the pulsing dot while anything runs, and leaves with the last finish.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_banner_is_hidden_at_zero_and_shows_the_summary_above_the_queue_banner() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var banner = host.View.FindControl<Border>("SubagentsBanner")!;
            var queued = host.View.FindControl<Border>("QueuedMessagesBanner")!;
            var note = host.View.FindControl<StackPanel>("ChatActivityNote")!;
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(Grid.GetRow(note)).IsLessThan(Grid.GetRow(banner));
            await Assert.That(Grid.GetRow(banner)).IsLessThan(Grid.GetRow(queued));
            await Assert.That(Grid.GetRow(queued)).IsLessThan(Grid.GetRow(host.View.FindControl<Border>("ComposerCard")!));

            var path = Tmp.CreateFile("sub.jsonl", [AgentCallLine, AgentLaunchLine]);
            await host.LoadAsync(path);
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "1 subagent running" && t.IsEffectivelyVisible)).IsTrue();
            await Assert.That(banner.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsTrue();
            await Assert.That(queued.IsVisible).IsFalse();

            await host.AppendLinesAndTickAsync(path, AgentFinishLine);
            await Assert.That(banner.IsVisible).IsFalse();
            await host.CloseAsync();
        });
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewSmokeTests/The_subagents_banner*"`
Expected: fails — `FindControl<Border>("SubagentsBanner")` returns null (NullReferenceException on `banner.IsVisible`).

- [ ] **Step 3: Add the row**

In `ChatTabView.axaml`, line 21: `<Grid RowDefinitions="*,Auto,Auto,Auto,Auto">`.

Insert after the `ChatActivityNote` `StackPanel` (after line 163):

```xml
        <!-- One line however many run; the names are the sidebar's. -->
        <Border x:Name="SubagentsBanner" Grid.Row="2" Margin="22,4,22,0" Padding="13,10"
                Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}"
                BorderThickness="1" CornerRadius="8" IsVisible="{Binding HasRunningSubagents}">
            <StackPanel Orientation="Horizontal" Spacing="8">
                <Border Classes="toolRunning toolStatus" Width="8" Height="8" CornerRadius="4" VerticalAlignment="Center"
                        Background="{StaticResource KcapWarningBrush}" />
                <TextBlock x:Name="SubagentSummaryText" Text="{Binding SubagentSummary}" FontSize="12" FontWeight="SemiBold"
                           Foreground="{StaticResource KcapTextBrush}" VerticalAlignment="Center" />
            </StackPanel>
        </Border>
```

Then change `QueuedMessagesBanner` to `Grid.Row="3"` (line 165) and `ComposerCard` to `Grid.Row="4"` (line 191).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewSmokeTests/*"`
Expected: all pass. Then `dotnet build src/Capacitor.App/Capacitor.App.csproj` — no `AVLN` warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Views/ChatTabView.axaml test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs
git commit -m "Show a running-subagents strip above the chat composer (#966)" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: The SUBAGENTS section in the work-context pane

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkContextViewModel.cs` (usings; fields near 48–58; ctor 143–180; `TeardownAsync` 299–313), `src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs` (lines 124–139), `src/Capacitor.App/Views/WorkContextView.axaml` (styles at 125–131; the block between the divider at line 331 and `SessionToggle` at 334), `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (line 113)
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs`, `WorkContextViewSmokeTests.cs`, `WorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: `SessionSubagents` (`Rows`, `RunningCount`, `Changed`, `Apply`) and `SubagentRow` (`Name`, `Description`, `IsBackground`, `StateText`, `IsRunning/IsDone/IsFailed/IsStopped`) from Task 4; the `subagents` local in `WorkspaceViewModel` from Task 5.
- Produces: `WorkContextViewModel(IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener, SessionSubagents subagents, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null, Action<string>? openWorkItem = null)`; `IAvaloniaReadOnlyList<SubagentRow> Subagents`; `bool HasSubagents`; `string SubagentsHeader`; `bool SubagentsExpanded` (starts true); `ReactiveCommand<Unit, Unit> ToggleSubagentsCommand`. XAML names: `SubagentsSection` (StackPanel), `SubagentsToggle` (Button), `SubagentsHeaderText` (TextBlock), `SubagentList` (ItemsControl).

- [ ] **Step 1: Write the failing view-model tests**

In `WorkContextViewModelTests.cs` add `using Capacitor.Cli.Core;` to the usings. Change the `Harness` (lines 23–48) so it owns a tracker:

```csharp
    sealed class Harness {
        public BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        public FakeWorkContextSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public Subject<ReactiveUnit> SignIn { get; } = new();
        public int SignInRequests;
        public List<string> OpenedWorkItems { get; } = [];
        public SessionSubagents Subagents { get; }
        public WorkContextViewModel Vm { get; }

        public Harness() {
            Subagents = new SessionSubagents(Time);
            Vm = new WorkContextViewModel(Presence, Source, Time, Opener, Subagents, () => SignInRequests++, SignIn, OpenedWorkItems.Add);
        }
```

(`PushAsync`, `Push`, `TickAsync` stay.) Add two helpers after `Ready()` (line 58) and the tests at the end of the class:

```csharp
    static ChatProjectionResult Spawn(string callId, DateTimeOffset at) =>
        new([], [], [new SubagentSignal.Started(callId, "Explore", "Map the UI", at)]);

    static ChatProjectionResult Finish(string callId, DateTimeOffset at) =>
        new([], [], [new SubagentSignal.Finished(callId, null, SubagentOutcome.Done, at)]);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_section_is_hidden_without_rows_and_its_header_counts_running_and_total() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var raised = new List<string?>();
            h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            await Assert.That(h.Vm.HasSubagents).IsFalse();

            var now = h.Time.GetUtcNow();
            h.Subagents.Apply(Spawn("c1", now));
            h.Subagents.Apply(Spawn("c2", now));
            await Assert.That(h.Vm.HasSubagents).IsTrue();
            await Assert.That(h.Vm.Subagents).Count().IsEqualTo(2);
            await Assert.That(h.Vm.SubagentsHeader).IsEqualTo("2 running · 2 total");
            await Assert.That(raised).Contains(nameof(WorkContextViewModel.HasSubagents));
            await Assert.That(raised).Contains(nameof(WorkContextViewModel.SubagentsHeader));

            h.Subagents.Apply(Finish("c1", now.AddSeconds(5)));
            await Assert.That(h.Vm.SubagentsHeader).IsEqualTo("1 running · 2 total");
            h.Subagents.Apply(Finish("c2", now.AddSeconds(6)));
            await Assert.That(h.Vm.SubagentsHeader).IsEqualTo("2 total");
            await Assert.That(h.Vm.HasSubagents).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }

    /// The section is a session-local fact like the facts under SESSION: it renders whatever
    /// phase the server read is in.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_section_renders_while_the_pane_read_is_loading_or_failed() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Push(Dto());
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            h.Subagents.Apply(Spawn("c1", h.Time.GetUtcNow()));
            await Assert.That(h.Vm.HasSubagents).IsTrue();
            await Assert.That(h.Vm.SubagentsHeader).IsEqualTo("1 running · 1 total");

            gate.SetResult(WorkContextRead.Of(WorkContextReadKind.Unreachable, "no response"));
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Unreachable);
            await Assert.That(h.Vm.HasSubagents).IsTrue();

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SignedOut);
            await Assert.That(h.Vm.HasSubagents).IsTrue();
            await Assert.That(h.Vm.Subagents).Count().IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_section_starts_expanded_and_the_toggle_folds_it() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.SubagentsExpanded).IsTrue();
            await h.Vm.ToggleSubagentsCommand.Execute();
            await Assert.That(h.Vm.SubagentsExpanded).IsFalse();
            await h.Vm.ToggleSubagentsCommand.Execute();
            await Assert.That(h.Vm.SubagentsExpanded).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }
```

In `WorkspaceViewModelTests.cs`, add `[TempDir] public required TempDir Tmp { get; init; }` as the first member of the class, and the fixture + test:

```csharp
    const string AgentCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";

    /// One tracker per workspace: what the chat reads off the transcript is what the pane lists.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_and_the_pane_share_one_subagent_tracker() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var vm = Build(daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()), new FakeTerminalAttachClientFactory(), new FakeTimeProvider());
            var path = Tmp.CreateFile("t.jsonl", [AgentCallLine]);

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { TranscriptPath = path });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.Chat!.PendingReadForTesting ?? Task.CompletedTask);

            await Assert.That(vm.Chat.HasRunningSubagents).IsTrue();
            await Assert.That(vm.WorkContext.HasSubagents).IsTrue();
            await Assert.That(vm.WorkContext.SubagentsHeader).IsEqualTo("1 running · 1 total");
            await Assert.That(vm.WorkContext.Subagents.Single().Name).IsEqualTo("Explore");
            await vm.TeardownAsync();
        });
    }
```

- [ ] **Step 2: Write the failing smoke test**

In `WorkContextViewSmokeTests.cs` add `using Capacitor.Cli.Core;` to the usings. Change the `Host` (lines 25–34) to:

```csharp
    sealed class Host : IAsyncDisposable {
        public BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        public FakeWorkContextSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public SessionSubagents Subagents { get; }
        public WorkContextViewModel Vm { get; }
        public Window Window { get; }

        public Host() {
            Subagents = new SessionSubagents(Time);
            Vm = new WorkContextViewModel(Presence, Source, Time, new RecordingOpener(), Subagents);
            Window = new Window { Content = new WorkContextView { DataContext = Vm }, Width = 320, Height = 900 };
        }
```

(`ShowAsync`, `Find`, `DisposeAsync` stay.) Append inside the class:

```csharp
    /// Each row carries its state in words as well as in the dot, and the failed word is painted
    /// danger; the section hides whole when the session spawned none and folds on its toggle.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_section_lists_rows_by_state_and_hides_when_the_session_spawned_none() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());
            var section = host.Find<StackPanel>("SubagentsSection");
            await Assert.That(section.IsEffectivelyVisible).IsFalse();

            var now = host.Time.GetUtcNow();
            host.Subagents.Apply(new ChatProjectionResult([], [], [
                new SubagentSignal.Started("c1", "Explore", "Map desktop chat UI surfaces", now.AddSeconds(-18)),
                new SubagentSignal.Started("c2", "Reviewer", "Check the plan", now.AddMinutes(-3)),
                new SubagentSignal.Detached("c1", "a1"),
                new SubagentSignal.Finished("c2", null, SubagentOutcome.Failed, now.AddSeconds(-132)),
            ]));
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await Assert.That(section.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Find<TextBlock>("SubagentsHeaderText").Text).IsEqualTo("1 running · 2 total");
            var texts = section.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            await Assert.That(texts).Contains("Explore");
            await Assert.That(texts).Contains("background");
            await Assert.That(texts).Contains("running · 18s");
            await Assert.That(texts).Contains("Map desktop chat UI surfaces");
            await Assert.That(texts).Contains("Reviewer");
            await Assert.That(texts).Contains("failed · 48s");
            await Assert.That(texts.Count(t => t == "background")).IsEqualTo(1);

            var failed = section.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "failed · 48s");
            var danger = (ISolidColorBrush)Avalonia.Application.Current!.FindResource("KcapDangerBrush")!;
            await Assert.That(((ISolidColorBrush)failed.Foreground!).Color).IsEqualTo(danger.Color);
            await Assert.That(section.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsEqualTo(1);

            await host.Vm.ToggleSubagentsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Assert.That(host.Find<ItemsControl>("SubagentList").IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Find<TextBlock>("SubagentsHeaderText").IsEffectivelyVisible).IsTrue();
        });
    }
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewModelTests/*"`
Expected: build error — `WorkContextViewModel` has no `subagents` parameter, `HasSubagents`, `SubagentsHeader`, `SubagentsExpanded` or `ToggleSubagentsCommand`.

- [ ] **Step 4: Take the shared tracker in `WorkContextViewModel`**

In `WorkContextViewModel.cs` add `using Avalonia.Collections;` to the usings. Add the field after `_openWorkItem` (line 51):

```csharp
    readonly SessionSubagents _subagents;
```

Replace the constructor signature and its first lines (143–150):

```csharp
    public WorkContextViewModel(
            IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener,
            SessionSubagents subagents, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null,
            Action<string>? openWorkItem = null) {
        _source = source;
        _opener = opener;
        _time = time;
        _openWorkItem = openWorkItem;
        _subagents = subagents;
        _subagents.Changed += RefreshSubagents;
        InitializeProjections();
```

Add the members after `SessionSummaryLine` (line 77):

```csharp
    /// The session's subagents, shared with the chat tab; a session-local fact like the ones
    /// under SESSION, so it renders in every pane phase.
    public IAvaloniaReadOnlyList<SubagentRow> Subagents => _subagents.Rows;
    public bool HasSubagents => _subagents.Rows.Count > 0;
    public string SubagentsHeader {
        get {
            var running = _subagents.RunningCount;
            var total = _subagents.Rows.Count;
            return running > 0 ? $"{running} running · {total} total" : $"{total} total";
        }
    }

    void RefreshSubagents() {
        this.RaisePropertyChanged(nameof(HasSubagents));
        this.RaisePropertyChanged(nameof(SubagentsHeader));
    }
```

In `TeardownAsync`, after `_tornDown = true;` (line 301) add `_subagents.Changed -= RefreshSubagents;`.

In `WorkContextViewModel.Projections.cs`, after `SessionExpanded` (line 129) add:

```csharp
    bool _subagentsExpanded = true;
    public bool SubagentsExpanded { get => _subagentsExpanded; private set => this.RaiseAndSetIfChanged(ref _subagentsExpanded, value); }
```

after `ToggleSessionCommand` (line 133) add:

```csharp
    public ReactiveCommand<Unit, Unit> ToggleSubagentsCommand { get; private set; } = null!;
```

and in `InitializeProjections` (lines 135–139) add the line:

```csharp
        ToggleSubagentsCommand = Toggle(() => SubagentsExpanded = !SubagentsExpanded);
```

`WorkspaceViewModel.cs` line 113 becomes:

```csharp
        WorkContext = new WorkContextViewModel(presence.Select(p => p.Dto), workContext, time, opener, subagents, requestSignIn, signInCompleted, actions.OpenWorkItemInWeb);
```

(`subagents` is the local Task 5 declared on the line above.)

- [ ] **Step 5: Add the section to the view**

In `WorkContextView.axaml`, add to `UserControl.Styles` after the `Path.chevron` style (line 130):

```xml
        <!-- The state word, never only the dot: failed is painted danger through the class, so a
             local Foreground must not be set on it. -->
        <Style Selector="TextBlock.subagentState">
            <Setter Property="FontSize" Value="10.5" />
            <Setter Property="Foreground" Value="{StaticResource KcapMutedBrush}" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style Selector="TextBlock.subagentState.failed">
            <Setter Property="Foreground" Value="{StaticResource KcapDangerBrush}" />
        </Style>
```

Insert between the divider (line 331) and the `<!-- Session facts. -->` comment:

```xml
                <!-- Subagents: read from the transcript, so it renders in every pane phase. -->
                <StackPanel x:Name="SubagentsSection" IsVisible="{Binding HasSubagents}" Margin="0,0,0,12">
                    <Button x:Name="SubagentsToggle" Command="{Binding ToggleSubagentsCommand}" Classes="sectionHeader">
                        <DockPanel>
                            <Panel DockPanel.Dock="Right" Width="12" Height="12" VerticalAlignment="Center">
                                <Path Classes="chevron" Data="M3,4.5 L6,7.5 L9,4.5" IsVisible="{Binding SubagentsExpanded}" />
                                <Path Classes="chevron" Data="M4.5,3 L7.5,6 L4.5,9" IsVisible="{Binding !SubagentsExpanded}" />
                            </Panel>
                            <TextBlock x:Name="SubagentsHeaderText" DockPanel.Dock="Right" Text="{Binding SubagentsHeader}" FontSize="10.5"
                                       Foreground="{StaticResource KcapMutedBrush}" VerticalAlignment="Center" Margin="0,0,10,0" />
                            <TextBlock Text="SUBAGENTS" Classes="eyebrow" />
                        </DockPanel>
                    </Button>
                    <ItemsControl x:Name="SubagentList" ItemsSource="{Binding Subagents}" Margin="0,10,0,0" IsVisible="{Binding SubagentsExpanded}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate x:DataType="vm:SubagentRow">
                                <Grid ColumnDefinitions="Auto,*,Auto" RowDefinitions="Auto,Auto" ColumnSpacing="9" Margin="0,0,0,8">
                                    <!-- Pulsing warning while running, success done, danger failed, neutral stopped. -->
                                    <Panel Grid.Row="0" Grid.Column="0" Width="8" Height="8" VerticalAlignment="Center">
                                        <Border Classes="toolRunning" Width="8" Height="8" CornerRadius="4" Background="{StaticResource KcapWarningBrush}" IsVisible="{Binding IsRunning}" />
                                        <Ellipse Width="8" Height="8" Fill="{StaticResource KcapSuccessBrush}" IsVisible="{Binding IsDone}" />
                                        <Ellipse Width="8" Height="8" Fill="{StaticResource KcapDangerBrush}" IsVisible="{Binding IsFailed}" />
                                        <Ellipse Width="8" Height="8" Fill="{StaticResource KcapFaintBrush}" IsVisible="{Binding IsStopped}" />
                                    </Panel>
                                    <StackPanel Grid.Row="0" Grid.Column="1" Orientation="Horizontal" Spacing="6">
                                        <TextBlock Text="{Binding Name}" FontSize="11.5" FontWeight="SemiBold" Foreground="{StaticResource KcapTextBrush}"
                                                   TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                                        <TextBlock Text="background" FontSize="10" Foreground="{StaticResource KcapFaintBrush}" VerticalAlignment="Center"
                                                   IsVisible="{Binding IsBackground}" />
                                    </StackPanel>
                                    <TextBlock Grid.Row="0" Grid.Column="2" Text="{Binding StateText}" Classes="subagentState" Classes.failed="{Binding IsFailed}" />
                                    <TextBlock Grid.Row="1" Grid.Column="1" Grid.ColumnSpan="2" Text="{Binding Description}" FontSize="10.5"
                                               Foreground="{StaticResource KcapFaintBrush}" TextTrimming="CharacterEllipsis" Margin="0,2,0,0"
                                               IsVisible="{Binding Description, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                </Grid>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </StackPanel>

```

- [ ] **Step 6: Run the tests to verify they pass**

Run, one at a time:
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewModelTests/*"` — all pass.
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewSmokeTests/*"` — all pass.
- `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewModelTests/*"` — all pass.
- `dotnet build src/Capacitor.App/Capacitor.App.csproj` — no `AVLN` warnings.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/ViewModels/WorkContextViewModel.cs src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs \
        src/Capacitor.App/Views/WorkContextView.axaml src/Capacitor.App/ViewModels/WorkspaceViewModel.cs \
        test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs \
        test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs
git commit -m "List a session's subagents in the work-context pane (#966)" \
  -m "The section sits with the session-local facts rather than inside the server-read card, so it renders while that read is loading or has failed." \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Docs

**Files:**
- Modify: `docs/CHANGES.md` (new entry after the intro, before `## One remap rule covers a family of deleted worktrees`), `README.md` (the `### Desktop app (macOS)` section, after the paragraph beginning `Download …` that ends `without a local daemon.`)

**Interfaces:** none.

- [ ] **Step 1: Add the change note**

Insert into `docs/CHANGES.md` after the intro paragraph ending `the code wins.` and before `## One remap rule covers a family of deleted worktrees`:

```markdown
## The desktop chat shows a session's subagents

A Claude session's subagents are read off the transcript alone: an `Agent` or `Task` call starts a
row, a result whose `toolUseResult.status` is `async_launched` marks it background and binds the
agent id, a task-notification or a `TaskStop` result ends it. The evidence is the same on both
lanes because the leaf now writes the root `toolUseResult` of a single-result line into the
`claude_code` extension as `tool_use_result` — the key and content kcap-server's normalizer already
persists — so sessions ingested before this change list their subagents too. On a line with several
results the object names none of them and is written on none.

The facts ride a third member of `ChatProjectionResult`, beside the rows, rather than on the wire
envelope: `AcpEventEnvelope` is mirrored on the server with a per-field compat guard, and a display
fact only the desktop reads has no business there. The side-band also keeps "which tool names spawn
a subagent" in the vendor's rules, so Codex or Gemini can join without an app-side table. A
task-notification is recognised by `origin_kind` or by its opening tag, because the server's events
carry no `origin_kind`; the same predicate serves the row filter, the input echo and the signal.

The tracker ends a row on transcript evidence only. Without a terminal result, a notification or a
`TaskStop` result the row runs until the session ends — the web's 15-minute quiet reaper is not
copied, since it would also end a quiet subagent that is still working. Session end is a view over
the rows, not a transition: a running row presents as stopped with no duration while the lane says
nothing more will arrive, a real finish in the final drain still settles it, and a remote row that
comes back turns the presentation off again. A repeated notification for an earlier execution never
ends a later launch of the same agent id: a known call id decides alone, and an agent-id-only finish
dated before the row started belongs to an earlier execution.
```

- [ ] **Step 2: Add the README paragraph**

In `README.md`, under `### Desktop app (macOS)`, insert after the first paragraph (the one ending `without a local daemon.`):

```markdown
When a Claude Code session spawns subagents, the chat shows a strip above the composer while any of them run ("2 subagents running"), and the work-context pane lists them under **SUBAGENTS** — the agent type, a *background* tag for one launched in the background, and its state: running with its elapsed time, done, failed or stopped. Both are read from the transcript, so they work for sessions on other machines' daemons as well; rows are not links.
```

- [ ] **Step 3: Check the tree builds and commit**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` — no warnings (docs only; a sanity check that the branch is green before the PR).

```bash
git add docs/CHANGES.md README.md
git commit -m "Document the desktop chat's subagent strip and section (#966)" \
  -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Before opening the PR

- `dotnet build src/Capacitor.Models.Transcripts/Capacitor.Models.Transcripts.csproj`, `dotnet build src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj`, `dotnet build src/Capacitor.App/Capacitor.App.csproj` — warning-free.
- `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — no output.
- The PR title carries no reference; the description follows `.github/PULL_REQUEST_TEMPLATE.md` and its reference line reads `Part of #966` with `AI-2840` beside it.

