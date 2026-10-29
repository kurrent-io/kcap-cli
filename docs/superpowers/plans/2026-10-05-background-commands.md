# Background Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claude background shell commands show in the desktop app's strip above the composer and in the session sidebar, beside background subagents.

**Architecture:** The subagent pipeline (vendor rules emit signals → `SessionSubagents` folds them into rows → strip and sidebar bind the rows) is renamed to "runs" and gains a second row kind. Claude's rules emit a provisional start for every `Bash` call; a result carrying `toolUseResult.backgroundTaskId` promotes it to a Shell row; the existing `<task-notification>` path ends it.

**Tech Stack:** .NET 10, Avalonia 12 (ReactiveUI), TUnit.

**Spec:** `docs/superpowers/specs/2026-10-05-background-commands-design.md`

## Global Constraints

- Comments follow `CLAUDE.md` § Comments: scarce, no history, no spec/task coordinates, no ticket ids.
- No Linear ids anywhere in code or commit messages.
- Commit subject: imperative, one clause, ≤80 chars; body ≤5 lines; end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Status colours (`KcapSuccess*`, `KcapWarning*`) stay on outcome marks only; kind glyphs use `KcapMutedBrush`.
- Unused usings are build errors (IDE0005).
- Run git as `/usr/bin/git` from the worktree root; no compound shell constructs in Bash calls.
- App UI tests: prefix runs with `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8`.
- TUnit filter syntax: `--treenode-filter "/*/*/ClassName/*"`.

## Review Focus

1. A foreground `Bash` call followed by its plain result must leave no row and no pending entry — a reasonable person sees nothing for ordinary commands. (Task 3, `A_foreground_shell_call_leaves_no_row`.)
2. A notification whose `<status>` is `running` (or any unknown value) must not end a row. (Task 2, `A_running_or_unknown_notification_status_ends_nothing`.)
3. A command auto-backgrounded on timeout must be dated from its call, so elapsed time includes the foreground part. (Task 3, `A_timed_out_shell_is_dated_from_its_call`.)
4. A notification or stop for a shell whose call the feed never saw (window started mid-session) changes nothing. (Task 3, `A_detach_for_an_unseen_call_makes_no_row`.)
5. The session ending with a shell still running presents it as stopped, like an agent. (Task 3, `Session_over_presents_a_running_shell_as_stopped`.)

---

### Task 1: Rename the subagent pipeline to runs

Mechanical; no behaviour change.

**Files:**
- Rename: `src/Capacitor.Cli.Core/SubagentSignal.cs` → `RunSignal.cs`, `src/Capacitor.Cli.Core/SubagentOutcome.cs` → `RunOutcome.cs`
- Rename: `src/Capacitor.App/ViewModels/SessionSubagents.cs` → `SessionRuns.cs`, `SubagentRow.cs` → `RunRow.cs`, `SubagentState.cs` → `RunState.cs`, `SubagentCount.cs` → `RunCount.cs`
- Rename: `test/Capacitor.App.Tests.Unit/SessionSubagentsTests.cs` → `SessionRunsTests.cs`
- Modify: every `.cs`/`.axaml` under `src/Capacitor.Cli.Core`, `src/Capacitor.App`, `test/Capacitor.Cli.Core.Tests.Unit`, `test/Capacitor.App.Tests.Unit` that uses the identifiers below. **Not** `test/Capacitor.Cli.Tests.Integration` — `KiroCrewSessionStartParentTests` has an unrelated method named `SubagentState`.

**Interfaces:**
- Produces: `RunSignal` (`Started`, `Detached`, `Finished`), `RunOutcome`, `ChatProjectionResult.Runs`, `IChatDisplayRules.Runs(CanonicalEvent, AcpEventEnvelope)`, `SessionRuns`, `RunRow`, `RunState`, `RunCount`; `ChatTabViewModel.HasRunningRuns`, `RunningRow`, `RunSummary`; `WorkContextViewModel.Runs`, `HasRuns`, `RunningRuns`, `HasRunningRuns`, `RunsHeader`, `RunCounts`, `RunsExpanded`, `ToggleRunsCommand`.

- [ ] **Step 1: Move the files**

```bash
/usr/bin/git mv src/Capacitor.Cli.Core/SubagentSignal.cs src/Capacitor.Cli.Core/RunSignal.cs
/usr/bin/git mv src/Capacitor.Cli.Core/SubagentOutcome.cs src/Capacitor.Cli.Core/RunOutcome.cs
/usr/bin/git mv src/Capacitor.App/ViewModels/SessionSubagents.cs src/Capacitor.App/ViewModels/SessionRuns.cs
/usr/bin/git mv src/Capacitor.App/ViewModels/SubagentRow.cs src/Capacitor.App/ViewModels/RunRow.cs
/usr/bin/git mv src/Capacitor.App/ViewModels/SubagentState.cs src/Capacitor.App/ViewModels/RunState.cs
/usr/bin/git mv src/Capacitor.App/ViewModels/SubagentCount.cs src/Capacitor.App/ViewModels/RunCount.cs
/usr/bin/git mv test/Capacitor.App.Tests.Unit/SessionSubagentsTests.cs test/Capacitor.App.Tests.Unit/SessionRunsTests.cs
```

- [ ] **Step 2: Rename the unique identifiers**

Write this script to the scratchpad as `rename-runs.pl` and run it with `find src/Capacitor.Cli.Core src/Capacitor.App test/Capacitor.Cli.Core.Tests.Unit test/Capacitor.App.Tests.Unit \( -name '*.cs' -o -name '*.axaml' \) -exec perl -pi <scratchpad>/rename-runs.pl {} +`. All patterns are word-bounded, so `x:Name`s such as `SubagentsBanner`, `RunningSubagentText`, `SubagentsSection` stay (the smoke tests look them up by name).

```perl
s/\bSubagentSignal\b/RunSignal/g;
s/\bSubagentOutcome\b/RunOutcome/g;
s/\bSessionSubagents\b/SessionRuns/g;
s/\bSessionSubagentsTests\b/SessionRunsTests/g;
s/\bSubagentRow\b/RunRow/g;
s/\bSubagentState\b/RunState/g;
s/\bSubagentCounts\b/RunCounts/g;
s/\bSubagentCount\b/RunCount/g;
s/\bSubagentCountOrder\b/RunCountOrder/g;
s/\bHasRunningSubagents\b/HasRunningRuns/g;
s/\bRunningSubagents\b/RunningRuns/g;
s/\bRunningSubagent\b/RunningRow/g;
s/\bSubagentSummary\b/RunSummary/g;
s/\bSubagentsHeader\b/RunsHeader/g;
s/\bHasSubagents\b/HasRuns/g;
s/\bSubagentsExpanded\b/RunsExpanded/g;
s/\b_subagentsExpanded\b/_runsExpanded/g;
s/\bToggleSubagentsCommand\b/ToggleRunsCommand/g;
s/\bRefreshSubagents\b/RefreshRuns/g;
s/\bSubagents\(CanonicalEvent\b/Runs(CanonicalEvent/g;
```

The last line matters: `IChatDisplayRules.Runs` has a default body, so an implementer still named `Subagents` would compile and silently never be called. After running, confirm none is left:

Run: `rtk proxy grep -rn 'Subagents(CanonicalEvent' src test`
Expected: no output.

- [ ] **Step 3: Rename the `Subagents` members by hand**

`Subagents` alone is too common to rewrite blindly. Change exactly these:
- `src/Capacitor.Cli.Core/ChatProjectionResult.cs`: the record parameter `IReadOnlyList<RunSignal> Subagents` → `Runs`; its doc comment's "a subagent signal" → "a run signal".
- `src/Capacitor.App/ViewModels/WorkContextViewModel.cs`: property `Subagents` → `Runs`; field `_subagents` → `_runs`.
- `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`, `WorkspaceViewModel.cs`, `RemoteSessionViewModel.cs`: field/local `_subagents`/`subagents` holding a `SessionRuns` → `_runs`/`runs`.
- `src/Capacitor.App/Views/WorkContextView.axaml`: `{Binding Subagents}` → `{Binding Runs}`.
- `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`: harness property `public SessionRuns Subagents` → `Runs`, and its uses `h.Subagents` → `h.Runs`.
- `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`: `Host` property `public SessionRuns Subagents` → `Runs`, and its uses `host.Subagents` → `host.Runs`. Do the same for any other test harness the build in Step 4 does not flag but that holds a `SessionRuns` in a property named `Subagents` (`rtk proxy grep -rn 'SessionRuns Subagents' test`).

- [ ] **Step 4: Build and fix what the compiler names**

Run: `dotnet build Capacitor.slnx 2>&1 | grep -E 'error|warn' | sort -u`
Expected: only `CS1061 ... does not contain a definition for 'Subagents'` errors, each on a `ChatProjectionResult` or `WorkContextViewModel` access (in `TranscriptChat.cs`, `SessionRuns.cs`, `RemoteTranscriptFeed.cs` and the tests). Replace `.Subagents` with `.Runs` at each named line and rebuild until clean — 0 errors, 0 warnings. Also rename local variables named after the old types where the compiler points you (e.g. `subagents` lists in `TranscriptChat.cs` → `runs`).

- [ ] **Step 5: Run the affected suites**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`
Expected: all pass.
Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git add -A src test
/usr/bin/git commit -m "Rename the subagent row pipeline to runs" -m "Background shell commands will share these rows, and a shell is not a subagent.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Claude rules emit shell runs and map notification statuses

**Files:**
- Create: `src/Capacitor.Cli.Core/RunKind.cs`
- Modify: `src/Capacitor.Cli.Core/RunSignal.cs`
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeChatRules.cs` (the `Runs` method, `SpawnFacts`, new `ShellFacts`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeChatRulesTests.cs`

**Interfaces:**
- Consumes: `RunSignal`, `RunOutcome` from Task 1.
- Produces:
  - `public enum RunKind { Agent, Shell }` (namespace `Capacitor.Cli.Core`).
  - `RunSignal.Started(string CallId, string Name, string Description, DateTimeOffset At, RunKind Kind = RunKind.Agent, bool Provisional = false)`.
  - Claude: `Bash` call → `Started(callId, name, commandLine, at, RunKind.Shell, Provisional: true)`; result with `backgroundTaskId` → `Detached(callId, backgroundTaskId)`; notification statuses `completed`/`failed`/`killed` → `Done`/`Failed`/`Stopped`, no `<status>` → outcome `null`, any other status → no signal; stop gate accepts `local_bash`.

- [ ] **Step 1: Write the failing tests**

In `ClaudeChatRulesTests`, beside the existing `AgentCall` constants, add the shell fixtures (shaped like real transcript lines):

```csharp
    const string ShellCall = """{"type":"assistant","timestamp":"2026-09-17T10:00:00Z","message":{"content":[{"type":"tool_use","id":"toolu_B","name":"Bash","input":{"command":"dotnet test --solution Capacitor.slnx\n  --no-build","description":"Run the full suite","run_in_background":true}}]}}""";
    const string ShellLaunchResult = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_B","type":"tool_result","content":"Command running in background with ID: bcyix00ks. Output is being written to: /tmp/x/tasks/bcyix00ks.output.","is_error":false}]},"toolUseResult":{"stdout":"","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false,"backgroundTaskId":"bcyix00ks"}}""";
    const string ShellTimedOutResult = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_C","type":"tool_result","content":"Command running in background with ID: b7k2. Output is being written to: /tmp/x/tasks/b7k2.output.","is_error":false}]},"toolUseResult":{"stdout":"","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false,"backgroundTaskId":"b7k2","timedOutAfterMs":120000}}""";
```

Change the existing killed assertion in `Finished_from_a_notification_keyed_by_both_ids_and_failed_unless_completed` and rename the test:

```csharp
    public async Task Finished_from_a_notification_keyed_by_both_ids_with_the_status_as_its_outcome(bool originKind) {
        // ... existing body up to the `failed` lines, then:
        var failed = (RunSignal.Finished)R(Notification(originKind, status: "failed")).Runs.Single();
        await Assert.That(failed.Outcome).IsEqualTo(RunOutcome.Failed);
        var killed = (RunSignal.Finished)R(Notification(originKind, status: "killed")).Runs.Single();
        await Assert.That(killed.Outcome).IsEqualTo(RunOutcome.Stopped);
    }
```

In `Stopped_from_a_TaskStop_success_result_and_nothing_from_a_TaskOutput_probe`, replace the final `shell` assertion (a `local_bash` stop now ends its row):

```csharp
        var shell = (RunSignal.Finished)R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_S","content":"Successfully stopped task: b1"}]},"toolUseResult":{"task_id":"b1","task_type":"local_bash","message":"Successfully stopped task: b1","command":"sleep 100"}}""").Runs.Single();
        await Assert.That(shell.AgentId).IsEqualTo("b1");
        await Assert.That(shell.Outcome).IsEqualTo(RunOutcome.Stopped);
        var shellProbe = R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_O","content":"…"}]},"toolUseResult":{"task_id":"b1","task_type":"local_bash","message":"Task output (last 10 lines)"}}""");
        await Assert.That(shellProbe.Runs).IsEmpty();
```

In `No_signal_for_a_sidechain_call_a_sidechain_notification_or_any_other_tool`, the `Bash` line now yields a provisional shell start; replace it with a non-spawning tool:

```csharp
        await Assert.That(R("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Read","input":{"file_path":"/x","subagent_type":"Explore"}}]}}""").Runs).IsEmpty();
        await Assert.That(R(ShellCall.Replace("\"type\":\"assistant\",", "\"type\":\"assistant\",\"isSidechain\":true,")).Runs).IsEmpty();
```

New tests:

```csharp
    [Test]
    public async Task A_Bash_call_is_a_provisional_shell_start_named_by_its_description() {
        var started = (RunSignal.Started)R(ShellCall).Runs.Single();
        await Assert.That(started.CallId).IsEqualTo("toolu_B");
        await Assert.That(started.Kind).IsEqualTo(RunKind.Shell);
        await Assert.That(started.Provisional).IsTrue();
        await Assert.That(started.Name).IsEqualTo("Run the full suite");
        await Assert.That(started.Description).IsEqualTo("dotnet test --solution Capacitor.slnx");
        await Assert.That(started.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));
    }

    /// A call without the flag still starts provisionally: a foreground command that hits its
    /// timeout is moved to the background, and only its result says so.
    [Test]
    public async Task An_unflagged_Bash_call_without_a_description_is_named_by_its_first_command_line() {
        var call = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_C","name":"Bash","input":{"command":"  make check\nmake lint"}}]}}""";
        var started = (RunSignal.Started)R(call).Runs.Single();
        await Assert.That(started.Provisional).IsTrue();
        await Assert.That(started.Name).IsEqualTo("make check");
        await Assert.That(started.Description).IsEqualTo("make check");
    }

    [Test]
    public async Task A_long_command_name_is_cut_to_eighty_characters() {
        var command = new string('x', 120);
        var started = (RunSignal.Started)R($$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_C","name":"Bash","input":{"command":"{{command}}"}}]}}""").Runs.Single();
        await Assert.That(started.Name).IsEqualTo(new string('x', 79) + "…");
    }

    [Test]
    [Arguments(ShellLaunchResult, "toolu_B", "bcyix00ks")]
    [Arguments(ShellTimedOutResult, "toolu_C", "b7k2")]
    public async Task A_result_with_a_background_task_id_detaches_the_call(string line, string callId, string taskId) {
        var detached = (RunSignal.Detached)R(line).Runs.Single();
        await Assert.That(detached.CallId).IsEqualTo(callId);
        await Assert.That(detached.AgentId).IsEqualTo(taskId);
    }

    [Test]
    public async Task A_plain_Bash_result_is_no_signal() {
        var result = """{"type":"user","message":{"content":[{"tool_use_id":"toolu_B","type":"tool_result","content":"ok"}]},"toolUseResult":{"stdout":"ok","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false}}""";
        await Assert.That(R(result).Runs).IsEmpty();
    }

    [Test]
    [Arguments("running")]
    [Arguments("pending")]
    public async Task A_running_or_unknown_notification_status_ends_nothing(string status) {
        await Assert.That(R(Notification(originKind: true, status: status)).Runs).IsEmpty();
    }
```

`A_notification_cut_off_before_its_closing_tag_still_finishes` stays as it is and must now also assert the unknown outcome — add:

```csharp
        await Assert.That(finished.Outcome).IsNull();
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeChatRulesTests/*"`
Expected: compile failure (`RunKind` and `Started.Kind`/`Provisional` do not exist).

- [ ] **Step 3: Add the kind and extend the signal**

`src/Capacitor.Cli.Core/RunKind.cs`:

```csharp
namespace Capacitor.Cli.Core;

public enum RunKind { Agent, Shell }
```

In `RunSignal.cs`, replace `Started`:

```csharp
    /// A tool call that may run on. A provisional start makes no row until a Detached for its
    /// call says the run went to the background.
    public sealed record Started(string CallId, string Name, string Description, DateTimeOffset At, RunKind Kind = RunKind.Agent, bool Provisional = false) : RunSignal;
```

Update the type's doc comment from "A subagent fact" to "A fact about a subagent or background command".

- [ ] **Step 4: Implement the Claude rules**

In `ClaudeChatRules.Runs`, add a `Bash` case after the `Agent`/`Task` one, check `backgroundTaskId` first in the tool-result case, accept `local_bash` in the stop gate, and map statuses:

```csharp
            case AcpEventKind.ToolCall when raw.ToolName is "Bash" && raw.ToolCallId is { Length: > 0 } callId: {
                var (name, command) = ShellFacts(raw.ToolInputJson);
                return [new RunSignal.Started(callId, name, command, evt.Timestamp, RunKind.Shell, Provisional: true)];
            }
            case AcpEventKind.ToolResult when raw.ToolCallId is { Length: > 0 } callId && ToolUseResult(slug) is { } result: {
                if (SchemaExtensions.Text(result, "backgroundTaskId") is { Length: > 0 } taskId)
                    return [new RunSignal.Detached(callId, taskId)];
                if (SchemaExtensions.Text(result, "status") == "async_launched"
                    && (SchemaExtensions.Text(result, "agentId") ?? SchemaExtensions.Text(result, "agent_id")) is { Length: > 0 } agentId)
                    return [new RunSignal.Detached(callId, agentId)];
                // The message gate keeps a TaskGet or TaskOutput probe, which carries the same
                // task_id, from ending a running row.
                if (SchemaExtensions.Text(result, "task_type") is "local_agent" or "local_bash"
                    && SchemaExtensions.Text(result, "task_id") is { Length: > 0 } stoppedId
                    && (SchemaExtensions.Text(result, "message") ?? "").AsSpan().TrimStart().StartsWith(StoppedTaskMessage, StringComparison.OrdinalIgnoreCase))
                    return [new RunSignal.Finished(null, stoppedId, RunOutcome.Stopped, evt.Timestamp)];
                return [];
            }
            case AcpEventKind.UserMessage when IsTaskNotification(slug, raw): {
                var text = raw.Text ?? "";
                var callId = Tag(TaskToolUseId(), text);
                var agentId = Tag(TaskId(), text);
                if (callId is null && agentId is null) return [];
                // A cut-off notification has no status and still says the run ended; a status
                // that is not an end, such as running, says nothing.
                RunOutcome? outcome;
                switch (Tag(TaskStatus(), text)?.ToLowerInvariant()) {
                    case null:        outcome = null; break;
                    case "completed": outcome = RunOutcome.Done; break;
                    case "failed":    outcome = RunOutcome.Failed; break;
                    case "killed":    outcome = RunOutcome.Stopped; break;
                    default:          return [];
                }
                return [new RunSignal.Finished(callId, agentId, outcome, evt.Timestamp)];
            }
```

(The `Agent`/`Task` case is unchanged; its `Started` keeps the defaults `RunKind.Agent`, not provisional.)

Add beside `SpawnFacts`:

```csharp
    const int ShellNameLimit = 80;

    /// The row's name is the call's description, else the command's first line; the description
    /// line under it is that first line.
    static (string Name, string Command) ShellFacts(string? inputJson) {
        if (inputJson is null) return ("command", "");
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var input = doc.RootElement;
            var command = FirstLine(input.Str("command") ?? "");
            var name = input.Str("description") is { } d && d.Trim() is { Length: > 0 } described ? described : command;
            if (name.Length == 0) name = "command";
            return (name.Length > ShellNameLimit ? name[..(ShellNameLimit - 1)] + "…" : name, command);
        } catch (JsonException) {
            return ("command", "");
        }
    }

    static string FirstLine(string text) {
        var trimmed = text.TrimStart();
        var end = trimmed.IndexOf('\n');
        return (end < 0 ? trimmed : trimmed[..end]).TrimEnd();
    }
```

Update the class doc comment's last sentence to "Also where a subagent's or a background command's launch, detachment and end are read."

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeChatRulesTests/*"`
Expected: all pass.
Run the whole Core suite (the canonical-chat tests use the same rules): `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git add src/Capacitor.Cli.Core test/Capacitor.Cli.Core.Tests.Unit
/usr/bin/git commit -m "Read background shell commands from Claude transcripts" -m "Every Bash call starts provisionally: a command that times out is moved to the background with no flag on its call. A killed run now reads stopped, not failed.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: SessionRuns promotes provisional shell starts into rows

**Files:**
- Modify: `src/Capacitor.App/ViewModels/SessionRuns.cs`
- Modify: `src/Capacitor.App/ViewModels/RunRow.cs`
- Test: `test/Capacitor.App.Tests.Unit/SessionRunsTests.cs`

**Interfaces:**
- Consumes: `RunSignal.Started.Kind/Provisional`, `RunKind` from Task 2.
- Produces: `RunRow(string callId, string name, string description, DateTimeOffset startedAt, RunKind kind = RunKind.Agent)`, `RunRow.Kind`, `RunRow.IsShell`.

- [ ] **Step 1: Write the failing tests**

Add to `SessionRunsTests`:

```csharp
    static RunSignal.Started Shell(string callId, DateTimeOffset? at = null) =>
        new(callId, "Run the full suite", "dotnet test", at ?? T0, RunKind.Shell, Provisional: true);

    [Test]
    public async Task A_foreground_shell_call_leaves_no_row() {
        var s = new SessionRuns(Clock());
        var changes = 0;
        s.Changed += () => changes++;
        s.Apply(Signals(Shell("c1")));
        await Assert.That(s.Rows).IsEmpty();
        s.Apply(Result("c1"));
        await Assert.That(s.Rows).IsEmpty();
        // The pending entry went with the result: a stray Detached later makes no row.
        s.Apply(Signals(Detached("c1", "b1")));
        await Assert.That(s.Rows).IsEmpty();
        await Assert.That(changes).IsEqualTo(0);
    }

    [Test]
    public async Task A_background_shell_runs_from_its_detach_until_its_notification() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")));
        var row = Only(s);
        await Assert.That(row.Kind).IsEqualTo(RunKind.Shell);
        await Assert.That(row.IsShell).IsTrue();
        await Assert.That(row.Name).IsEqualTo("Run the full suite");
        await Assert.That(row.Description).IsEqualTo("dotnet test");
        await Assert.That(row.IsBackground).IsTrue();
        await Assert.That(row.StateText).IsEqualTo("running in background · 0s");
        await Assert.That(s.RunningCount).IsEqualTo(1);

        s.Apply(Signals(Finished("c1", "b1", RunOutcome.Stopped, at: T0.AddSeconds(30))));
        await Assert.That(row.State).IsEqualTo(RunState.Stopped);
        await Assert.That(row.StateText).IsEqualTo("stopped · 30s");
    }

    [Test]
    public async Task A_timed_out_shell_is_dated_from_its_call() {
        var clock = Clock();
        var s = new SessionRuns(clock);
        s.Apply(Signals(Shell("c1", at: T0)));
        clock.Advance(TimeSpan.FromMinutes(2));
        s.Apply(Mixed([new AcpEventEnvelope(Kind: AcpEventKind.ToolResult, ToolCallId: "c1")], Detached("c1", "b1")));
        await Assert.That(Only(s).StartedAt).IsEqualTo(T0);
        await Assert.That(Only(s).StateText).IsEqualTo("running in background · 2m 00s");
    }

    [Test]
    public async Task A_detach_for_an_unseen_call_makes_no_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Detached("c9", "b9"), Finished("c9", "b9")));
        await Assert.That(s.Rows).IsEmpty();
    }

    [Test]
    public async Task A_shell_stopped_by_its_task_id_alone_ends() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.Apply(Signals(Finished(null, "b1", RunOutcome.Stopped)));
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
    }

    [Test]
    public async Task Session_over_presents_a_running_shell_as_stopped() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.SessionOver = true;
        await Assert.That(Only(s).State).IsEqualTo(RunState.Stopped);
        await Assert.That(Only(s).StateText).IsEqualTo("stopped");
    }

    [Test]
    public async Task A_repeated_provisional_start_for_a_live_row_changes_nothing() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Apply(Signals(Detached("c1", "b1")));
        s.Apply(Signals(Shell("c1")));
        await Assert.That(s.Rows).Count().IsEqualTo(1);
        await Assert.That(Only(s).IsRunning).IsTrue();
    }

    [Test]
    public async Task Clear_drops_pending_shell_calls() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Shell("c1")));
        s.Clear();
        s.Apply(Signals(Detached("c1", "b1")));
        await Assert.That(s.Rows).IsEmpty();
    }

    [Test]
    public async Task An_agent_start_is_an_agent_row() {
        var s = new SessionRuns(Clock());
        s.Apply(Signals(Started("c1")));
        await Assert.That(Only(s).Kind).IsEqualTo(RunKind.Agent);
        await Assert.That(Only(s).IsShell).IsFalse();
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionRunsTests/*"`
Expected: compile failure (`RunRow.Kind`/`IsShell` do not exist).

- [ ] **Step 3: Give the row a kind**

In `RunRow.cs`, change the constructor and add the members:

```csharp
    public RunRow(string callId, string name, string description, DateTimeOffset startedAt, RunKind kind = RunKind.Agent) {
        CallId = callId;
        Name = name;
        Description = description;
        StartedAt = startedAt;
        Kind = kind;
    }

    public RunKind Kind { get; }
    public bool IsShell => Kind == RunKind.Shell;
```

Update the class doc comment: "One subagent or background command as the sidebar shows it." Add `using Capacitor.Cli.Core;` if not already present.

- [ ] **Step 4: Hold provisional starts and promote them**

In `SessionRuns.cs`:

```csharp
    /// Calls that may still go to the background, by call id; a Detached promotes one to a row,
    /// its result drops it.
    readonly Dictionary<string, RunSignal.Started> _pending = new(StringComparer.Ordinal);
```

`Start`:

```csharp
    void Start(RunSignal.Started started) {
        if (_byCall.ContainsKey(started.CallId)) return;
        if (started.Provisional) {
            _pending[started.CallId] = started;
            return;
        }
        Add(started);
    }

    RunRow Add(RunSignal.Started started) {
        var row = new RunRow(started.CallId, started.Name, started.Description, started.At, started.Kind);
        _byCall[started.CallId] = row;
        _rows.Add(row);
        return row;
    }
```

`Detach`:

```csharp
    void Detach(RunSignal.Detached detached) {
        if (!_byCall.TryGetValue(detached.CallId, out var row)) {
            if (!_pending.Remove(detached.CallId, out var pending)) return;
            row = Add(pending);
        }
        if (row.IsEnded) return;
        row.MarkBackground();
        _byAgent[detached.AgentId] = row;
    }
```

In `Apply`'s envelope loop, drop the pending entry for every result not detached in this projection — insert after the `detachedHere` check:

```csharp
            _pending.Remove(callId);
```

`Clear()`: add `_pending.Clear();`. Update the class doc comment to "The subagents and background commands of one session, …". `SessionOver` deliberately leaves `_pending` alone: a remote lane can turn it back off, and a pending entry shows nothing.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionRunsTests/*"`
Expected: all pass, including the pre-existing ones.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git add src/Capacitor.App/ViewModels test/Capacitor.App.Tests.Unit/SessionRunsTests.cs
/usr/bin/git commit -m "Track background shell commands as session runs" -m "A Bash call is held until its result: only one carrying a background id becomes a row, dated from the call.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: The strip above the composer names commands

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (`RunSummary`)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: `RunRow.Kind`, `RunRow.IsBackground`, `SessionRuns.Rows` (Tasks 1–3); the Claude fixture lines run through the real projection, so Task 2's rules are exercised end to end.

- [ ] **Step 1: Write the failing test**

Add fixture lines beside `AgentCallLine`:

```csharp
    const string ShellCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_S","name":"Bash","input":{"command":"dotnet test","description":"Run the full suite","run_in_background":true}}]}}""";
    const string ShellLaunchLine = """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_S","type":"tool_result","content":"Command running in background with ID: bcyix00ks.","is_error":false}]},"toolUseResult":{"stdout":"","stderr":"","interrupted":false,"isImage":false,"noOutputExpected":false,"backgroundTaskId":"bcyix00ks"}}""";
    const string ShellFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>bcyix00ks</task-id>\n<tool-use-id>toolu_S</tool-use-id>\n<status>completed</status>\n<summary>Background command \"Run the full suite\" completed (exit code 0)</summary>\n</task-notification>"}}""";
```

Add the test after `Fixture_transcripts_drive_the_strip_singular_and_plural`:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Fixture_transcripts_put_background_commands_in_the_strip() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ShellCallLine, ShellLaunchLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.HasRunningRuns).IsTrue();
            await Assert.That(h.Chat.RunningRow!.Name).IsEqualTo("Run the full suite");
            await Assert.That(h.Chat.RunningRow!.StateText).StartsWith("running in background · ");

            File.AppendAllText(path, ShellCallLine.Replace("toolu_S", "toolu_T") + "\n" + ShellLaunchLine.Replace("toolu_S", "toolu_T").Replace("bcyix00ks", "b2") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunningRow).IsNull();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("2 commands running in background");

            File.AppendAllText(path, AgentCallLine + "\n" + AgentLaunchLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("1 subagent, 2 commands running in background");

            File.AppendAllText(path, ShellFinishLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.RunSummary).IsEqualTo("1 subagent, 1 command running in background");
            await Assert.That(h.Runs.Rows.Single(r => r.CallId == "toolu_S").State).IsEqualTo(RunState.Done);

            File.AppendAllText(path, ShellCallLine.Replace("toolu_S", "toolu_F") + "\n" + ToolResultLine.Replace("t1", "toolu_F") + "\n");
            await h.TickAsync();
            await Assert.That(h.Runs.Rows.Any(r => r.CallId == "toolu_F")).IsFalse();
            await h.TeardownAsync();
        });
    }
```

The existing `Fixture_transcripts_drive_the_strip_singular_and_plural` keeps its `"2 subagents running"` expectation (one foreground agent, one background — not all background).

- [ ] **Step 2: Run it to verify it fails**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/Fixture_transcripts_put_background_commands_in_the_strip"`
Expected: FAIL — `RunSummary` is `"2 subagents running in background"`.

- [ ] **Step 3: Implement the wording**

Replace `RunSummary` in `ChatTabViewModel.cs`:

```csharp
    public string RunSummary {
        get {
            var running = _runs.Rows.Where(r => r.IsRunning).ToList();
            if (running.Count < 2) return "";
            var shells = running.Count(r => r.IsShell);
            var agents = running.Count - shells;
            var counts = (agents, shells) switch {
                (_, 0) => $"{agents} subagents",
                (0, _) => $"{shells} commands",
                _      => $"{Plural(agents, "subagent")}, {Plural(shells, "command")}",
            };
            return running.All(r => r.IsBackground) ? $"{counts} running in background" : $"{counts} running";
        }
    }

    static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
```

Shell rows are always background, so a command never appears in the `running` (foreground) wording alone. Update the doc comment above `HasRunningRuns` if it says "subagent".

- [ ] **Step 4: Run the strip tests to verify they pass**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git add src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs
/usr/bin/git commit -m "Count background commands in the chat's running strip" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: The sidebar section covers agents and commands

**Files:**
- Modify: `src/Capacitor.App/Views/WorkContextView.axaml` (`SubagentItem` template ~line 312, section title ~line 746, comment ~line 651)
- Modify: `docs/superpowers/specs/2026-10-05-background-commands-design.md`
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs`

**Interfaces:**
- Consumes: `RunRow.IsShell` (Task 3), `WorkContextViewModel.Runs` (Task 1).

- [ ] **Step 1: Write the failing smoke test**

Find the existing smoke test that renders `SubagentList` with rows (search `SubagentList` in `WorkContextViewSmokeTests.cs`) and follow its setup — it builds a `SessionRuns`, applies signals, and realizes the view. Add one that applies an agent and a promoted shell and asserts:

```csharp
    /// The title covers both kinds, and only a shell row is led by the `$` glyph — in the folded
    /// running list and in the opened full list alike, since both use one item template.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_section_is_titled_for_agents_and_commands_and_marks_shell_rows() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());
            var now = host.Time.GetUtcNow();
            host.Runs.Apply(new ChatProjectionResult([], [], [
                new RunSignal.Started("c1", "Explore", "Map desktop chat UI surfaces", now.AddSeconds(-18)),
                new RunSignal.Started("c2", "Run the full suite", "dotnet test", now.AddSeconds(-40), RunKind.Shell, Provisional: true),
                new RunSignal.Detached("c2", "b1"),
            ]));
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            var section = host.Find<StackPanel>("SubagentsSection");
            await Assert.That(VisibleTexts(section)).Contains("AGENTS & COMMANDS");
            await Assert.That(VisibleGlyphs(host.Find<ItemsControl>("RunningSubagentList"))).IsEqualTo(1);
            await Assert.That(VisibleTexts(section)).Contains("Run the full suite");

            await host.Vm.ToggleRunsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Assert.That(VisibleGlyphs(host.Find<ItemsControl>("SubagentList"))).IsEqualTo(1);
        });

        static int VisibleGlyphs(ItemsControl list) =>
            list.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Name == "ShellGlyph" && t.IsEffectivelyVisible);
    }
```

`Host`, `KeyOnlyRead()`, `VisibleTexts(...)` and `RunOnUiAsync` already exist in this file; Task 1 renamed `Host.Subagents` to `Host.Runs`.

- [ ] **Step 2: Run it to verify it fails**

Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewSmokeTests/The_section_is_titled_for_agents_and_commands_and_marks_shell_rows"`
Expected: FAIL on the title.

- [ ] **Step 3: Update the view**

Section title:

```xml
                            <TextBlock Text="AGENTS &amp; COMMANDS" Classes="eyebrow" />
```

Rename the item template key `SubagentItem` → `RunItem` (both the definition and its two `ItemTemplate="{StaticResource …}"` uses), and put the glyph in its name line:

```xml
                <StackPanel Grid.Column="1" Spacing="2">
                    <StackPanel Orientation="Horizontal" Spacing="5">
                        <TextBlock x:Name="ShellGlyph" Text="$" FontFamily="Menlo, Consolas, monospace" FontSize="12" LineHeight="16"
                                   Foreground="{StaticResource KcapMutedBrush}" VerticalAlignment="Center" IsVisible="{Binding IsShell}" />
                        <TextBlock Text="{Binding Name}" FontSize="13" LineHeight="16" FontWeight="SemiBold" Foreground="{StaticResource KcapTextBrush}"
                                   TextTrimming="CharacterEllipsis" ToolTip.Tip="{Binding Name}" />
                    </StackPanel>
```

`App.axaml` defines no monospace font resource, hence the literal family. Update the two comments that say "SUBAGENTS"/"Subagents" (lines ~651 and ~742) to "AGENTS & COMMANDS" / "Agents and commands".

- [ ] **Step 4: Amend the spec to what was built**

In the spec's "Sidebar section", replace the glyph bullet with: "A shell row's name is led by a muted monospace `$`; agent rows are unchanged." In "SessionRuns", delete "So does the end of the session, so a call that never got a result does not linger." and add: "The end of the session leaves pending calls alone: a remote lane can turn it back off, and a pending call shows nothing."

- [ ] **Step 5: Run the app suite and build clean**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E 'warning|error' | sort -u`
Expected: no output (AVLN XAML warnings included).
Run: `LANG=en_US.UTF-8 LC_ALL=en_US.UTF-8 dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git add src/Capacitor.App/Views/WorkContextView.axaml test/Capacitor.App.Tests.Unit/WorkContextViewSmokeTests.cs docs/superpowers/specs/2026-10-05-background-commands-design.md
/usr/bin/git commit -m "List background commands beside subagents in the sidebar" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Whole-branch verification

- [ ] **Step 1: Solution build**

Run: `dotnet build Capacitor.slnx 2>&1 | grep -E ' (warning|error) ' | sort -u`
Expected: no output.

- [ ] **Step 2: AOT publish check** (Cli.Core changed)

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 3: Linear-id check**

Run: `bash scripts/check-linear-ids.sh`
Expected: passes.

- [ ] **Step 4: Full suite**

Run: `dotnet test --solution Capacitor.slnx` (foreground; may approach 10 minutes — if it is cut off, run the four unit suites one by one instead). Known environmental daemon timing failures re-run alone.
Expected: all pass.

- [ ] **Step 5: Live check**

Launch the desktop app from this build, open a Claude session, have the agent run `sleep 60` with `run_in_background`, and confirm: the strip shows the command with its timer; the sidebar section "AGENTS & COMMANDS" lists it with the `$` glyph; it reads done after the notification. Repeat with the session opened through the remote (server) lane to confirm `backgroundTaskId` survives the server's events. Report what was and was not seen.
