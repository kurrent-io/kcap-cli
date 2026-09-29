# Subagents in the desktop chat — live count (PR 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish, beside the daemon's unchanged wait verdict, the number of subagents it believes a hosted Claude session is running, so the desktop chat's "Working for …" note and the rail row stay visibly busy while only subagents work.

**Architecture:** A subagent's own hooks (`SubagentStart`, its tool calls, `SubagentStop`) relay `{live, sent_at}` reports from `kcap hook` to a new `/{token}/claude/subagent` route on the daemon's loopback bridge; the orchestrator drops stale live reports, applies the rest to the agent's `AgentActivityClock` (per-id live/stopped state ordered by `sent_at`), and retires unreported ids through one shared timer whose single reschedule method sweeps every clock, arms the timer, and only then pulses status. `AgentStatusDto` gains a trailing `live_subagents`; the app folds it into one predicate, `SessionStatusDots.IsWorking`, read by the chat's activity note and the rail row.

**Tech Stack:** .NET 10, NativeAOT CLI + daemon (System.Text.Json source generation), Avalonia + ReactiveUI app, TUnit on Microsoft Testing Platform, WireMock.Net for the relay tests, Microsoft.Extensions.TimeProvider.Testing `FakeTimeProvider` (daemon, CLI and app test projects all reference it).

**Spec:** docs/superpowers/specs/2026-09-17-ai2840-desktop-subagents-design.md (decisions D7–D8). Depends on PR 1: docs/superpowers/plans/2026-09-17-ai2840-subagents-display.md

## Global Constraints

- Live ids are retired after **10 minutes** unreported (`AgentActivityClock.SubagentLiveness`); a live report stamped within **30 seconds** after its id's stop `sent_at` is ignored (`SubagentRestartWindow`); a report's stamp is honoured for **10 minutes of monotonic time** after it arrived (`SubagentStampRetention`), past which the next report for the id is taken on its freshness alone; a live report whose `sent_at` is more than **60 seconds** behind the orchestrator's `GetUtcNow()` is dropped in `HandleSubagent` before it reaches the clock (`AgentOrchestrator.SubagentLiveReportFreshness`). Stops are never aged out.
- The clock reads no wall time: only `TimeProvider.GetTimestamp`/`GetElapsedTime`, as its class doc demands; it only compares the `sent_at` values it is handed. Within a honoured stamp, a report stamped earlier than the latest one applied to its id is dropped.
- `LiveSubagents` is null until the first report of either kind, then the size of the live set with **no age filter at read time**. The count changes only through `SubagentSeen`, `SubagentStopped` and `TakeSubagentExpiries`.
- Published count (`AgentStatusDto.LiveSubagents`): null before any report, the clock's count while `Status == "Running"`, zero in any other status. Wire name `live_subagents`, trailing member, always emitted (null when absent).
- Route: `POST /{token}/claude/subagent`; `claude` is the only vendor; token rules, body cap (`MaxPermissionRequestBodyBytes`) and attribution identical to `input-wait`; 204 whether or not attributed; a body without a usable numeric `sent_at` is stamped with the bridge's clock on arrival.
- Relay payload: `{ session_id, agent_id, cwd, subagent_id, live, sent_at }`, `sent_at` = the hook's UTC clock in Unix milliseconds; 1 s cap and budget clamp identical to today's input-wait relay; best effort, silent. A subagent's hook **never** posts `input-wait`.
- `RescheduleSubagentExpiry(bool announce)` runs in one hold of its own lock, in this order: (1) return if disposal has begun; (2) `TakeSubagentExpiries()` on **every** agent's clock whatever its status; (3) set the timer to the earliest deadline or rest it; (4) pulse if `announce` or any clock retired an id. `DisposeAsync` takes the same lock to mark disposal and dispose the timer. `DaemonStatusNotifier.Pulse` is called inside the lock (it only bumps a generation and releases waiters asynchronously).
- No new `FrameType`, no `LocalControlCapabilities` entry: the count rides the existing status frame and nothing new is handled. No change to what `AwaitingInput`, `WaitGeneration`, `ActivitySeq` or `IdleForMs` mean; no input or turn signal alters the subagent sets.
- `SessionStatusDots.IsWorking` = `Status == "Running" && (AwaitingInput == false || LiveSubagents > 0)`; null counts as zero. The pending-card pause in `RefreshActivityNote` is unchanged.
- Rail: the dot pulses while the count is above zero, the meta line gains "N subagent(s)", the tooltip gains "N subagent(s) running"; badges and `NeedsYou` untouched; remote rows carry null and are unchanged.
- One type per file (a private nested record inside a class is fine). Comments: scarce, never historical, no spec/ticket coordinates. Commit subject: one imperative clause ending in ` (#966)`, at most 80 characters; every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`. Unused `using`s are build errors.
- Builds are warning-free, including `dotnet build src/Capacitor.App/Capacitor.App.csproj`. After touching Core/CLI/daemon, run the AOT publish check for BOTH shipping executables and expect no output:
  `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` and
  `dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`. `AgentStatusDto` is serialized by the source-generated `StatusIpcJsonContext`; a plain `int?` member needs no context change.
- TUnit filtering uses `--treenode-filter "/*/*/<ClassName>/*"`, never `--filter`.
- Run git from the worktree root (`/Users/alexey/dev/temp/kcap-cli/.capacitor/worktrees/agent-e90d8dfa163045`); commit only, never push.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` | `AgentStatusDto.LiveSubagents`, the trailing wire member. |
| `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs` | Pins `live_subagents` last in each agent, as null/0/positive; absence deserializes to null. |
| `src/Capacitor.Cli.Daemon/Services/SubagentExpiry.cs` (new) | The `(RetiredAny, NextDue)` result of one sweep. |
| `src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs` | Per-id subagent state under `_gate`; `SubagentSeen`, `SubagentStopped`, `LiveSubagents`, `TakeSubagentExpiries`. |
| `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentActivityClockSubagentTests.cs` (new) | Every clock rule under a `FakeTimeProvider`. |
| `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` | The `/subagent` route, `SubagentHandler`, arrival stamping, optional `TimeProvider`. |
| `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs` (new) | Route arms (204/400/404/413), attribution, stamp passthrough and arrival stamping; the held-report case through the orchestrator. |
| `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` | `TimeProvider` parameter, `HandleSubagent`, `RescheduleSubagentExpiry`, the shared timer and lock, disposal, the bridge wiring. |
| `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` | The published count rule. |
| `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorHarness.cs` | `BuildOrchestrator(..., timeProvider)`. |
| `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ManualTimerTimeProvider.cs` (new) | A clock whose timer fires only when the test says, for the race cases. |
| `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorSubagentTests.cs` (new) | Relay, freshness, published count, expiry timer, the race cases, disposal. |
| `src/Capacitor.Cli/Commands/DaemonHintRelay.cs` (renamed from `DaemonInputWaitRelay.cs`) | One relay, two messages: input-wait and subagent. |
| `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs` | Posts the subagent message for any hook with `agent_id`, ahead of every gate. |
| `src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs` | Calls the renamed input-wait message. |
| `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookSubagentRelayTests.cs` (new) | What a subagent's hook relays, with its stamp, and what silences it. |
| `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookInputWaitRelayTests.cs` | Narrowed: a subagent's tool call posts no `input-wait`. |
| `src/Capacitor.App/ViewModels/SessionStatusDots.cs` | `IsWorking`, the one busy predicate. |
| `src/Capacitor.App/ViewModels/ChatSessionInfo.cs` | Carries the count from `FromLocal`; null from `FromRemote`. |
| `src/Capacitor.App/Services/AgentRow.cs` | Carries the count from `FromLocal`. |
| `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` | `RefreshActivityNote` reads `IsWorking`. |
| `test/Capacitor.App.Tests.Unit/SessionStatusDotsTests.cs` (new) | `IsWorking` across verdict and count, null included. |
| `test/Capacitor.App.Tests.Unit/ChatSessionInfoTests.cs` (new) | The count rides the local session info and not the remote one. |
| `test/Capacitor.App.Tests.Unit/AgentRowTests.cs` | `FromLocal` carries the count. |
| `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` | The note holds on the count; the pending-card pause applies to it. |
| `src/Capacitor.App/ViewModels/RailSessionViewModel.cs` | `DotPulses`, meta and tooltip text for the count. |
| `src/Capacitor.App/Views/SessionRailView.axaml` | The dot's pulsing class binds `DotPulses`. |
| `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs` | Pulse, meta, tooltip; badges and `NeedsYou` unchanged; a remote row unchanged. |
| `docs/CHANGES.md` | The reasoning entry. |

---

### Task 1: Wire contract — `AgentStatusDto.LiveSubagents`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs:82-85`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `AgentStatusDto(..., string? TranscriptFormat = null, int? LiveSubagents = null)` — positional record, trailing member, snake_case `live_subagents` on the wire through the existing `StatusIpcJsonContext`. Every existing positional construction stays valid.

- [ ] **Step 1: Write the failing tests**

Append inside the `StatusIpcJsonTests` class, after `Old_agent_json_without_transcript_format_deserializes_to_null`:

```csharp
    /// The daemon's count of subagents it believes are running: null until the agent's first
    /// subagent report, so an older daemon and a session that spawned none read alike.
    [Test]
    public async Task Live_subagents_is_the_trailing_member_and_always_emitted() {
        var unset    = new AgentStatusDto("a", "agent", "claude", null, "Running", null, null, null, DateTime.UnixEpoch, null, null, TranscriptFormat: TranscriptFormats.Vendor);
        var zero     = unset with { LiveSubagents = 0 };
        var positive = unset with { LiveSubagents = 3 };

        await Assert.That(JsonSerializer.Serialize(unset, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":"vendor","live_subagents":null}""");
        await Assert.That(JsonSerializer.Serialize(zero, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":"vendor","live_subagents":0}""");
        await Assert.That(JsonSerializer.Serialize(positive, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":"vendor","live_subagents":3}""");
    }

    [Test]
    public async Task Old_agent_json_without_live_subagents_deserializes_to_null() {
        var json = """{"id":"a","kind":"agent","vendor":"claude","repo_path":null,"status":"Running","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null,"requester_display":null,"awaiting_input":true,"transcript_format":"vendor"}""";
        var dto = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.AgentStatusDto)!;
        await Assert.That(dto.LiveSubagents).IsNull();
        await Assert.That(dto.TranscriptFormat).IsEqualTo(TranscriptFormats.Vendor);
    }
```

- [ ] **Step 2: Move the pinned payloads' tail**

In the same file, every pinned string that ends an agent object with `"transcript_format":null}` now ends with `"transcript_format":null,"live_subagents":null}`. Replace all ten occurrences (two inside the line-32 payload of `DaemonStatus_serializes_exactly_with_nulls_present_and_pinned_field_order`, one each on the two `EndsWith` lines of `Transcript_path_serializes_after_title_and_null_is_emitted`, `Checkout_members_serialize_last_and_nulls_are_emitted`, `Session_and_branch_members_serialize_last_and_nulls_are_emitted`, the interpolated one in `Awaiting_input_serializes_before_transcript_format_and_never_omitted`, and the `unset` line of the transcript-format test). In `Transcript_format_is_the_trailing_member_and_always_emitted`, rename the test to `Transcript_format_serializes_before_live_subagents_and_always_emitted` and change its three assertions to:

```csharp
        await Assert.That(JsonSerializer.Serialize(vendor, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","awaiting_input":null,"transcript_format":"vendor","live_subagents":null}""");
        await Assert.That(JsonSerializer.Serialize(envelopes, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":"envelopes","live_subagents":null}""");
        await Assert.That(JsonSerializer.Serialize(unset, StatusIpcJsonContext.Default.AgentStatusDto)).EndsWith(""","transcript_format":null,"live_subagents":null}""");
```

`Unknown_members_in_a_payload_deserialize_without_error` stays untouched.

- [ ] **Step 3: Run the class to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/StatusIpcJsonTests/*"
```
Expected: build error — `AgentStatusDto` has no `LiveSubagents`.

- [ ] **Step 4: Add the member**

In `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`, change the last parameter of `AgentStatusDto`

```csharp
    string? TranscriptFormat = null);
```
to
```csharp
    string? TranscriptFormat = null,
    // How many subagents the daemon believes are running: null until the agent's first subagent
    // report (an older daemon, a vendor whose hooks report none, or a session that has spawned
    // none yet), then a number — the clock's count while Running, zero in any other status.
    int? LiveSubagents = null);
```

- [ ] **Step 5: Run the class to verify it passes**

Same command as Step 3. Expected: every test in `StatusIpcJsonTests` passes (17 tests).

- [ ] **Step 6: Build the consumers and run the AOT check**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: build succeeds with no warnings; both greps print nothing.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs
git commit -q -m "Add live_subagents to the local status payload (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: Clock — per-id subagent state on `AgentActivityClock`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/SubagentExpiry.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs` (new members after `ClearAwaitingInputSince`, before `SetLaunchStage`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentActivityClockSubagentTests.cs` (new)

**Interfaces:**
- Consumes: the existing `AgentActivityClock(TimeProvider time)` and its `_gate`.
- Produces:
  - `internal readonly record struct SubagentExpiry(bool RetiredAny, TimeSpan? NextDue);`
  - `AgentActivityClock.SubagentLiveness` (`TimeSpan`, 10 min), `AgentActivityClock.SubagentStampRetention` (10 min), `AgentActivityClock.SubagentRestartWindow` (30 s) — `internal static readonly`.
  - `public int? LiveSubagents { get; }`
  - `public bool SubagentSeen(string id, long sentAtMs)` — returns whether the live set changed.
  - `public bool SubagentStopped(string id, long sentAtMs)` — returns whether the live set changed or this was the first report.
  - `public SubagentExpiry TakeSubagentExpiries()`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentActivityClockSubagentTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The clock's subagent sets: what a report adds or removes, which reports it drops, and that
/// nothing here moves with time alone. Stamps are the hooks' wall clock and are inputs here; the
/// clock reads only its monotonic time.
public class AgentActivityClockSubagentTests {
    static readonly TimeSpan Liveness  = AgentActivityClock.SubagentLiveness;
    static readonly TimeSpan Retention = AgentActivityClock.SubagentStampRetention;
    static readonly TimeSpan Window    = AgentActivityClock.SubagentRestartWindow;

    static long Ms(FakeTimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();

    [Test]
    public async Task Null_before_any_report_and_a_number_after_the_first_of_either_kind() {
        var seen    = new AgentActivityClock(new FakeTimeProvider());
        var stopped = new AgentActivityClock(new FakeTimeProvider());

        await Assert.That(seen.LiveSubagents).IsNull();
        await Assert.That(stopped.LiveSubagents).IsNull();

        await Assert.That(seen.SubagentSeen("a", 1_000)).IsTrue();
        await Assert.That(seen.LiveSubagents).IsEqualTo(1);

        await Assert.That(stopped.SubagentStopped("a", 1_000)).IsTrue();
        await Assert.That(stopped.LiveSubagents).IsEqualTo(0);
    }

    [Test]
    public async Task Seen_refreshed_and_stopped_report_only_the_changes_to_the_live_set() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        await Assert.That(clock.SubagentSeen("a", 1_000)).IsTrue();
        await Assert.That(clock.SubagentSeen("a", 2_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
        await Assert.That(clock.SubagentStopped("a", 3_000)).IsTrue();
        await Assert.That(clock.SubagentStopped("a", 4_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
    }

    [Test]
    public async Task A_live_report_stamped_before_the_latest_applied_one_is_dropped_and_so_is_such_a_stop() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SubagentSeen("a", 1_000);
        clock.SubagentStopped("a", 3_000);
        await Assert.That(clock.SubagentSeen("a", 2_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);

        clock.SubagentSeen("b", 5_000);
        await Assert.That(clock.SubagentStopped("b", 4_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task A_start_stamped_within_the_window_after_its_stop_is_ignored_and_a_later_one_counts_again() {
        var clock  = new AgentActivityClock(new FakeTimeProvider());
        var stopAt = 10_000L;
        var window = (long) Window.TotalMilliseconds;

        await Assert.That(clock.SubagentStopped("a", stopAt)).IsTrue();
        await Assert.That(clock.SubagentSeen("a", stopAt + window)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
        await Assert.That(clock.SubagentSeen("a", stopAt + window + 1)).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task A_duplicate_stop_does_not_move_the_window() {
        var clock = new AgentActivityClock(new FakeTimeProvider());

        clock.SubagentSeen("a", 1_000);
        clock.SubagentStopped("a", 10_000);
        await Assert.That(clock.SubagentStopped("a", 40_000)).IsFalse();
        await Assert.That(clock.SubagentSeen("a", 45_000)).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    /// The bridge stamps a report that carries no sent_at with its arrival time, so such reports
    /// reach the clock in arrival order.
    [Test]
    public async Task A_report_stamped_on_arrival_is_ordered_by_its_arrival() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);

        clock.SubagentSeen("a", Ms(time));
        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentStopped("a", Ms(time))).IsTrue();
        time.Advance(Window + TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentSeen("a", Ms(time))).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task After_a_backward_wall_clock_step_the_resumed_id_is_rejected_until_the_stops_stamp_expires() {
        var time   = new FakeTimeProvider();
        var clock  = new AgentActivityClock(time);
        var stopAt = 1_000_000L;
        clock.SubagentSeen("a", stopAt - 1_000);
        clock.SubagentStopped("a", stopAt);
        var steppedBack = stopAt - (long) TimeSpan.FromMinutes(11).TotalMilliseconds;

        await Assert.That(clock.SubagentSeen("a", steppedBack)).IsFalse();
        time.Advance(Retention - TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentSeen("a", steppedBack + 1_000)).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(clock.SubagentSeen("a", steppedBack + 2_000)).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task The_count_does_not_move_with_time_alone() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));

        time.Advance(Liveness + TimeSpan.FromMinutes(1));

        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task Take_retires_each_aged_id_exactly_once_and_returns_the_time_to_the_oldest_survivor() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(TimeSpan.FromMinutes(1));
        clock.SubagentSeen("b", Ms(time));
        time.Advance(Liveness - TimeSpan.FromMinutes(1));

        var first = clock.TakeSubagentExpiries();
        await Assert.That(first.RetiredAny).IsTrue();
        await Assert.That(first.NextDue).IsEqualTo(TimeSpan.FromMinutes(1));
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);

        var again = clock.TakeSubagentExpiries();
        await Assert.That(again.RetiredAny).IsFalse();
        await Assert.That(again.NextDue).IsEqualTo(TimeSpan.FromMinutes(1));

        time.Advance(TimeSpan.FromMinutes(1));
        var last = clock.TakeSubagentExpiries();
        await Assert.That(last.RetiredAny).IsTrue();
        await Assert.That(last.NextDue).IsNull();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
        await Assert.That(clock.TakeSubagentExpiries()).IsEqualTo(new SubagentExpiry(false, null));
    }

    [Test]
    public async Task A_heartbeat_renews_an_ids_deadline() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(TimeSpan.FromMinutes(9));

        await Assert.That(clock.SubagentSeen("a", Ms(time))).IsFalse();
        await Assert.That(clock.TakeSubagentExpiries().NextDue).IsEqualTo(Liveness);

        time.Advance(TimeSpan.FromMinutes(9));
        var expiry = clock.TakeSubagentExpiries();
        await Assert.That(expiry.RetiredAny).IsFalse();
        await Assert.That(expiry.NextDue).IsEqualTo(TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task A_heartbeat_for_an_aged_id_not_yet_retired_keeps_it_counted_and_reports_no_change() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(Liveness + TimeSpan.FromMinutes(1));

        await Assert.That(clock.SubagentSeen("a", Ms(time))).IsFalse();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
        var expiry = clock.TakeSubagentExpiries();
        await Assert.That(expiry.RetiredAny).IsFalse();
        await Assert.That(expiry.NextDue).IsEqualTo(Liveness);
    }

    [Test]
    public async Task A_stop_for_an_aged_id_not_yet_retired_removes_it_and_reports_the_change() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        time.Advance(Liveness + TimeSpan.FromMinutes(1));

        await Assert.That(clock.SubagentStopped("a", Ms(time))).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(0);
        await Assert.That(clock.TakeSubagentExpiries()).IsEqualTo(new SubagentExpiry(false, null));
    }

    [Test]
    public async Task The_parent_waiting_while_a_child_keeps_reporting_stays_waiting_with_the_child_counted() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetAwaitingInput(true);

        clock.SubagentSeen("child", Ms(time));
        time.Advance(TimeSpan.FromSeconds(30));
        clock.SubagentSeen("child", Ms(time));

        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task Subagent_reports_leave_the_wait_generation_the_seq_and_the_idle_time_alone() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SetAwaitingInput(true);
        time.Advance(TimeSpan.FromSeconds(5));
        var (generation, seq, idle) = (clock.WaitGeneration, clock.ActivitySeq, clock.IdleForMs);

        clock.SubagentSeen("a", Ms(time));
        clock.SubagentStopped("a", Ms(time) + 1);
        clock.TakeSubagentExpiries();

        await Assert.That(clock.WaitGeneration).IsEqualTo(generation);
        await Assert.That(clock.ActivitySeq).IsEqualTo(seq);
        await Assert.That(clock.IdleForMs).IsEqualTo(idle);
        await Assert.That(clock.AwaitingInput).IsTrue();
    }

    [Test]
    public async Task No_input_or_turn_signal_alters_the_subagent_sets() {
        var time  = new FakeTimeProvider();
        var clock = new AgentActivityClock(time);
        clock.SubagentSeen("a", Ms(time));
        clock.SetAwaitingInput(true);
        var sampled = clock.WaitGeneration;
        clock.SetAwaitingInput(true);

        clock.ClearAwaitingInputSince(sampled);
        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);

        clock.ClearAwaitingInputSince(clock.WaitGeneration);
        clock.SetTurnInFlight(true);
        clock.SetTurnInFlight(false);
        await Assert.That(clock.AwaitingInput).IsTrue();
        await Assert.That(clock.LiveSubagents).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run the class to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentActivityClockSubagentTests/*"
```
Expected: build error — `AgentActivityClock` has no `SubagentSeen`.

- [ ] **Step 3: Create the sweep result**

Create `src/Capacitor.Cli.Daemon/Services/SubagentExpiry.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// <summary>One <see cref="AgentActivityClock.TakeSubagentExpiries"/> result: whether a live id
/// was retired for age, and the time until the oldest survivor falls due (null when none is
/// live). Both come from the same instant, so an id is either retired by exactly one call or
/// counted in the deadline that call returns.</summary>
internal readonly record struct SubagentExpiry(bool RetiredAny, TimeSpan? NextDue);
```

- [ ] **Step 4: Add the clock members**

In `src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs`, after the closing brace of `ClearAwaitingInputSince` (before the `SetLaunchStage` doc comment), insert:

```csharp
    /// <summary>A subagent id the hooks have reported: whether its last report said alive, the
    /// monotonic instant that report arrived, and the <c>sent_at</c> it carried.</summary>
    sealed class SubagentRecord {
        public bool Live;
        public long ReportedTimestamp;
        public long SentAtMs;
    }

    readonly Dictionary<string, SubagentRecord> _subagents = new(StringComparer.Ordinal);
    bool _subagentsReported;

    /// <summary>A live id unreported this long is retired by <see cref="TakeSubagentExpiries"/>:
    /// a SubagentStop is not guaranteed, so a lost one costs at most this much stale count.</summary>
    internal static readonly TimeSpan SubagentLiveness = TimeSpan.FromMinutes(10);

    /// <summary>How long, on the monotonic clock, a report's <c>sent_at</c> is compared against
    /// later ones. Hooks and daemon read one wall clock, so a step misleads the comparison; past
    /// this the next report for the id is taken on its freshness alone.</summary>
    internal static readonly TimeSpan SubagentStampRetention = TimeSpan.FromMinutes(10);

    /// <summary>A live report stamped this soon after its id's stop is the start hook scheduled
    /// after its own stop hook (both run asynchronously), not a resume.</summary>
    internal static readonly TimeSpan SubagentRestartWindow = TimeSpan.FromSeconds(30);

    /// <summary>Null until the first subagent report of either kind, then the number of ids
    /// reported alive and not since stopped or retired. No age filter at read time: the count
    /// moves only through <see cref="SubagentSeen"/>, <see cref="SubagentStopped"/> and
    /// <see cref="TakeSubagentExpiries"/>, each of which reports its change to the caller, so a
    /// published snapshot and the report that settles an id can never disagree unannounced.</summary>
    public int? LiveSubagents {
        get {
            lock (_gate) {
                if (!_subagentsReported) return null;
                var live = 0;
                foreach (var record in _subagents.Values) if (record.Live) live++;
                return live;
            }
        }
    }

    /// <summary>A hook reported the id alive. Returns whether the live set changed. Dropped when
    /// stamped earlier than the latest report applied to the id, or within
    /// <see cref="SubagentRestartWindow"/> after the id's stop, while that stamp is honoured.
    /// Raises no callback: the orchestrator reschedules expiry before it announces.</summary>
    public bool SubagentSeen(string id, long sentAtMs) {
        lock (_gate) {
            _subagentsReported = true;
            var now = time.GetTimestamp();

            if (!_subagents.TryGetValue(id, out var record)) {
                _subagents[id] = new SubagentRecord { Live = true, ReportedTimestamp = now, SentAtMs = sentAtMs };
                return true;
            }

            if (StampHonoured(record)) {
                if (sentAtMs < record.SentAtMs) return false;
                if (!record.Live && sentAtMs - record.SentAtMs <= SubagentRestartWindow.TotalMilliseconds) return false;
            }

            var added = !record.Live;
            record.Live = true;
            record.ReportedTimestamp = now;
            record.SentAtMs = sentAtMs;
            return added;
        }
    }

    /// <summary>A hook reported the id gone. Returns whether the live set changed or this was the
    /// first report. A stop for an id already stopped changes nothing, the stamp included; one
    /// stamped earlier than a later live report is the overtaken stop of a resumed run.</summary>
    public bool SubagentStopped(string id, long sentAtMs) {
        lock (_gate) {
            var first = !_subagentsReported;
            _subagentsReported = true;
            var now = time.GetTimestamp();

            if (!_subagents.TryGetValue(id, out var record)) {
                _subagents[id] = new SubagentRecord { Live = false, ReportedTimestamp = now, SentAtMs = sentAtMs };
                return first;
            }

            if (!record.Live) return false;
            if (StampHonoured(record) && sentAtMs < record.SentAtMs) return false;

            record.Live = false;
            record.ReportedTimestamp = now;
            record.SentAtMs = sentAtMs;
            return true;
        }
    }

    /// <summary>The only place an id is retired for age. At one instant under the gate: retires
    /// every live id unreported for <see cref="SubagentLiveness"/>, drops stop records past
    /// <see cref="SubagentStampRetention"/>, and reports both what it retired and when the oldest
    /// survivor falls due.</summary>
    public SubagentExpiry TakeSubagentExpiries() {
        lock (_gate) {
            var retired = false;
            TimeSpan? next = null;
            List<string>? gone = null;

            foreach (var (id, record) in _subagents) {
                var age = time.GetElapsedTime(record.ReportedTimestamp);
                if (record.Live && age < SubagentLiveness) {
                    var due = SubagentLiveness - age;
                    if (next is null || due < next) next = due;
                    continue;
                }
                if (record.Live) retired = true;
                else if (age < SubagentStampRetention) continue;
                (gone ??= []).Add(id);
            }

            if (gone is not null) foreach (var id in gone) _subagents.Remove(id);

            return new SubagentExpiry(retired, next);
        }
    }

    // Caller must hold _gate.
    bool StampHonoured(SubagentRecord record) =>
        time.GetElapsedTime(record.ReportedTimestamp) < SubagentStampRetention;
```

- [ ] **Step 5: Run the class to verify it passes**

Same command as Step 2. Expected: all 16 tests pass. Then run the neighbouring class to confirm nothing else on the clock moved:

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentActivityClockTests/*"
```
Expected: all pass.

- [ ] **Step 6: AOT check**

```bash
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: no output.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/SubagentExpiry.cs src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentActivityClockSubagentTests.cs
git commit -q -m "Count live subagents on the activity clock (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Bridge — the `/subagent` route and `SubagentHandler`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` (primary constructor lines 31-37; constants line 39-40; handler property after line 71; routing after the input-wait branch at lines 475-480; new method after `HandleInputWaitAsync`, which ends at line 954)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs` (new)

**Interfaces:**
- Consumes: the bridge's existing `AttributeHandler`, `ReadCappedBodyAsync`, `Str`, `Close`, `PermissionWire.Canonical`, `MaxPermissionRequestBodyBytes`, `RequestReadTimeout`.
- Produces:
  - `LocalPermissionBridge(ServerConnection server, ILogger<LocalPermissionBridge> logger, ILoopbackPortSource ports, PermissionPromptBroker? broker = null, PermissionDecisionLog? decisionLog = null, TimeProvider? time = null)`.
  - `internal Action<string, string, bool, long>? SubagentHandler { get; set; }` — `(agentId, subagentId, live, sentAtMs)`.
  - Route `POST /{token}/claude/subagent`: 404 for an unknown token or any other vendor, 413 over the body cap, 400 without a canonical `session_id`, a non-empty `subagent_id` and a boolean `live`, else 204; `sent_at` passes through when it is a JSON integer, otherwise the bridge's `GetUtcNow()` in Unix ms at arrival.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The subagent relay: a Claude subagent's hook tells the daemon the subagent is alive or gone,
/// and the bridge hands the attributed agent's report, with its stamp, to the orchestrator.
public class LocalPermissionBridgeSubagentTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public LocalPermissionBridge Bridge { get; }
        public FakeTimeProvider Time { get; } = new();
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public List<(string AgentId, string SubagentId, bool Live, long SentAt)> Seen { get; } = [];

        public Harness(string? attributeTo = "agent-1") {
            Bridge = new LocalPermissionBridge(new FakeServerConnection(respond: null), NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, time: Time) {
                AttributeHandler = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
                SubagentHandler  = (id, subagent, live, sentAt) => Seen.Add((id, subagent, live, sentAt)),
            };
        }

        public Task StartAsync() => Bridge.StartAsync(CancellationToken.None);

        public Task<HttpResponseMessage> PostAsync(object body, string vendor = "claude", string? token = null) {
            var baseUrl = token is null ? Bridge.BaseUrl! : $"http://127.0.0.1:{new Uri(Bridge.BaseUrl!).Port}/{token}";
            return Client.PostAsync($"{baseUrl}/{vendor}/subagent", JsonContent.Create(body));
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_live_report_reaches_the_handler_with_the_attributed_agent_and_its_stamp() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", cwd = "/repo", subagent_id = "sub-1", live = true, sent_at = 1_700_000_000_000L });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", "sub-1", true, 1_700_000_000_000L));
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_stop_report_relays_live_false() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = false, sent_at = 1_700_000_000_000L });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", "sub-1", false, 1_700_000_000_000L));
    }

    /// An older hook sends no stamp, and a stamp that is not an integer is no stamp: the bridge's
    /// own clock at arrival keeps such reports in arrival order.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_report_without_a_usable_stamp_is_stamped_on_arrival() {
        await using var h = new Harness();
        await h.StartAsync();
        var arrival = h.Time.GetUtcNow().ToUnixTimeMilliseconds();

        var missing = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true });
        var text    = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-2", live = true, sent_at = "soon" });

        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(text.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Select(s => s.SentAt)).IsEquivalentTo(new[] { arrival, arrival });
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_report_missing_a_session_a_subagent_id_or_a_verdict_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var noSession  = await h.PostAsync(new { agent_id = "agent-1", subagent_id = "sub-1", live = true });
        var noSubagent = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", live = true });
        var noVerdict  = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1" });

        await Assert.That(noSession.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noSubagent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noVerdict.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// A report the ladder cannot place is not an error the hook can act on, so it is acknowledged
    /// and dropped rather than refused.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task An_unattributed_report_is_acknowledged_and_dropped() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, subagent_id = "sub-1", live = true, sent_at = 1L });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    [Arguments("codex")]
    [Arguments("cursor")]
    public async Task Only_claude_has_a_subagent_route(string vendor) {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true, sent_at = 1L }, vendor: vendor);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task An_unknown_token_has_no_subagent_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true, sent_at = 1L }, token: "nope");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task An_oversized_body_is_refused() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true, pad = new string('x', LocalPermissionBridge.MaxPermissionRequestBodyBytes) });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(h.Seen).IsEmpty();
    }
}
```

- [ ] **Step 2: Run the class to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeSubagentTests/*"
```
Expected: build error — no `time` parameter, no `SubagentHandler`.

- [ ] **Step 3: Take the clock and declare the handler**

In `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs`, change the primary constructor

```csharp
        PermissionPromptBroker?        broker      = null,
        PermissionDecisionLog?         decisionLog = null
    ) : IHostedService, IAsyncDisposable {
    const int    MaxBindAttempts = 15;
    const string PathSuffix      = "/permission-request";
    const string InputWaitSuffix = "/input-wait";
```
to
```csharp
        PermissionPromptBroker?        broker      = null,
        PermissionDecisionLog?         decisionLog = null,
        TimeProvider?                  time        = null
    ) : IHostedService, IAsyncDisposable {
    const int    MaxBindAttempts = 15;
    const string PathSuffix      = "/permission-request";
    const string InputWaitSuffix = "/input-wait";
    const string SubagentSuffix  = "/subagent";
```

After the `_decisionLog` field (line 47) add:

```csharp
    // Stamps a subagent report that arrives without a usable sent_at.
    readonly TimeProvider _time = time ?? TimeProvider.System;
```

After the `InputWaitHandler` property (line 71) add:

```csharp
    /// Assigned by the orchestrator like <see cref="InputWaitHandler"/>: the attributed agent id,
    /// the subagent id a Claude hook reported, whether it reported it alive, and the report's
    /// <c>sent_at</c> in Unix milliseconds. Null = reports are acknowledged and dropped.
    internal Action<string, string, bool, long>? SubagentHandler { get; set; }
```

- [ ] **Step 4: Route the path**

In `HandleAsync`, directly after the input-wait branch

```csharp
            if (context.Request.HttpMethod == "POST" && path is not null
             && path.EndsWith(InputWaitSuffix, StringComparison.Ordinal)) {
                await HandleInputWaitAsync(context, path);

                return;
            }
```
add
```csharp
            if (context.Request.HttpMethod == "POST" && path is not null
             && path.EndsWith(SubagentSuffix, StringComparison.Ordinal)) {
                await HandleSubagentAsync(context, path);

                return;
            }
```

- [ ] **Step 5: Add the handler**

After the closing brace of `HandleInputWaitAsync` (before `static string? Str(`), insert:

```csharp
    /// <summary>
    /// A Claude subagent's hook reporting the subagent alive or gone:
    /// <c>/{token}/claude/subagent</c> with <c>session_id</c>, <c>subagent_id</c>, <c>live</c>,
    /// <c>sent_at</c>, and the <c>agent_id</c>/<c>cwd</c> the attribution ladder reads. Same
    /// token rules as the permission path; only Claude's hooks report subagents. A body whose
    /// <c>sent_at</c> is not an integer is stamped on arrival, so an older hook's reports keep
    /// their arrival order. Answers 204 whether or not the ladder placed the agent.
    /// </summary>
    async Task HandleSubagentAsync(HttpListenerContext context, string path) {
        var trimmed    = path.TrimStart('/');
        var firstSlash = trimmed.IndexOf('/');

        if (firstSlash <= 0) {
            Close(context, 404);

            return;
        }

        var token = trimmed[..firstSlash];

        if (!string.Equals(token, _sharedToken, StringComparison.Ordinal) && !_reviewerTokens.ContainsKey(token)) {
            Close(context, 404);

            return;
        }

        var afterToken = path[(token.Length + 2)..];
        var vendor     = afterToken.Length > SubagentSuffix.Length ? afterToken[..^SubagentSuffix.Length] : "";

        if (vendor is not "claude") {
            Close(context, 404);

            return;
        }

        using var readCts = new CancellationTokenSource(RequestReadTimeout);
        var       body    = await ReadCappedBodyAsync(context.Request.InputStream, MaxPermissionRequestBodyBytes, readCts.Token);

        if (body is null) {
            Close(context, 413);

            return;
        }

        JsonObject? node;

        try {
            node = JsonNode.Parse(body) as JsonObject;
        } catch (JsonException) {
            node = null;
        }

        var sessionId  = PermissionWire.Canonical(Str(node, "session_id"));
        var subagentId = Str(node, "subagent_id");
        var live       = node?["live"] is JsonValue verdict && verdict.TryGetValue<bool>(out var l) ? l : (bool?) null;

        if (sessionId is null || string.IsNullOrEmpty(subagentId) || live is null) {
            Close(context, 400);

            return;
        }

        var sentAt = node?["sent_at"] is JsonValue stamp && stamp.TryGetValue<long>(out var s)
            ? s
            : _time.GetUtcNow().ToUnixTimeMilliseconds();

        var attributed = AttributeHandler?.Invoke(new PermissionAttribution(Str(node, "agent_id"), sessionId, Str(node, "cwd")));
        if (attributed is { } agent) SubagentHandler?.Invoke(agent.AgentId, subagentId, live.Value, sentAt);

        Close(context, 204);
    }
```

- [ ] **Step 6: Run the class to verify it passes**

Same command as Step 2. Expected: all 9 tests pass. Also run the neighbour:

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeInputWaitTests/*"
```
Expected: all pass.

- [ ] **Step 7: AOT check**

```bash
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: no output.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs
git commit -q -m "Route subagent reports through the permission bridge (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Orchestrator — `HandleSubagent`, the shared expiry timer, the published count

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (fields near line 434; constructor parameters at lines 663-696 and body at lines 713-714; bridge wiring at line 768; new methods after `HandleInputWait` at lines 971-976; `CreateActivityClock` at lines 1762-1769; `DisposeAsync` finally block at lines 5306-5317)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:80-81`
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorHarness.cs` (`BuildOrchestrator` parameters, `HarnessOrchestrator`)
- Create: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ManualTimerTimeProvider.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorSubagentTests.cs` (new)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs` (append the held-report case)

**Interfaces:**
- Consumes: Task 2's `SubagentSeen`/`SubagentStopped`/`LiveSubagents`/`TakeSubagentExpiries`/`SubagentExpiry`; Task 3's `SubagentHandler` and route; the existing `_statusNotifier`, `_agents`, `SnapshotAgentsForStatus`, `StatusNotifierForTest`, `PermissionBridgeForTest`, `SeedAgentForTest`, `SetAgentStatus`, `BeforeHandlerRunsForTest`.
- Produces:
  - `AgentOrchestrator(..., PolicySnapshotProvider? policySnapshots = null, TimeProvider? timeProvider = null)` — DI resolves the registered `TimeProvider.System`; every clock `CreateActivityClock` builds and the expiry timer use it.
  - `internal static readonly TimeSpan AgentOrchestrator.SubagentLiveReportFreshness` (60 s).
  - `internal Action? AgentOrchestrator.BeforeSubagentSweepForTest { get; set; }` — runs under the expiry lock after the disposal check, before the sweep.
  - `AgentOrchestratorHarness.BuildOrchestrator(..., IHttpClientFactory? httpClientFactory = null, TimeProvider? timeProvider = null)`.
  - `ManualTimerTimeProvider` with `Advance(TimeSpan)`, `Timer` (`ManualTimer` with `Due`, `Changes`, `Disposed`, `Fire()`).
  - `AgentStatusDto.LiveSubagents` published as null before any report, the clock's count while `Running`, zero otherwise.

- [ ] **Step 1: Write the manual-timer clock**

Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ManualTimerTimeProvider.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A clock whose timer never fires on its own: the test advances time and dispatches the callback
/// itself, so "the deadline has passed but the callback has not run yet" is a state it can hold for
/// as long as it likes. Wall clock and monotonic timestamp move together, as on a FakeTimeProvider.
sealed class ManualTimerTimeProvider : TimeProvider {
    long           _ticks;
    DateTimeOffset _utcNow = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// The last timer created on this provider: the orchestrator's, since nothing else in these
    /// tests creates one.
    public ManualTimer? Timer { get; private set; }

    public override long           TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long           GetTimestamp()     => _ticks;
    public override DateTimeOffset GetUtcNow()        => _utcNow;

    public void Advance(TimeSpan by) {
        _ticks  += by.Ticks;
        _utcNow += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        Timer = new ManualTimer(callback, state, dueTime);

    public sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer {
        /// The due time of the last Change; Timeout.InfiniteTimeSpan when rested.
        public TimeSpan Due      { get; private set; } = dueTime;
        public int      Changes  { get; private set; }
        public bool     Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) {
            Due = dueTime;
            Changes++;
            return true;
        }

        public void Fire() => callback(state);

        public void      Dispose()      => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return default; }
    }
}
```

- [ ] **Step 2: Write the failing orchestrator tests**

Create `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorSubagentTests.cs`:

```csharp
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The orchestrator's half of the subagent count: a relayed report lands on the attributed agent's
/// clock, the published count follows the agent's status, and one shared timer retires aged ids so
/// that every expiry is announced exactly once, whichever call meets it. A FakeTimeProvider fires
/// the timer as time advances; a ManualTimerTimeProvider holds the callback until the test runs it.
public class AgentOrchestratorSubagentTests {
    static readonly TimeSpan Liveness = AgentActivityClock.SubagentLiveness;

    static AgentOrchestrator Build(TimeProvider time) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);

    static long Ms(TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();

    static int? Published(AgentOrchestrator orch, string agentId) =>
        orch.SnapshotAgentsForStatus().Single(a => a.Id == agentId).LiveSubagents;

    [Test]
    public async Task A_relayed_report_reaches_the_attributed_agents_clock_and_pulses_on_a_changed_value() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-1");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;
        var             v0    = orch.StatusNotifierForTest.Version;

        relay(agent.Id, "child-a", true, Ms(time));

        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);

        var v1 = orch.StatusNotifierForTest.Version;
        relay(agent.Id, "child-a", true, Ms(time));

        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v1);

        relay("somebody-else", "child-b", true, Ms(time));
        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task An_unchanged_report_pulses_only_when_its_call_retired_an_expired_id() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-2");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        relay(agent.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(2);

        var v0 = orch.StatusNotifierForTest.Version;
        relay(agent.Id, "child-b", true, Ms(time));

        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);

        var v1 = orch.StatusNotifierForTest.Version;
        relay(agent.Id, "child-b", true, Ms(time));
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v1);
    }

    /// A live report says "alive now", so an old one says nothing; a stop is a fact whatever its
    /// age. That the stop reached the clock shows in the restart window it opens.
    [Test]
    public async Task A_live_report_more_than_60_seconds_old_never_reaches_the_clock_and_a_stop_that_old_still_does() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-3");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time) - 60_001);
        await Assert.That(agent.ActivityClock.LiveSubagents).IsNull();

        relay(agent.Id, "child-a", true, Ms(time) - 60_000);
        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);

        relay(agent.Id, "child-b", false, Ms(time) - 61_000);
        relay(agent.Id, "child-b", true, Ms(time) - 40_000);
        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task The_published_count_is_null_before_any_report_the_clocks_count_while_running_and_zero_otherwise() {
        var time = new FakeTimeProvider();
        await using var orch     = Build(time);
        var             running  = orch.SeedAgentForTest("run");
        var             starting = orch.SeedAgentForTest("start", status: "Starting");
        var             done     = orch.SeedAgentForTest("done", status: "Completed");
        var             relay    = orch.PermissionBridgeForTest.SubagentHandler!;

        foreach (var id in (string[]) ["run", "start", "done"]) await Assert.That(Published(orch, id)).IsNull();

        relay(running.Id, "child", true, Ms(time));
        relay(starting.Id, "child", true, Ms(time));
        relay(done.Id, "child", true, Ms(time));

        await Assert.That(Published(orch, "run")).IsEqualTo(1);
        await Assert.That(Published(orch, "start")).IsEqualTo(0);
        await Assert.That(Published(orch, "done")).IsEqualTo(0);
        await Assert.That(starting.ActivityClock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task The_timer_pulses_at_the_expiry_and_re_arms_for_the_next() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-5");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(1));
        relay(agent.Id, "child-b", true, Ms(time));
        var v0 = orch.StatusNotifierForTest.Version;

        time.Advance(TimeSpan.FromMinutes(9));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);

        time.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 2);
    }

    [Test]
    public async Task A_callback_held_past_the_second_deadline_publishes_a_count_of_neither() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-6");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(1));
        relay(agent.Id, "child-b", true, Ms(time));
        await Assert.That(time.Timer!.Due).IsEqualTo(TimeSpan.FromMinutes(9));

        time.Advance(TimeSpan.FromMinutes(10));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(2);
        var v0 = orch.StatusNotifierForTest.Version;

        time.Timer.Fire();

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
        await Assert.That(time.Timer.Due).IsEqualTo(Timeout.InfiniteTimeSpan);
    }

    [Test]
    public async Task Another_sessions_unchanged_heartbeat_publishes_a_zero_whose_deadline_passed_before_the_call_began() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             a     = orch.SeedAgentForTest("a");
        var             b     = orch.SeedAgentForTest("b");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(a.Id, "child-a", true, Ms(time));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Published(orch, a.Id)).IsEqualTo(1);
        var v0 = orch.StatusNotifierForTest.Version;

        relay(b.Id, "child-b", true, Ms(time));

        await Assert.That(Published(orch, a.Id)).IsEqualTo(0);
        await Assert.That(Published(orch, b.Id)).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);

        for (var i = 0; i < 3; i++) {
            time.Advance(TimeSpan.FromMinutes(1));
            relay(b.Id, "child-b", true, Ms(time));
            await Assert.That(Published(orch, a.Id)).IsEqualTo(0);
            await Assert.That(a.ActivityClock.LiveSubagents).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Another_sessions_unchanged_heartbeat_publishes_a_zero_whose_deadline_passes_inside_the_call() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             a     = orch.SeedAgentForTest("a");
        var             b     = orch.SeedAgentForTest("b");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(a.Id, "child-a", true, Ms(time));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var v0 = orch.StatusNotifierForTest.Version;
        orch.BeforeSubagentSweepForTest = () => time.Advance(TimeSpan.FromSeconds(1));

        relay(b.Id, "child-b", true, Ms(time));

        await Assert.That(Published(orch, a.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    [Test]
    public async Task With_the_callback_held_the_subagents_stop_alone_publishes_zero_and_the_released_callback_adds_nothing() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-9");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        time.Advance(TimeSpan.FromMinutes(11));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        var v0 = orch.StatusNotifierForTest.Version;

        relay(agent.Id, "child-a", false, Ms(time));

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);

        time.Timer!.Fire();
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    /// The report runs inside the callback's hold, ahead of its sweep: its own call arms the timer
    /// for the new id, and the callback's later sweep finds that deadline already set.
    [Test]
    public async Task A_report_that_lands_while_the_callback_is_rescheduling_still_has_its_expiry_fire() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-10");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(Liveness);
        orch.BeforeSubagentSweepForTest = () => {
            orch.BeforeSubagentSweepForTest = null;
            relay(agent.Id, "child-b", true, Ms(time));
        };

        time.Timer!.Fire();

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        await Assert.That(time.Timer.Due).IsEqualTo(Liveness);

        time.Advance(Liveness);
        var v0 = orch.StatusNotifierForTest.Version;
        time.Timer.Fire();

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    [Test]
    public async Task A_report_taken_while_starting_expires_on_time_once_the_agent_runs() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-11", status: "Starting");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);

        time.Advance(TimeSpan.FromMinutes(5));
        orch.SetAgentStatus(agent, "Running");
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);

        var v0 = orch.StatusNotifierForTest.Version;
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    [Test]
    public async Task A_callback_held_across_disposal_publishes_nothing_and_nothing_re_arms() {
        var time  = new ManualTimerTimeProvider();
        var orch  = Build(time);
        var agent = orch.SeedAgentForTest("sub-12");
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(11));

        await orch.DisposeAsync();
        await Assert.That(time.Timer!.Disposed).IsTrue();
        var v0      = orch.StatusNotifierForTest.Version;
        var changes = time.Timer.Changes;

        time.Timer.Fire();

        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0);
        await Assert.That(time.Timer.Changes).IsEqualTo(changes);
    }
}
```

- [ ] **Step 3: Run the class to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorSubagentTests/*"
```
Expected: build error — `BuildOrchestrator` has no `timeProvider`, `SubagentHandler` is never assigned, `BeforeSubagentSweepForTest` does not exist, `LiveSubagents` is never published.

- [ ] **Step 4: Thread the clock through the harness**

In `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorHarness.cs`, change the last `BuildOrchestrator` parameter

```csharp
            IHttpClientFactory?                                 httpClientFactory      = null
        ) {
```
to
```csharp
            IHttpClientFactory?                                 httpClientFactory      = null,
            // The clock behind the subagent-expiry timer and every activity clock a seeded agent
            // gets by default; a test that drives expiry passes its own.
            TimeProvider?                                       timeProvider           = null
        ) {
```
Change the `HarnessOrchestrator` construction's last argument `deferProcessorPublication` to `deferProcessorPublication, timeProvider`; add `TimeProvider? timeProvider` as the last parameter of the `HarnessOrchestrator` constructor (after `bool deferProcessorPublication`), and pass it to `base(...)` after `policySnapshots: new PolicySnapshotProvider(configRoot.Root)` as `timeProvider: timeProvider`.

- [ ] **Step 5: The orchestrator's fields, constructor and wiring**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, after the `_statusNotifier` field (line 434) add:

```csharp
    readonly TimeProvider _time;

    // One timer for every hosted agent's subagent expiry. RescheduleSubagentExpiry is the only code
    // that touches it, always under this lock, which DisposeAsync also takes: a callback entered
    // after disposal returns before it can publish or re-arm.
    readonly Lock   _subagentExpiryLock = new();
    readonly ITimer _subagentExpiry;
    bool            _subagentExpiryDisposed;

    /// <summary>Test seam: runs under <see cref="_subagentExpiryLock"/> after the disposal check and
    /// before the sweep, so a test can move the clock between a call's admission and its visit to
    /// each agent's clock.</summary>
    internal Action? BeforeSubagentSweepForTest { get; set; }

    /// <summary>A live report is "alive now"; one stamped further behind the daemon's wall clock
    /// than this says nothing and is dropped before it reaches a clock. The bridge runs its handlers
    /// independently, so a report admitted early can be processed late — this is what keeps a live
    /// report held across its own stop dead however long it was held. Stops are facts and are never
    /// aged out.</summary>
    internal static readonly TimeSpan SubagentLiveReportFreshness = TimeSpan.FromSeconds(60);
```

Change the constructor's last parameter

```csharp
            PolicySnapshotProvider?                           policySnapshots = null
        ) {
```
to
```csharp
            PolicySnapshotProvider?                           policySnapshots = null,
            // Null in every pre-existing construction site; DI resolves the registered
            // TimeProvider.System. Drives the subagent-expiry timer and every activity clock, so a
            // test hands the orchestrator and its clocks one fake.
            TimeProvider?                                     timeProvider = null
        ) {
```

After `_policySnapshots   = policySnapshots;` (line 714) add:

```csharp
        _time              = timeProvider ?? TimeProvider.System;
        _subagentExpiry    = _time.CreateTimer(
            _ => RescheduleSubagentExpiry(announce: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
```

After `_permissionBridge.InputWaitHandler    =  HandleInputWait;` (line 768) add:

```csharp
        _permissionBridge.SubagentHandler     =  HandleSubagent;
```

- [ ] **Step 6: The handler and the reschedule**

After the closing brace of `HandleInputWait` (line 976), insert:

```csharp
    /// The bridge's subagent report for a hosted Claude session: applied to the attributed agent's
    /// clock, then the shared expiry timer is rescheduled, announcing when the clock said the
    /// count changed. An id the daemon does not hold is dropped.
    void HandleSubagent(string agentId, string subagentId, bool live, long sentAtMs) {
        if (!_agents.TryGetValue(agentId, out var agent)) return;
        if (live && _time.GetUtcNow().ToUnixTimeMilliseconds() - sentAtMs > SubagentLiveReportFreshness.TotalMilliseconds) return;

        var changed = live
            ? agent.ActivityClock.SubagentSeen(subagentId, sentAtMs)
            : agent.ActivityClock.SubagentStopped(subagentId, sentAtMs);
        RescheduleSubagentExpiry(announce: changed);
    }

    /// The only code that touches the expiry timer and the only code that pulses status for
    /// subagents. Every clock is swept whatever its agent's status — a report taken while the agent
    /// was still Starting must expire on time once it runs — the timer is armed before anything is
    /// announced, and both happen under one lock hold: an id retired here is absent from the
    /// snapshot the pulse causes, and one that survived is inside the deadline just armed, however
    /// long this call was delayed. Every mutation is followed by a call, so the last call to run has
    /// seen the latest state and cannot overwrite a deadline a racing report set. Pulse only bumps a
    /// generation and releases waiters asynchronously, so holding the lock across it re-enters
    /// nothing.
    void RescheduleSubagentExpiry(bool announce) {
        lock (_subagentExpiryLock) {
            if (_subagentExpiryDisposed) return;
            BeforeSubagentSweepForTest?.Invoke();

            var       retired = false;
            TimeSpan? next    = null;
            foreach (var agent in _agents.Values) {
                var expiry = agent.ActivityClock.TakeSubagentExpiries();
                retired |= expiry.RetiredAny;
                if (expiry.NextDue is { } due && (next is null || due < next)) next = due;
            }

            _subagentExpiry.Change(next ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (announce || retired) _statusNotifier.Pulse();
        }
    }
```

- [ ] **Step 7: Clocks on the orchestrator's time; disposal under the same lock**

Change `CreateActivityClock` (line 1762-1763)

```csharp
    AgentActivityClock CreateActivityClock() =>
        new(TimeProvider.System) {
```
to
```csharp
    AgentActivityClock CreateActivityClock() =>
        new(_time) {
```

In `DisposeAsync`'s `finally` block, before the `try { _heartbeatTimer.Dispose(); ...` block, insert:

```csharp
            try {
                lock (_subagentExpiryLock) {
                    _subagentExpiryDisposed = true;
                    _subagentExpiry.Dispose();
                }
            } catch (Exception ex) {
                LogDisposeStepFailed(ex, "subagent-expiry");
            }
```

- [ ] **Step 8: Publish the count**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`, change the tail of the `AgentStatusDto` construction

```csharp
                AwaitingInput: a.Status == "Running" && a.ActivityClock.AwaitingInput,
                TranscriptFormat: a.Runtime is IAcpTranscriptSource ? TranscriptFormats.Envelopes : TranscriptFormats.Vendor))];
```
to
```csharp
                AwaitingInput: a.Status == "Running" && a.ActivityClock.AwaitingInput,
                TranscriptFormat: a.Runtime is IAcpTranscriptSource ? TranscriptFormats.Envelopes : TranscriptFormats.Vendor,
                // Null until the agent's first subagent report, a number from then on: the clock's
                // count only while the agent is live, since nothing runs under a terminal one.
                LiveSubagents: a.ActivityClock.LiveSubagents is { } live ? a.Status == "Running" ? live : 0 : null))];
```

- [ ] **Step 9: Run the class to verify it passes**

Same command as Step 3. Expected: all 12 tests pass. Then the neighbours:

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorInputWaitTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentStatusSnapshotTests/*"
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusWiringTests/*"
```
Expected: all pass.

- [ ] **Step 10: Write the failing held-report test through the orchestrator**

In `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs`, add the using `using Capacitor.Cli.Daemon.Tests.Unit.Pty;` after `using Capacitor.Cli.Daemon.Services;`, and append inside the class:

```csharp
    /// One FakeTimeProvider for the orchestrator and its clocks: a live report the bridge admitted
    /// early and ran late is dead on both sides of the stamp-retention boundary — released after
    /// 45 s it is overtaken by its own stop, released after 11 min, once the stop's stamp is no
    /// longer honoured, it is dropped as stale before it reaches the clock.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    [Arguments(45)]
    [Arguments(660)]
    public async Task A_live_report_held_across_its_own_stop_stays_dead_however_long_it_was_held(int heldSeconds) {
        var time = new FakeTimeProvider();
        await using var orch   = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);
        var             agent  = orch.SeedAgentForTest("agent-1");
        var             bridge = orch.PermissionBridgeForTest;
        var hold    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        bridge.BeforeHandlerRunsForTest = () => {
            if (Interlocked.Increment(ref calls) != 2) return Task.CompletedTask;
            entered.TrySetResult();
            return hold.Task;
        };
        await bridge.StartAsync(CancellationToken.None);
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Task<HttpResponseMessage> Post(bool live) => client.PostAsync($"{bridge.BaseUrl}/claude/subagent",
                JsonContent.Create(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live, sent_at = time.GetUtcNow().ToUnixTimeMilliseconds() }));

            (await Post(live: true)).EnsureSuccessStatusCode();
            await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);

            time.Advance(TimeSpan.FromSeconds(1));
            var held = Post(live: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            time.Advance(TimeSpan.FromSeconds(1));
            (await Post(live: false)).EnsureSuccessStatusCode();
            await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(0);

            time.Advance(TimeSpan.FromSeconds(heldSeconds));
            hold.SetResult();
            await Assert.That((await held).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(0);
        } finally {
            await bridge.DisposeAsync();
        }
    }
```

- [ ] **Step 11: Run the bridge class to verify it passes**

```bash
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeSubagentTests/*"
```
Expected: all 11 tests pass (the new one twice). To see it pin something, temporarily delete the `if (live && ...) return;` line in `HandleSubagent` and rerun: the 660-second arm fails with a count of 1; restore the line.

- [ ] **Step 12: AOT check**

```bash
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: no output.

- [ ] **Step 13: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorHarness.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/ManualTimerTimeProvider.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorSubagentTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeSubagentTests.cs
git commit -q -m "Publish live subagents and expire them on one shared timer (#966)" -m "A heartbeat from one session can meet another session's expiry, so the sweep announces whatever it retires and arms the timer before it pulses; the count never moves with time alone." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: CLI relay — one relay, two messages; the subagent message from `ClaudeHookCommand`

**Files:**
- Rename + modify: `src/Capacitor.Cli/Commands/DaemonInputWaitRelay.cs` → `src/Capacitor.Cli/Commands/DaemonHintRelay.cs`
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs:86-93`
- Modify: `src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs:250`
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookInputWaitRelayTests.cs` (doc comment line 17; test at lines 80-94)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookSubagentRelayTests.cs` (new)

**Interfaces:**
- Consumes: `HostedAgent`, `DaemonBridge.Loopback`, `HookClock.Time` (`TimeProvider`), `HookBudget.Remaining`; Task 3's route.
- Produces:
  - `internal static class DaemonHintRelay` with `internal static readonly TimeSpan Cap` (1 s),
  - `public static Task NotifyInputWaitAsync(HostedAgent hosted, string vendor, string? sessionId, string? cwd, bool waiting, TimeSpan budget)`,
  - `public static Task NotifySubagentAsync(HostedAgent hosted, string vendor, string? sessionId, string? cwd, string subagentId, bool live, long sentAtMs, TimeSpan budget)`.

- [ ] **Step 1: Write the failing subagent relay tests**

Create `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookSubagentRelayTests.cs`:

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The subagent relay a daemon-hosted Claude session sends its daemon: every hook a subagent runs
/// reports it alive, its stop reports it gone, and neither touches the parent's wait. Bare
/// <c>[NotInParallel]</c> for the same reason as <see cref="ClaudeHookInputWaitRelayTests"/>: the
/// relay drops its own POST once <see cref="DaemonHintRelay.Cap"/> is spent and says nothing.
public class ClaudeHookSubagentRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid        = "9dc2775376454e4691ecc2d69973c152";
    const string SubagentId = "3f2504e04f8911d39a0c0305e82c3301";

    /// Answers every server post with 200 so the hook reaches its normal end.
    sealed class OkHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    static HookClock Aged(TimeSpan elapsed) {
        var time  = new FakeTimeProvider();
        var clock = new HookClock(time);
        time.Advance(elapsed);
        return clock;
    }

    static WireMockServer Bridge() {
        var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/claude/subagent").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        return bridge;
    }

    static HostedAgent HostedOn(WireMockServer bridge) =>
        new("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

    static JsonNode? Relayed(WireMockServer bridge, string path) =>
        bridge.LogEntries.Where(e => e.RequestMessage.Path == path).Select(e => JsonNode.Parse(e.RequestMessage.Body!)).SingleOrDefault();

    /// The relay rides ahead of client creation, so it is exercised through HandleWithDeps and
    /// asserted on the bridge itself, never on the server.
    async Task<int> RunAsync(HostedAgent hosted, string eventName, HookClock? clock = null, string? agentId = SubagentId) {
        using var client = new HttpClient(new OkHandler());
        var agentField = agentId is null ? "" : $",\"agent_id\":\"{agentId}\"";
        var payload = $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"{{{Sid}}}","cwd":"/tmp","tool_name":"Bash","tool_input":{"command":"ls"}{{{agentField}}}}""";
        return await new ClaudeHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), clock ?? new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), SystemProcessStarter.Instance, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory))
            .HandleWithDeps(new HookSpool(Config.Root), new StringReader(payload), () => Task.FromResult(new AuthAttempt(client, AuthStatus.Ok, null, null)), new StringWriter());
    }

    [Test, NotInParallel]
    [Arguments("SubagentStart")]
    [Arguments("PreToolUse")]
    public async Task A_subagents_hook_reports_it_alive_with_its_stamp(string eventName) {
        using var bridge = Bridge();
        var time = new FakeTimeProvider();

        var exit = await RunAsync(HostedOn(bridge), eventName, new HookClock(time));

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge, "/tok/claude/subagent")!;
        await Assert.That(body["live"]!.GetValue<bool>()).IsTrue();
        await Assert.That(body["subagent_id"]!.GetValue<string>()).IsEqualTo(SubagentId);
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(body["cwd"]!.GetValue<string>()).IsEqualTo("/tmp");
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsEqualTo(time.GetUtcNow().ToUnixTimeMilliseconds());
        await Assert.That(Relayed(bridge, "/tok/claude/input-wait")).IsNull();
    }

    [Test, NotInParallel]
    public async Task A_subagents_stop_reports_it_gone() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "SubagentStop");

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge, "/tok/claude/subagent")!;
        await Assert.That(body["live"]!.GetValue<bool>()).IsFalse();
        await Assert.That(body["subagent_id"]!.GetValue<string>()).IsEqualTo(SubagentId);
        await Assert.That(Relayed(bridge, "/tok/claude/input-wait")).IsNull();
    }

    [Test, NotInParallel]
    public async Task A_parents_hook_reports_no_subagent_and_still_relays_its_wait() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "Stop", agentId: null);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Relayed(bridge, "/tok/claude/subagent")).IsNull();
        await Assert.That(Relayed(bridge, "/tok/claude/input-wait")!["waiting"]!.GetValue<bool>()).IsTrue();
    }

    /// Without the hosted agent id nothing identifies the session to a daemon.
    [Test, NotInParallel]
    public async Task An_unhosted_subagent_hook_relays_nothing() {
        using var bridge = Bridge();
        var unhosted = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        await RunAsync(unhosted, "SubagentStart");

        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task An_exhausted_hook_budget_skips_the_relay() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "SubagentStart", Aged(TimeSpan.FromSeconds(10)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }
}
```

- [ ] **Step 2: Narrow the existing subagent test to what it protects**

In `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookInputWaitRelayTests.cs`, change the class doc comment's `<see cref="DaemonInputWaitRelay.Cap"/>` to `<see cref="DaemonHintRelay.Cap"/>`, and replace `A_subagents_tool_call_relays_nothing` (lines 80-94) with:

```csharp
    /// A subagent's tool call runs the same hook with the parent's environment, but it is not the
    /// parent's turn: a background subagent working on after the parent asked the user something
    /// must not clear the parent's wait. What it does relay is ClaudeHookSubagentRelayTests' subject.
    [Test, NotInParallel]
    public async Task A_subagents_tool_call_posts_no_input_wait() {
        using var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await RunAsync(hosted, "PreToolUse", extraFields: ",\"agent_id\":\"3f2504e04f8911d39a0c0305e82c3301\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count(e => e.RequestMessage.Path == "/tok/claude/input-wait")).IsEqualTo(0);
    }
```

- [ ] **Step 3: Run the new class to verify it fails**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeHookSubagentRelayTests/*"
```
Expected: build error — `DaemonHintRelay` does not exist.

- [ ] **Step 4: Rename and reshape the relay**

```bash
git mv src/Capacitor.Cli/Commands/DaemonInputWaitRelay.cs src/Capacitor.Cli/Commands/DaemonHintRelay.cs
```

Replace the file's content with:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Display hints for the daemon hosting this agent: a turn boundary (the user's move, or a new
/// turn), and a subagent alive or gone. Only a daemon-spawned agent has both an id and a loopback
/// bridge; for anything else every message is a no-op. Best effort on a short cap, and never past
/// what the hook may still spend: the daemon is local, and a wedged one must not push the hook's
/// real work past the host's kill.
/// </summary>
internal static class DaemonHintRelay {
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(1);

    public static Task NotifyInputWaitAsync(
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, bool waiting, TimeSpan budget) =>
        PostAsync(hosted, $"{vendor}/input-wait", new JsonObject {
            ["session_id"] = sessionId,
            ["cwd"]        = cwd,
            ["waiting"]    = waiting,
        }, budget);

    /// <summary><paramref name="sentAtMs"/> is the hook's own UTC clock in Unix milliseconds: the
    /// hooks are asynchronous and the daemon runs their reports independently, so it orders a
    /// subagent's reports by this stamp rather than by arrival.</summary>
    public static Task NotifySubagentAsync(
            HostedAgent hosted, string vendor, string? sessionId, string? cwd, string subagentId, bool live,
            long sentAtMs, TimeSpan budget) =>
        PostAsync(hosted, $"{vendor}/subagent", new JsonObject {
            ["session_id"]  = sessionId,
            ["cwd"]         = cwd,
            ["subagent_id"] = subagentId,
            ["live"]        = live,
            ["sent_at"]     = sentAtMs,
        }, budget);

    static async Task PostAsync(HostedAgent hosted, string route, JsonObject payload, TimeSpan budget) {
        var cap = budget < Cap ? budget : Cap;
        if (cap <= TimeSpan.Zero) return;
        if (hosted.AgentId is not { } agentId) return;
        // A display hint is not worth a line on stderr, so a refused bridge is as quiet as no bridge.
        if (hosted.Bridge is not DaemonBridge.Loopback bridge) return;

        payload["agent_id"] = agentId;

        try {
            using var client  = new HttpClient { Timeout = cap };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var _       = await client.PostAsync($"{bridge.BaseUrl}/{route}", content);
        } catch {
            // A display hint, never a hook outcome.
        }
    }
}
```

- [ ] **Step 5: Post the subagent message from the Claude hook, ahead of every gate**

In `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs`, replace lines 86-93

```csharp
        // Ahead of every gate below: the hosting daemon's turn-boundary hint is local, so neither
        // the server's reachability nor the credential may hold it back. Spends this hook's own
        // budget, which the clock has been counting since the process started. A subagent's tool
        // call (agent_id set) runs this same hook with the parent's environment, but it is not the
        // parent's turn: a background subagent must not clear a wait the parent just began.
        if (agentId is null
         && command switch { "stop" => true, "user-prompt-submit" or "pre-tool-use" => false, _ => (bool?) null } is { } waiting)
            await DaemonInputWaitRelay.NotifyAsync(hosted, "claude", sessionId, cwd, waiting, budget.Remaining);
```
with
```csharp
        // Ahead of every gate below: the hosting daemon's hints are local, so neither the server's
        // reachability nor the credential may hold them back. They spend this hook's own budget,
        // which the clock has been counting since the process started. A subagent's hook (agent_id
        // set) runs with the parent's environment but is not the parent's turn: it reports the
        // subagent alive or gone and never touches the parent's wait — a background subagent must
        // not clear a wait the parent just began.
        if (agentId is not null)
            await DaemonHintRelay.NotifySubagentAsync(
                hosted, "claude", sessionId, cwd, agentId, live: command != "subagent-stop",
                clock.Time.GetUtcNow().ToUnixTimeMilliseconds(), budget.Remaining);
        else if (command switch { "stop" => true, "user-prompt-submit" or "pre-tool-use" => false, _ => (bool?) null } is { } waiting)
            await DaemonHintRelay.NotifyInputWaitAsync(hosted, "claude", sessionId, cwd, waiting, budget.Remaining);
```

In `src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs:250`, change `DaemonInputWaitRelay.NotifyAsync(` to `DaemonHintRelay.NotifyInputWaitAsync(` (the argument list is unchanged).

- [ ] **Step 6: Run the three relay classes to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeHookSubagentRelayTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeHookInputWaitRelayTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexHookInputWaitRelayTests/*"
```
Expected: all pass (6, 6 and 2 tests).

- [ ] **Step 7: AOT check for the CLI**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: no output.

- [ ] **Step 8: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonHintRelay.cs src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookSubagentRelayTests.cs test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookInputWaitRelayTests.cs
git commit -q -m "Relay subagent hooks to the hosting daemon (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```
(`git add` on the new path records the rename; the old path is already staged by `git mv`.)

---

### Task 6: App verdict — `IsWorking`, the count on `ChatSessionInfo` and `AgentRow`, the activity note

**Files:**
- Modify: `src/Capacitor.App/ViewModels/SessionStatusDots.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatSessionInfo.cs`
- Modify: `src/Capacitor.App/Services/AgentRow.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (the `_awaitingInput` field at line 222, `RefreshActivityNote` at line 227, `OnSession` at line 524 — line numbers shift once PR 1 is merged; anchor on the names)
- Test: `test/Capacitor.App.Tests.Unit/SessionStatusDotsTests.cs` (new), `test/Capacitor.App.Tests.Unit/ChatSessionInfoTests.cs` (new), `test/Capacitor.App.Tests.Unit/AgentRowTests.cs`, `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: Task 1's `AgentStatusDto.LiveSubagents`; the existing `ChatTabViewModelTests.Harness`, `Hosted(path, status, awaitingInput)` and `WorkspaceFixtures.Agent`.
- Produces:
  - `SessionStatusDots.IsWorking(string status, bool? awaitingInput, int? liveSubagents)`, `IsWorking(AgentStatusDto dto)`, `IsWorking(AgentRow row)`.
  - `ChatSessionInfo(..., string? FeedKey, int? LiveSubagents = null)`; `FromLocal` carries `dto.LiveSubagents`, `FromRemote` leaves null.
  - `AgentRow(..., string? LaunchStage = null, int? LiveSubagents = null)`; `FromLocal` carries `dto.LiveSubagents`.

PR 1 gives `ChatTabViewModel` a `SessionSubagents` constructor parameter and the test harness already passes it; this task touches neither the constructor nor the harness.

- [ ] **Step 1: Write the failing predicate and carrier tests**

Create `test/Capacitor.App.Tests.Unit/SessionStatusDotsTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The one busy predicate the chat pane and the rail share: running, and either mid-turn or with
/// subagents the daemon still counts. Null counts as zero on both facts.
public class SessionStatusDotsTests {
    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    [Test]
    [Arguments("Running", false, null, true)]
    [Arguments("Running", false, 0, true)]
    [Arguments("Running", true, null, false)]
    [Arguments("Running", true, 0, false)]
    [Arguments("Running", true, 1, true)]
    [Arguments("Running", null, null, false)]
    [Arguments("Running", null, 2, true)]
    [Arguments("Starting", false, 2, false)]
    [Arguments("Completed", false, 2, false)]
    public async Task Is_working_across_the_verdict_and_the_count(string status, bool? awaitingInput, int? liveSubagents, bool expected) {
        var dto = WorkspaceFixtures.Agent("a1", "claude", true) with { Status = status, AwaitingInput = awaitingInput, LiveSubagents = liveSubagents };

        await Assert.That(SessionStatusDots.IsWorking(status, awaitingInput, liveSubagents)).IsEqualTo(expected);
        await Assert.That(SessionStatusDots.IsWorking(dto)).IsEqualTo(expected);
        await Assert.That(SessionStatusDots.IsWorking(AgentRow.FromLocal(dto, Repo))).IsEqualTo(expected);
    }

    [Test]
    public async Task A_remote_row_carries_no_count_and_never_works_by_it() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });

        await Assert.That(row.LiveSubagents).IsNull();
        await Assert.That(SessionStatusDots.IsWorking(row)).IsFalse();
    }
}
```

Create `test/Capacitor.App.Tests.Unit/ChatSessionInfoTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class ChatSessionInfoTests {
    [Test]
    public async Task FromLocal_carries_the_live_subagent_count() {
        var dto = WorkspaceFixtures.Agent("a1", "claude", true) with { LiveSubagents = 2 };
        await Assert.That(ChatSessionInfo.FromLocal(dto, ended: false).LiveSubagents).IsEqualTo(2);
        await Assert.That(ChatSessionInfo.FromLocal(dto with { LiveSubagents = null }, ended: false).LiveSubagents).IsNull();
    }

    [Test]
    public async Task FromRemote_carries_none() {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "r1", Status = "Running", DaemonName = "d", OwnerUserId = "u", Vendor = "claude", RepoOwner = "o", RepoName = "r",
        });
        await Assert.That(ChatSessionInfo.FromRemote(row, ended: false).LiveSubagents).IsNull();
        await Assert.That(ChatSessionInfo.Gone.LiveSubagents).IsNull();
    }
}
```

In `test/Capacitor.App.Tests.Unit/AgentRowTests.cs`, append inside the class:

```csharp
    [Test]
    public async Task FromLocal_carries_the_dtos_live_subagents() {
        var dto = WorkspaceFixtures.Agent("a1", "claude", true) with { LiveSubagents = 3 };
        await Assert.That(AgentRow.FromLocal(dto, Repo).LiveSubagents).IsEqualTo(3);
    }
```

- [ ] **Step 2: Write the failing activity-note tests**

In `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, directly after `The_working_clock_excludes_time_blocked_on_a_card` (which stays untouched), add:

```csharp
    /// The daemon reports the parent waiting while its subagents still run; the note follows the
    /// count and clears when it drops to zero or is unknown.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_working_note_holds_while_only_subagents_run() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                var waiting = Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = true };
                await h.PushAsync(waiting with { LiveSubagents = 2 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");

                await h.PushAsync(waiting with { LiveSubagents = 0 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                await h.PushAsync(waiting with { LiveSubagents = 1 });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 0m 0s");

                await h.PushAsync(waiting with { LiveSubagents = null });
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");
            } finally { await h.TeardownAsync(); }
        });
    }

    /// A card up means something is blocked on the user, and the note cannot know whether the
    /// asker is the parent or a subagent: the pause applies to background work too.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_card_pauses_the_note_while_subagents_run() {
        await RunOnUiAsync(async () => {
            var h = new Harness(TranscriptChat.Journal);
            try {
                await h.PushAsync(Agent("a1", "pi", hasTerminal: false) with { Status = "Running", AwaitingInput = true, LiveSubagents = 1 });
                h.Time.Advance(TimeSpan.FromSeconds(65));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");

                h.Permissions.Add(PermissionEntries.Entry("r1", "a1"));
                await WaitUntilAsync(() => h.Chat.HasPendingCards, what: "the blocking card");
                h.Time.Advance(TimeSpan.FromMinutes(10));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("");

                h.Permissions.Remove("r1");
                await WaitUntilAsync(() => !h.Chat.HasPendingCards, what: "the card removed");
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 5s");
                h.Time.Advance(TimeSpan.FromSeconds(2));
                await Assert.That(h.Chat.ActivityNote).IsEqualTo("Working for 1m 7s");
            } finally { await h.TeardownAsync(); }
        });
    }
```

- [ ] **Step 3: Run the new classes to verify they fail**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionStatusDotsTests/*"
```
Expected: build error — `SessionStatusDots.IsWorking` does not exist, `AgentRow` has no `LiveSubagents`.

- [ ] **Step 4: The predicate**

In `src/Capacitor.App/ViewModels/SessionStatusDots.cs`, after `NeedsAttention(AgentRow row)` add:

```csharp
    /// The one busy verdict every surface reads: a live agent that is either mid-turn or still has
    /// subagents the daemon counts. The wait flag keeps its meaning beside it — a parent that
    /// stopped with agents running reads as waiting on the user and busy at once.
    public static bool IsWorking(string status, bool? awaitingInput, int? liveSubagents) =>
        status == "Running" && (awaitingInput == false || liveSubagents > 0);

    public static bool IsWorking(AgentStatusDto dto) => IsWorking(dto.Status, dto.AwaitingInput, dto.LiveSubagents);

    public static bool IsWorking(AgentRow row) => IsWorking(row.Status, row.AwaitingInput, row.LiveSubagents);
```

- [ ] **Step 5: Carry the count**

In `src/Capacitor.App/ViewModels/ChatSessionInfo.cs`, change the record header

```csharp
public sealed record ChatSessionInfo(
        string Status, string StatusLabel, string Vendor, string? Model, string? Root, bool? AwaitingInput, bool Ended,
        string ReadOnlyNotice, string? FeedKey) {
```
to
```csharp
public sealed record ChatSessionInfo(
        string Status, string StatusLabel, string Vendor, string? Model, string? Root, bool? AwaitingInput, bool Ended,
        string ReadOnlyNotice, string? FeedKey,
        // The daemon's count of running subagents; null on the remote lane and from an older daemon.
        int? LiveSubagents = null) {
```
and in `FromLocal` change `ChatTabViewModel.ParticipantNotice(dto), dto.TranscriptPath);` to `ChatTabViewModel.ParticipantNotice(dto), dto.TranscriptPath, dto.LiveSubagents);`.

In `src/Capacitor.App/Services/AgentRow.cs`, change

```csharp
        // The runtime's latest handshake stage on a pending row; null on every published row.
        string? LaunchStage = null) {
```
to
```csharp
        // The runtime's latest handshake stage on a pending row; null on every published row.
        string? LaunchStage = null,
        // The daemon's count of running subagents; null on a remote or pending row.
        int? LiveSubagents = null) {
```
and in `FromLocal` change `AwaitingInput: dto.AwaitingInput);` to `AwaitingInput: dto.AwaitingInput, LiveSubagents: dto.LiveSubagents);`.

- [ ] **Step 6: The note reads the predicate**

In `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`:

After `bool? _awaitingInput;` add `int? _liveSubagents;`.

In `RefreshActivityNote`, change
```csharp
        var inTurn = _status == "Running" && _awaitingInput == false;
```
to
```csharp
        var inTurn = SessionStatusDots.IsWorking(_status, _awaitingInput, _liveSubagents);
```

In `OnSession`, after `_awaitingInput = info.AwaitingInput;` add `_liveSubagents = info.LiveSubagents;`.

- [ ] **Step 7: Run the classes to verify they pass**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionStatusDotsTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatSessionInfoTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentRowTests/*"
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"
```
Expected: all pass, the existing activity-note and pause tests included.

- [ ] **Step 8: Build the app warning-free**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.App/ViewModels/SessionStatusDots.cs src/Capacitor.App/ViewModels/ChatSessionInfo.cs src/Capacitor.App/Services/AgentRow.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/SessionStatusDotsTests.cs test/Capacitor.App.Tests.Unit/ChatSessionInfoTests.cs test/Capacitor.App.Tests.Unit/AgentRowTests.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs
git commit -q -m "Keep the working note on while only subagents run (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: App rail — the pulsing dot, meta and tooltip

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RailSessionViewModel.cs` (properties after `IsStarting`; the constructor block from `IsStarting = ...` through `Tooltip = ...`)
- Modify: `src/Capacitor.App/Views/SessionRailView.axaml:216`
- Test: `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs`

**Interfaces:**
- Consumes: Task 6's `AgentRow.LiveSubagents`.
- Produces: `RailSessionViewModel.DotPulses` (`bool`, true while starting or while `LiveSubagents > 0`); `Meta` gains ` · N subagent(s)` and `Tooltip` gains `N subagent(s) running` for a positive count.

- [ ] **Step 1: Write the failing tests**

In `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs`, extend the `Row` helper:

```csharp
    static AgentRow Row(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test", bool? awaitingInput = null, int? liveSubagents = null) =>
        AgentRow.FromLocal(
            new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null,
                Title: title, AwaitingInput: awaitingInput, LiveSubagents: liveSubagents),
            Repo);
```

and append inside the class:

```csharp
    /// The daemon's count keeps the row visibly busy while only subagents run.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_subagents_pulse_the_dot_and_name_the_count() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var two   = new RailSessionViewModel(Row(liveSubagents: 2), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { });
            using var one   = new RailSessionViewModel(Row(liveSubagents: 1), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { });
            using var none  = new RailSessionViewModel(Row(liveSubagents: 0), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { });
            using var older = new RailSessionViewModel(Row(liveSubagents: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { });

            await Assert.That(two.DotPulses).IsTrue();
            await Assert.That(two.Meta).EndsWith(" · 2 subagents");
            await Assert.That(two.Tooltip).Contains("2 subagents running");
            await Assert.That(one.DotPulses).IsTrue();
            await Assert.That(one.Meta).EndsWith(" · 1 subagent");
            await Assert.That(one.Tooltip).Contains("1 subagent running");
            await Assert.That(none.DotPulses).IsFalse();
            await Assert.That(none.Meta).DoesNotContain("subagent");
            await Assert.That(none.Tooltip).DoesNotContain("subagent");
            await Assert.That(older.DotPulses).IsFalse();
            await Assert.That(older.Meta).DoesNotContain("subagent");
            await Assert.That(older.Tooltip).DoesNotContain("subagent");
        });
    }

    /// The wait badge and the pip answer for the parent as before, so a row can read as both
    /// waiting on the user and busy.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_subagents_leave_the_wait_badge_and_the_pip_as_they_are() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var waiting = new RailSessionViewModel(Row(awaitingInput: true, liveSubagents: 2), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { });
            using var busy    = new RailSessionViewModel(Row(awaitingInput: false, liveSubagents: 2), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { });

            await Assert.That(waiting.NeedsYou).IsTrue();
            await Assert.That(waiting.StatusBadge).IsEqualTo("zzz");
            await Assert.That(waiting.DotPulses).IsTrue();
            await Assert.That(waiting.Tooltip).Contains("waiting for input");
            await Assert.That(waiting.Tooltip).Contains("2 subagents running");
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(waiting.StatusBadge).IsEqualTo("!");

            await Assert.That(busy.NeedsYou).IsFalse();
            await Assert.That(busy.StatusBadge).IsEqualTo("");
            await Assert.That(busy.DotPulses).IsTrue();
        });
    }

    /// A remote row carries no count and looks as it did; a pending row still pulses for its start.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_is_unchanged_and_a_pending_row_still_pulses() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var remote = new RailSessionViewModel(RemoteRow("r1"), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { });
            var pendingRow = AgentRow.FromPending(new PendingLaunchDto("p1", "claude", "/repo", "t", DateTime.UtcNow, "spawned"), Repo);
            using var pending = new RailSessionViewModel(pendingRow, new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { });

            await Assert.That(remote.DotPulses).IsFalse();
            await Assert.That(remote.Meta).DoesNotContain("subagent");
            await Assert.That(remote.Tooltip).DoesNotContain("subagent");
            await Assert.That(pending.DotPulses).IsTrue();
        });
    }
```

- [ ] **Step 2: Run the class to verify it fails**

```bash
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/RailSessionViewModelTests/*"
```
Expected: build error — `RailSessionViewModel` has no `DotPulses`.

- [ ] **Step 3: The row's count text and pulse**

In `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`, after the `IsStarting` property add:

```csharp
    /// The status dot pulses while the launch is starting or while the daemon counts subagents
    /// running under the session.
    public bool DotPulses { get; }
```

In the constructor, replace

```csharp
        IsStarting = row.Origin == AgentOrigin.Pending;
        Meta = IsStarting ? LaunchStages.Label(row.LaunchStage) : Join(kindExtra, borrowed, age);
        StatusDot = SessionStatusDots.For(row.Status);
        Tooltip = IsStarting
            ? Join(row.Id, "Starting", LaunchStages.Label(row.LaunchStage))
            : Join(row.Id, row.Status, SessionStatusDots.WaitsOnUser(row) ? "waiting for input" : null,
                row.RequesterDisplay, row.BorrowedFrom is null ? null : $"borrowed {row.BorrowedFrom}");
```
with
```csharp
        IsStarting = row.Origin == AgentOrigin.Pending;
        var subagents = row.LiveSubagents is int live and > 0 ? $"{live} subagent{(live == 1 ? "" : "s")}" : null;
        DotPulses = IsStarting || subagents is not null;
        Meta = IsStarting ? LaunchStages.Label(row.LaunchStage) : Join(kindExtra, borrowed, age, subagents);
        StatusDot = SessionStatusDots.For(row.Status);
        Tooltip = IsStarting
            ? Join(row.Id, "Starting", LaunchStages.Label(row.LaunchStage))
            : Join(row.Id, row.Status, SessionStatusDots.WaitsOnUser(row) ? "waiting for input" : null,
                subagents is null ? null : $"{subagents} running",
                row.RequesterDisplay, row.BorrowedFrom is null ? null : $"borrowed {row.BorrowedFrom}");
```

- [ ] **Step 4: Bind the dot**

In `src/Capacitor.App/Views/SessionRailView.axaml`, in the session row template's `Ellipse` (line 216), change

```xml
                                                                                         Classes.pulsing="{Binding IsStarting}"
```
to
```xml
                                                                                         Classes.pulsing="{Binding DotPulses}"
```

- [ ] **Step 5: Run the class to verify it passes**

Same command as Step 2. Expected: all pass, the pre-existing rail tests included.

- [ ] **Step 6: Build the app warning-free and run the rail view smoke tests**

```bash
dotnet build src/Capacitor.App/Capacitor.App.csproj
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/SessionRailViewModelTests/*"
```
Expected: 0 warnings; all pass.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/ViewModels/RailSessionViewModel.cs src/Capacitor.App/Views/SessionRailView.axaml test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs
git commit -q -m "Pulse the rail dot and name the count while subagents run (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: Docs — the reasoning entry

**Files:**
- Modify: `docs/CHANGES.md` (new entry directly below the intro paragraphs, above `## One remap rule covers a family of deleted worktrees`)
- Check only: `README.md`

**Interfaces:**
- Consumes: nothing.
- Produces: the `docs/CHANGES.md` entry.

- [ ] **Step 1: Confirm the README needs no change**

```bash
grep -n "Working for\|status dot\|zzz\|subagents running" README.md
```
Expected: no match — the README's desktop section describes install, settings, updates and support, not the chat pane's note or the rail row, and this PR adds no CLI surface (no command, flag, default or prerequisite). Nothing to update.

- [ ] **Step 2: Write the entry**

In `docs/CHANGES.md`, after the paragraph ending `where an entry disagrees with the code, the code wins.` and the blank line that follows it, insert:

```markdown
## The daemon counts live subagents beside the wait verdict, never instead of it

Claude's hooks cannot tell "I will wait for my agents" from "I asked you something": both are the
parent's `Stop`. So `AwaitingInput` keeps its one meaning — the parent finished a turn and nothing
has been handed to it — and the wait badge, `NeedsYou` and the tray's attention are untouched. What
`AgentStatusDto` gains is a second fact, `live_subagents`: how many subagents the daemon believes
are running, null until the agent's first subagent report so an older daemon, a vendor whose hooks
report none and a session that spawned none read alike. The app reads "busy" from the two through
one predicate, `SessionStatusDots.IsWorking`, so the chat's working note and the rail's pulsing dot
agree. The pending-card pause is unchanged: a card up means something is blocked on the user, and
the note cannot know whether the asker is the parent or a subagent whose tool calls raise cards on
the same lane.

Evidence is the subagent's own hooks — `SubagentStart`, each tool call, `SubagentStop` — relayed to
the bridge's `/{token}/claude/subagent` route with a `sent_at` the hook stamps itself. The hooks are
asynchronous and the bridge runs its handlers independently, so reports apply in the order they were
sent, not the order they ran: a live report overtaken by its own stop stays dead, a stop overtaken
by a later live report does not end a resumed run, and a live report more than 60 seconds behind the
daemon's clock is dropped before it reaches the clock — a live report says "alive now", an old one
says nothing, which is what keeps one held across its own stop dead however long it was held. A
start stamped within 30 seconds after its stop is the start hook scheduled late, not a resume. Every
comparison reads the one wall clock hooks and daemon share, so a stamp is honoured for only 10
minutes of monotonic time: a clock step misleads the count for at most that long.

A `SubagentStop` is not guaranteed — the hooks run on a 5 s timeout, and a background agent killed
through `TaskStop` never fires one — so a live id unreported for 10 minutes is retired. Without the
window a lost stop would show activity for the rest of the session; with it, a lost stop costs at
most 10 minutes of a stale count, and a subagent quiet for longer drops out until its next tool
call. The count never moves with time alone: an id leaves the set only through a stop or through
the sweep, and each is announced by whichever call meets it, so a value cannot change between a
published snapshot and the report that settles it and leave the app showing a subagent forever.

One timer, shared by every hosted agent, covers expiry, and one method under one lock is the only
code that touches it: it sweeps every clock whatever the agent's status, arms the timer for the
earliest deadline, and only then pulses. Arming before announcing is what makes the pulse honest —
an id retired by the sweep is absent from the snapshot the pulse causes, and one that survived is
inside the deadline just armed, however long the call was delayed. Because every mutation is
followed by a call, the last call to run has seen the latest state, and a heartbeat from one session
can never postpone the news about another's expiry.

```

- [ ] **Step 3: Commit**

```bash
git add docs/CHANGES.md
git commit -q -m "Record why the daemon counts subagents beside the verdict (#966)" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Final verification

- [ ] Run the four touched suites in full and the solution build:

```bash
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
dotnet build Capacitor.slnx
```
Expected: green (the daemon suite's known deterministic local failures — the codex schema pin and the Pi env-var-absence assertion — are pre-existing and unrelated), 0 warnings.

- [ ] Both AOT publishes once more:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: no output.

- [ ] PR description: follow `.github/PULL_REQUEST_TEMPLATE.md`; the reference line reads `Closes #966` and `AI-2840`; the title is `Count live subagents beside the daemon's wait verdict`.
