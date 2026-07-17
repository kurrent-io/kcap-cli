# AI-1313 Phase B (kcap-cli) — Daemon self-defense Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the `kcap agent` daemon the self-defense the server's Phase A (PR #1109) can't provide: harden post-spawn launch cleanup, advertise richer per-agent metadata, add a reviewer TTL/idle backstop, and — the core — make hosted-agent processes recoverable after a daemon crash via durable PID records + a startup orphan reap + a scoped env-marker scan, so a survivor never leaks its capacity slot forever.

**Architecture:** All work is in the `kcap-cli` daemon (`Capacitor.Cli.Daemon`), on top of the existing `AgentOrchestrator` (registry `_agents`, `HandleLaunchAgent`/`HandleStopAgent`, `RunHeartbeatLoopAsync`) and the `Pty` spawn layer. Every protocol change is **additive** (an old server ignores unknown JSON fields / unknown one-way hub methods; an old daemon never sets them), so this PR is independently deployable in either order with the Phase A server. Death recovery is **managed-C#** here (write a PID record atomically at spawn → at next boot / on every heartbeat, reap leftover records + env-marked survivors by exact OS-native identity) — no native OS-containment primitive is required for correctness; that (Windows Job Object, Linux `PDEATHSIG`) is a separate follow-on that only makes death *immediate* rather than *next-sweep*.

**Tech Stack:** .NET 10, C#, AOT-published CLI; TUnit test suite (`test/`); `System.Text.Json` source-gen DTOs in `Capacitor.Cli.Core/Models.cs`; SignalR client (`ServerConnection`); existing PTY interop (`Pty/Unix/UnixPtyInterop.cs`, `Pty/Windows/ConPtyInterop.cs`).

## Global Constraints

- **Additive protocol only.** New DTO fields are trailing/optional; new daemon→server messages are one-way `SendAsync` (never `InvokeAsync`) so an unknown method on an old server is a server-side log line, not a client fault. Old daemon ↔ new server and new daemon ↔ old server must both work (spec §7).
- **No live daemon, no live flows in tests.** Every kill/reap/containment test acts on **isolated dummy processes** the test spawns and owns (spec §2, §8). Never signal a real `kcap-daemon` or a real reviewer.
- **Ambiguity never kills.** A process is killed only when its identity is proven (exact OS-native start-identity match **plus**, on Unix, the `KCAP_AGENT_ID` env check — the record regime; OR the full env marker triple — the marker regime). If identity/env cannot be read, the process is **spared**, the record quarantined, and a warning logged (spec §6.4(2),(2b)).
- **A record is deleted only after confirmed death** — exit observed, or the exact identity matches no live process. A failed/timed-out/attempted kill **retains** the record for the next sweep (spec §6.4(2)).
- **Process identity is the exact OS-native creation stamp**, stored + compared in native units, never round-tripped through `DateTime` (Linux `/proc/{pid}/stat` field 22 `starttime` in clock ticks; macOS `proc_pidinfo`/`kinfo_proc` start `tv_sec`+`tv_usec`; Windows creation `FILETIME`). No tolerance window (spec §6.4(2)).
- **Reviewer TTL/idle defaults:** lifetime 6h, idle 2h; `0` disables; config keys `daemon.reviewer_max_lifetime`/`daemon.reviewer_idle_timeout` + env `KCAP_REVIEWER_MAX_LIFETIME`/`KCAP_REVIEWER_IDLE_TIMEOUT`. Interactive agents untouched (spec §6.3).
- **Single-flight teardown:** exactly one teardown runs per agent even when the launch-catch and the read-loop `finally` race (`Interlocked.CompareExchange` flag) (spec §6.1).
- **`ActiveCount` never changes meaning on the wire:** it stays exactly the `_agents` Starting/Running count. The daemon admission gate becomes `EffectiveCount = ActiveCount + Quarantined.Count` (spec §6.4(2a)).

---

## Scope

**In scope (this PR):** D1 (post-spawn single-flight cleanup), D2 (agent metadata + additive `LiveAgents` on `DaemonConnect` + one-way periodic `DaemonStatusReport` **send**), D3 (reviewer TTL/idle backstop), D4 **managed reaping** — atomic PID records with exact identity, in-memory kill-quarantine, startup orphan reap (`killpg`), scoped env-marker scan (`KCAP_DAEMON_ID`/`KCAP_DAEMON_EPOCH`), and the `HandleStopAgent` PID-record fallback.

**Deferred to follow-on plans** (see the final section — each is an independent subsystem, so a separate plan per the writing-plans scope rule):
- **Plan B-native — D4 layer 1 OS containment:** Windows creation-time Job Object (`ConPtyProcess`) + Linux `PR_SET_PDEATHSIG` on a dedicated daemon-lifetime spawner thread with an async-signal-safe post-fork shim + `getppid` re-check (`UnixPtyProcess`). Pure hardening on top of this PR's records/scan (makes death immediate; the records/scan already recover survivors), and the only part needing native interop + a C shim.
- **Plan B2 — sequenced-command settlement (bilateral, server+CLI):** `Epoch`/`Seq`, `StopAgentV2`, `CommandAck`, `AckProcessedPrefix`, `ResolvedStartupCandidates`, generation settlement, and the server-side consumers (S3(b)/(c) untracked-reap + retry-until-gone, S6 daemon-authoritative, the `DaemonStatusReport` **server handler**). This PR only *sends* the report; nothing consumes it yet.

---

## File Structure

- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — D1 (single-flight teardown in the launch catch + read-loop finally), D2 (`AgentInstance` metadata; `LiveAgents` snapshot builder; `RunDaemonStatusReportLoopAsync`), D3 (reviewer TTL/idle in `RunHeartbeatLoopAsync`), D4 (write PID record at spawn; kill-quarantine field + heartbeat retry; `HandleStopAgent` PID-record fallback).
- `src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs` *(new)* — atomic write (temp+rename) / read / delete of `<state-dir>/agents/{agentId}.json`; parse + `.corrupt` quarantine; enumerate leftover records.
- `src/Capacitor.Cli.Daemon/Services/ProcessIdentity.cs` *(new)* — capture + compare the exact OS-native start-identity for a pid (Linux `/proc/{pid}/stat`, macOS `proc_pidinfo`, Windows `GetProcessTimes`); `IsAlive(pid, identity)`; read a live process's `KCAP_*` env (Linux `/proc/{pid}/environ`, macOS `ps -E -ww`).
- `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs` *(new)* — startup record pass (`killpg`→5s→`SIGKILL`, confirmed-death delete) + scoped env-marker scan; both re-runnable from a heartbeat tick.
- `src/Capacitor.Cli.Daemon/Services/AgentKillQuarantine.cs` *(new)* — in-memory set of unconfirmed-death launch contexts the heartbeat retries; feeds `EffectiveCount` + the status report.
- `src/Capacitor.Cli.Daemon/DaemonConfig.cs` — `ReviewerMaxLifetime`/`ReviewerIdleTimeout`; the daemon state-dir path + `KCAP_DAEMON_ID`/epoch.
- `src/Capacitor.Cli.Core/Models.cs` — `LaunchAgentCommand` gains `FlowRunId?`/`FlowRole?`; `DaemonConnect` gains `LiveAgents?`; new `LiveAgentInfo`, `DaemonStatusReport`, `QuarantinedAgentInfo` DTOs + `LaunchKind`-adjacent additions; register all in `CapacitorJsonContext`.
- `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` — send `LiveAgents` in `DaemonConnectAsync`; add `DaemonStatusReportAsync` one-way sender.
- `src/Capacitor.Cli.Daemon/Pty/*` — spawn passes the `KCAP_AGENT_ID`/`KCAP_DAEMON_ID`/`KCAP_DAEMON_EPOCH` env markers to the child (env only — no native containment in this PR).
- Tests under `test/…Daemon.Tests/` (mirror the existing daemon test project layout): `AgentPidRecordStoreTests`, `ProcessIdentityTests`, `OrphanReaperTests`, `AgentKillQuarantineTests`, `ReviewerTtlTests`, `LaunchCleanupTests`, `DaemonStatusReportTests`.

> **Note (verify at execution time):** confirm the daemon state-dir accessor + `DaemonLock` location the spec cites near `DaemonRunner.cs:380`/`CleanupOrphanedAsync`; `WorktreeManager.CleanupOrphanedAsync` is the sibling boot-cleanup seam. Confirm the exact daemon test project path/namespace (`grep -r "class .*Tests" test/` in the CLI repo) and mirror it — task test paths below use `test/Capacitor.Cli.Daemon.Tests/` as the placeholder to correct on first task.

---

### Task 1: D2a — additive DTOs (`FlowRunId`/`FlowRole`, `LiveAgents`, `DaemonStatusReport`)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs` (`LaunchAgentCommand`, `DaemonConnect`, `CapacitorJsonContext`)
- Test: `test/Capacitor.Cli.Daemon.Tests/DtoRoundTripTests.cs`

**Interfaces:**
- Produces: `LaunchAgentCommand` gains trailing `string? FlowRunId = null, string? FlowRole = null`.
- Produces: `record LiveAgentInfo(string Id, string Kind, DateTimeOffset CreatedAt, string? FlowRunId = null, string? FlowRole = null)`.
- Produces: `record QuarantinedAgentInfo(string Id, string Kind, DateTimeOffset CreatedAt, string? FlowRunId = null, string? FlowRole = null)`.
- Produces: `record DaemonStatusReport(int ActiveCount, IReadOnlyList<LiveAgentInfo> LiveAgents, IReadOnlyList<QuarantinedAgentInfo> Quarantined)`.
- Produces: `DaemonConnect` gains trailing `LiveAgentInfo[]? LiveAgents = null` (the existing `string[] LiveAgentIds` stays for back-compat).

- [ ] **Step 1: Write the failing test** — a DTO round-trips through `CapacitorJsonContext` and old-shape JSON (no new fields) still deserializes.

```csharp
using System.Text.Json;
using Capacitor.Cli.Core;

public class DtoRoundTripTests {
    [Test]
    public async Task DaemonStatusReport_roundtrips_through_source_gen_context() {
        var report = new DaemonStatusReport(
            ActiveCount: 2,
            LiveAgents: [ new LiveAgentInfo("a1", "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer") ],
            Quarantined: [ new QuarantinedAgentInfo("a2", "Default", DateTimeOffset.UtcNow) ]);
        var json = JsonSerializer.Serialize(report, CapacitorJsonContext.Default.DaemonStatusReport);
        var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.DaemonStatusReport)!;
        await Assert.That(back.ActiveCount).IsEqualTo(2);
        await Assert.That(back.LiveAgents[0].FlowRunId).IsEqualTo("flow-1");
    }

    [Test]
    public async Task LaunchAgentCommand_old_json_without_flow_fields_still_deserializes() {
        // An old server sends no FlowRunId/FlowRole — must deserialize with nulls, not throw.
        var oldJson = """{"AgentId":"a1","Model":"default","RepoPath":"/r","Vendor":"codex"}""";
        var cmd = JsonSerializer.Deserialize(oldJson, CapacitorJsonContext.Default.LaunchAgentCommand)!;
        await Assert.That(cmd.FlowRunId).IsNull();
        await Assert.That(cmd.FlowRole).IsNull();
    }
}
```

- [ ] **Step 2: Run → FAIL** (types/context members don't exist).

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests -- --treenode-filter "/*/*/DtoRoundTripTests/*"`
Expected: FAIL — `LiveAgentInfo`/`DaemonStatusReport` undefined.

- [ ] **Step 3: Add the DTOs + trailing fields + `[JsonSerializable]` registrations** in `Models.cs`. Add `FlowRunId`/`FlowRole` as trailing params on `LaunchAgentCommand` (after every existing param, all defaulted `= null`). Add the three new records. Add to `CapacitorJsonContext`:

```csharp
[JsonSerializable(typeof(LiveAgentInfo))]
[JsonSerializable(typeof(QuarantinedAgentInfo))]
[JsonSerializable(typeof(DaemonStatusReport))]
```

Add `LiveAgentInfo[]? LiveAgents = null` as the final param of `DaemonConnect`.

- [ ] **Step 4: Run → PASS.**

- [ ] **Step 5: Commit** — `git add -A && git commit -m "[AI-1313] Phase B D2a: additive daemon status DTOs (LiveAgents, DaemonStatusReport, LaunchAgentCommand flow identity)"`

---

### Task 2: D2b — `AgentInstance` metadata (Kind + flow identity) + `LiveAgents` snapshot

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`AgentInstance` record; capture `cmd.Kind`/`cmd.FlowRunId`/`cmd.FlowRole` at construction ~`:508`; add `BuildLiveAgents()`), `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`DaemonConnectAsync` sends `LiveAgents`)
- Test: `test/Capacitor.Cli.Daemon.Tests/LiveAgentsSnapshotTests.cs`

**Interfaces:**
- Consumes: `LaunchAgentCommand.FlowRunId`/`FlowRole`/`Kind` (Task 1).
- Produces: `AgentInstance` gains `public LaunchKind Kind { get; init; } = LaunchKind.Default; public string? FlowRunId { get; init; } public string? FlowRole { get; init; }`.
- Produces: `AgentOrchestrator.BuildLiveAgents() : IReadOnlyList<LiveAgentInfo>` — one entry per `_agents` value with `Status is "Starting" or "Running"`.
- Produces: `ServerConnection.GetLiveAgents` callback wired like the existing `GetLiveAgentIds`.

- [ ] **Step 1: Write the failing test** — `BuildLiveAgents` reflects a launched ReviewFlow agent's flow identity + kind, and excludes stopped agents. (Use the daemon test project's existing `AgentOrchestrator` construction helper — locate it: `grep -rn "new AgentOrchestrator(" test/`.)

```csharp
[Test]
public async Task BuildLiveAgents_includes_running_reviewflow_identity_excludes_stopped() {
    var orch = TestOrchestrator.Create(); // existing test helper
    orch.SeedAgentForTest(id: "a1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-1", flowRole: "reviewer", status: "Running");
    orch.SeedAgentForTest(id: "a2", kind: LaunchKind.Default, status: "Stopped");
    var live = orch.BuildLiveAgents();
    await Assert.That(live.Select(x => x.Id)).IsEquivalentTo(new[] { "a1" });
    await Assert.That(live[0].FlowRunId).IsEqualTo("flow-1");
    await Assert.That(live[0].Kind).IsEqualTo("ReviewFlow");
}
```

> If no `SeedAgentForTest` helper exists, add a minimal `internal` test seam on `AgentOrchestrator` that inserts a pre-built `AgentInstance` into `_agents` (guarded `[InternalsVisibleTo]` already exists for the daemon test project — verify).

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement** — add the three properties to `AgentInstance`; set them at the `:508` construction (`Kind = cmd.Kind, FlowRunId = cmd.FlowRunId, FlowRole = cmd.FlowRole`); add:

```csharp
internal IReadOnlyList<LiveAgentInfo> BuildLiveAgents() =>
    [.. _agents.Values
        .Where(a => a.Status is "Starting" or "Running")
        .Select(a => new LiveAgentInfo(a.Id, a.Kind.ToString(), a.CreatedAt, a.FlowRunId, a.FlowRole))];
```

Wire `_server.GetLiveAgents = BuildLiveAgents;` next to the existing `GetLiveAgentIds` wiring, and in `ServerConnection.DaemonConnectAsync` pass `GetLiveAgents?.Invoke()?.ToArray()` as the new `DaemonConnect.LiveAgents` arg.

- [ ] **Step 4: Run → PASS.**

- [ ] **Step 5: Commit** — `[AI-1313] Phase B D2b: AgentInstance flow identity + LiveAgents on DaemonConnect`

---

### Task 3: D2c — periodic one-way `DaemonStatusReport` send

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (start `RunDaemonStatusReportLoopAsync` next to the existing loops ~`:278`), `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`DaemonStatusReportAsync`)
- Test: `test/Capacitor.Cli.Daemon.Tests/DaemonStatusReportTests.cs`

**Interfaces:**
- Consumes: `BuildLiveAgents()` (Task 2), `AgentKillQuarantine.Snapshot()` (Task 8 — until then, empty).
- Produces: `ServerConnection.DaemonStatusReportAsync(DaemonStatusReport report)` — **one-way `SendAsync`**, wrapped in its own try/catch, never `InvokeAsync`.

- [ ] **Step 1: Write the failing test** — the report loop builds a report with `ActiveCount` = running/starting count and sends via the one-way path; a send exception does not escape.

```csharp
[Test]
public async Task StatusReport_sends_active_count_and_live_agents_one_way_swallowing_errors() {
    var sends = new List<DaemonStatusReport>();
    var server = new FakeServerConnection { OnStatusReport = r => { sends.Add(r); throw new Exception("boom"); } };
    var orch = TestOrchestrator.Create(server);
    orch.SeedAgentForTest("a1", LaunchKind.ReviewFlow, status: "Running");
    await orch.SendDaemonStatusReportOnceAsync(); // extract the loop body into a testable method
    await Assert.That(sends).Count().IsEqualTo(1);
    await Assert.That(sends[0].ActiveCount).IsEqualTo(1);
    // no throw escaped
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement** — add `ServerConnection.DaemonStatusReportAsync`:

```csharp
public async Task DaemonStatusReportAsync(DaemonStatusReport report) {
    try { await _hub.SendAsync("DaemonStatusReport", report, cancellationToken: _ct); }
    catch (Exception ex) { _logger.LogDebug(ex, "DaemonStatusReport send failed (old server or transient) — ignoring"); }
}
```

Add `SendDaemonStatusReportOnceAsync()` (builds `new DaemonStatusReport(ActiveCount, BuildLiveAgents(), _quarantine.Snapshot())` and calls the server) and a 60s `RunDaemonStatusReportLoopAsync(ct)` that calls it each tick inside a total try/catch, started from the same place as `RunHeartbeatLoopAsync`.

- [ ] **Step 4: Run → PASS.**

- [ ] **Step 5: Commit** — `[AI-1313] Phase B D2c: periodic one-way DaemonStatusReport send`

---

### Task 4: D3 — reviewer lifetime/idle backstop

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (config + env), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`RunHeartbeatLoopAsync` ~`:1473`)
- Test: `test/Capacitor.Cli.Daemon.Tests/ReviewerTtlTests.cs`

**Interfaces:**
- Produces: `DaemonConfig.ReviewerMaxLifetime`/`ReviewerIdleTimeout` (`TimeSpan`, defaults 6h/2h, `TimeSpan.Zero` disables).
- Consumes: `AgentInstance.Kind` (Task 2), `CreatedAt`/`LastOutputAt` (existing).

- [ ] **Step 1: Write the failing test** — with an injectable clock, a 6h-old Running ReviewFlow agent is reaped with `reviewer_ttl_expired`, a 2h-idle one with `reviewer_idle_expired`, an interactive agent of the same age is untouched, and `0` disables. (Inject a `Func<DateTime> _utcNow` seam into `AgentOrchestrator`; assert `HandleStopAgent` was invoked with the right end reason via a fake.)

```csharp
[Test]
public async Task Heartbeat_reaps_reviewflow_past_lifetime_but_not_interactive() {
    var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    var orch = TestOrchestrator.Create(clock: () => now, config: c => c.ReviewerMaxLifetime = TimeSpan.FromHours(6));
    orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", createdAt: now.AddHours(-7));
    orch.SeedAgentForTest("int", LaunchKind.Default,    status: "Running", createdAt: now.AddHours(-7));
    await orch.RunReviewerTtlSweepOnceAsync(); // extract the reviewer-TTL check into a testable method
    await Assert.That(orch.StoppedAgents).Contains(("rev", "reviewer_ttl_expired"));
    await Assert.That(orch.StoppedAgents.Select(x => x.Id)).DoesNotContain("int");
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement** — add the config fields + env parsing (`KCAP_REVIEWER_MAX_LIFETIME`/`KCAP_REVIEWER_IDLE_TIMEOUT`, seconds; `0`→`TimeSpan.Zero`); in the heartbeat `foreach`, after the stuck-Starting check, add:

```csharp
if (agent.Kind == LaunchKind.ReviewFlow && agent.Status == "Running") {
    if (_config.ReviewerMaxLifetime > TimeSpan.Zero && _utcNow() - agent.CreatedAt > _config.ReviewerMaxLifetime) {
        agent.PendingEndReason = "reviewer_ttl_expired"; _ = HandleStopAgent(agent.Id); continue;
    }
    if (_config.ReviewerIdleTimeout > TimeSpan.Zero && _utcNow() - agent.LastOutputAt > _config.ReviewerIdleTimeout) {
        agent.PendingEndReason = "reviewer_idle_expired"; _ = HandleStopAgent(agent.Id); continue;
    }
}
```

Route the existing `DateTime.UtcNow` reads in the loop through the `_utcNow()` seam.

- [ ] **Step 4: Run → PASS.**

- [ ] **Step 5: Commit** — `[AI-1313] Phase B D3: reviewer lifetime/idle backstop (6h/2h, 0 disables)`

---

### Task 5: D4a — `ProcessIdentity` (exact OS-native start-identity + liveness + env read)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ProcessIdentity.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests/ProcessIdentityTests.cs`

**Interfaces:**
- Produces: `readonly record struct ProcessStartIdentity(string Kind, long A, long B)` — `Kind` ∈ `linux-starttime`/`macos-tv`/`windows-filetime`; `(A,B)` the native units (macOS `tv_sec`,`tv_usec`; others in `A`, `B=0`). Serialized as-is (native units, no `DateTime`).
- Produces: `static ProcessStartIdentity? Capture(int pid)` — null if unreadable.
- Produces: `static bool Matches(int pid, ProcessStartIdentity expected)` — exact equality against a fresh `Capture(pid)`; false if the pid is gone or identity differs.
- Produces: `static bool IsAlive(int pid)`.
- Produces: `static string? ReadAgentEnv(int pid, string key)` — Linux `/proc/{pid}/environ`, macOS `ps -E -ww -o pid=,command=` parse; null if unreadable (→ caller spares).

- [ ] **Step 1: Write the failing test** — capture the identity of a dummy child this test spawns, assert `Matches` is true for the live pid and false after it exits; assert exact serialize round-trip (native units).

```csharp
[Test]
public async Task Capture_then_Matches_holds_for_live_process_and_fails_after_exit() {
    using var dummy = DummyProcess.StartSleep(seconds: 30);   // test-owned isolated process
    var id = ProcessIdentity.Capture(dummy.Pid)!;
    await Assert.That(ProcessIdentity.Matches(dummy.Pid, id.Value)).IsTrue();
    dummy.Kill(); dummy.WaitForExit();
    await Assert.That(ProcessIdentity.Matches(dummy.Pid, id.Value)).IsFalse();
}

[Test]
public async Task Identity_round_trips_in_native_units() {
    using var dummy = DummyProcess.StartSleep(30);
    var id = ProcessIdentity.Capture(dummy.Pid)!.Value;
    var json = JsonSerializer.Serialize(id, CapacitorJsonContext.Default.ProcessStartIdentity);
    var back = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.ProcessStartIdentity);
    await Assert.That(back).IsEqualTo(id);
    dummy.Kill();
}
```

> Add a `DummyProcess` test helper: spawns a real, isolated child (e.g. `/bin/sleep` on Unix, `ping -n` on Windows) the test fully owns; exposes `Pid`, `Kill()`, `WaitForExit()`, and (for Task 7) accepts custom env vars. This is the ONLY thing tests kill.

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement** `ProcessIdentity` with an OS switch: Linux parses `/proc/{pid}/stat` field 22 (`starttime`, careful with the parenthesized comm containing spaces — split on the last `')'`); macOS P/Invokes `proc_pidinfo(PROC_PIDTBSDINFO)` for `pbi_start_tvsec`/`pbi_start_tvusec`; Windows `GetProcessTimes` creation `FILETIME` → `long`. `ReadAgentEnv`: Linux reads NUL-delimited `/proc/{pid}/environ`; macOS runs `ps -E -ww -o pid=,command=` once and parses the appended env for the pid. Register `ProcessStartIdentity` in `CapacitorJsonContext`.

- [ ] **Step 4: Run → PASS** on this host (macOS dev box — the macOS branch runs; Linux/Windows branches are covered by CI runners on those OSes).

- [ ] **Step 5: Commit** — `[AI-1313] Phase B D4a: ProcessIdentity (exact OS-native start-identity, liveness, env read)`

---

### Task 6: D4b — `AgentPidRecordStore` (atomic records + parse + corrupt-quarantine + enumerate)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (state-dir accessor if not present; `KcapDaemonId` = hash of state-dir path; `KcapDaemonEpoch` = fresh GUID persisted at boot)
- Test: `test/Capacitor.Cli.Daemon.Tests/AgentPidRecordStoreTests.cs`

**Interfaces:**
- Produces: `record AgentPidRecord(string AgentId, int Pid, ProcessStartIdentity StartIdentity, string Kind, string Vendor, string? FlowRunId, string? FlowRole, string DaemonId, string DaemonEpoch, DateTimeOffset SpawnedAt)`.
- Produces: `void Write(AgentPidRecord r)` — temp-file + same-directory `File.Move(overwrite:true)` (atomic); creates `<state-dir>/agents/`.
- Produces: `bool Delete(string agentId)`.
- Produces: `IReadOnlyList<AgentPidRecord> ReadAll()` — parses every `*.json`; on parse failure renames `{agentId}.json`→`{agentId}.json.corrupt` and logs, excludes it (never returned, never acted on).

- [ ] **Step 1: Write the failing test** — write→ReadAll returns it with exact identity; a hand-written corrupt file is renamed `.corrupt` and excluded; `Delete` removes it.

```csharp
[Test]
public async Task Write_ReadAll_Delete_roundtrip_and_corrupt_is_quarantined() {
    var dir = TestDir.Create();
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);
    var rec = new AgentPidRecord("a1", 123, new("linux-starttime", 999, 0), "ReviewFlow", "codex", "f1", "reviewer", "did", "ep", DateTimeOffset.UtcNow);
    store.Write(rec);
    await Assert.That(store.ReadAll().Single().Pid).IsEqualTo(123);
    File.WriteAllText(Path.Combine(dir, "agents", "bad.json"), "{ not json");
    await Assert.That(store.ReadAll().Select(r => r.AgentId)).IsEquivalentTo(new[] { "a1" });
    await Assert.That(File.Exists(Path.Combine(dir, "agents", "bad.json.corrupt"))).IsTrue();
    store.Delete("a1");
    await Assert.That(store.ReadAll()).IsEmpty();
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the store (atomic temp+rename; `try { JsonSerializer.Deserialize } catch { rename .corrupt; log; skip }`). Add `AgentPidRecord` to `CapacitorJsonContext`. Add the state-dir + `KcapDaemonId`/`KcapDaemonEpoch` to config (epoch persisted to `<state-dir>/daemon-epoch` at boot).
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `[AI-1313] Phase B D4b: AgentPidRecordStore (atomic PID records + corrupt quarantine)`

---

### Task 7: D4c — write the record at spawn + pass env markers + `HandleStopAgent` PID-record fallback

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (write record immediately after the runtime spawns, before `RegisterAgentAsync`; delete on confirmed-death teardown; `HandleStopAgent` fallback ~`:906`), `src/Capacitor.Cli.Daemon/Pty/*` + launchers (pass `KCAP_AGENT_ID`/`KCAP_DAEMON_ID`/`KCAP_DAEMON_EPOCH` in the child env)
- Test: `test/Capacitor.Cli.Daemon.Tests/StopAgentFallbackTests.cs`

**Interfaces:**
- Consumes: `AgentPidRecordStore` (Task 6), `ProcessIdentity` (Task 5).
- Produces: env markers on every hosted-agent child; a PID record written at `runtime.Pid` capture time.
- Produces: `HandleStopAgent(agentId)` for an id absent from `_agents` consults the record → kills by exact `(pid, identity)` + Unix env `KCAP_AGENT_ID` check → confirmed-death delete.

- [ ] **Step 1: Write the failing test** — `HandleStopAgent` for an unknown id whose record points at a live **dummy** (with `KCAP_AGENT_ID` in its env) kills it; an identity-mismatched record leaves the dummy alone and deletes the record.

```csharp
[Test]
public async Task StopAgent_unknown_id_kills_by_pid_record_with_env_check() {
    var dir = TestDir.Create();
    using var dummy = DummyProcess.StartSleep(30, env: new() { ["KCAP_AGENT_ID"] = "ghost" });
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);
    store.Write(new AgentPidRecord("ghost", dummy.Pid, ProcessIdentity.Capture(dummy.Pid)!.Value, "ReviewFlow", "codex", null, null, "did", "ep", DateTimeOffset.UtcNow));
    var orch = TestOrchestrator.Create(pidStore: store);
    await orch.HandleStopAgent("ghost");
    dummy.WaitForExit(TimeSpan.FromSeconds(8));
    await Assert.That(dummy.HasExited).IsTrue();
    await Assert.That(store.ReadAll()).IsEmpty(); // confirmed-death delete
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** — after the runtime spawns (pid known) write the record before any await; on confirmed-death teardown delete it (retain on unconfirmed — Task 8); thread `KCAP_AGENT_ID`/`KCAP_DAEMON_ID`/`KCAP_DAEMON_EPOCH` into the child env in the launcher/PTY spawn env; extend `HandleStopAgent`'s `!_agents.TryGetValue` branch to the record-based kill (record regime: exact identity **+** Unix env check; `killpg(pid)`→5s→`SIGKILL`; delete only on confirmed death).
- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** — `[AI-1313] Phase B D4c: write PID record at spawn + env markers + StopAgent record fallback`

---

### Task 8: D1 + D4(2a) — single-flight teardown + kill-quarantine + `EffectiveCount`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AgentKillQuarantine.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`AgentInstance.CleanupStarted` flag; single-flight teardown routed from the launch catch `:563` and the read-loop finally; the `:330` admission gate → `EffectiveCount`; heartbeat retry of quarantined kills)
- Test: `test/Capacitor.Cli.Daemon.Tests/LaunchCleanupTests.cs`, `AgentKillQuarantineTests.cs`

**Interfaces:**
- Produces: `AgentInstance` gains `int CleanupStarted` (via `Interlocked.CompareExchange(ref CleanupStarted, 1, 0) == 0` gate).
- Produces: `AgentKillQuarantine` — `Add(context)`, `bool TryRetryAll()` (heartbeat calls; kill+confirm each; remove on confirmed death), `int Count`, `IReadOnlyList<QuarantinedAgentInfo> Snapshot()`.
- Produces: `AgentOrchestrator.EffectiveCount => ActiveCount + _quarantine.Count`; the `:330` guard uses it.

- [ ] **Step 1: Write the failing test (single-flight)** — a post-`:515`-insert launch failure removes the agent from `_agents`, terminates the runtime, and sends **both** `LaunchFailed` + `AgentUnregistered` even if an intermediate teardown step throws; racing read-loop cleanup + catch cleanup run the teardown exactly once.

```csharp
[Test]
public async Task Post_insert_launch_failure_tears_down_once_and_notifies() {
    var server = new FakeServerConnection();
    var orch = TestOrchestrator.Create(server, failRegisterAgent: true); // force RegisterAgentAsync to throw
    await orch.HandleLaunchAgent(new LaunchAgentCommand(AgentId: "a1", Model: "default", RepoPath: TestRepo.Path, Vendor: "codex"));
    await Assert.That(orch.ContainsAgent("a1")).IsFalse();
    await Assert.That(server.LaunchFailed).Contains("a1");
    await Assert.That(server.AgentUnregistered).Contains("a1");
    await Assert.That(orch.LastRuntimeTerminatedFor("a1")).IsTrue();
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the single-flight `TeardownAgentAsync(agent, reason, confirmedDeathRequired: true)` — `CleanupStarted` gate; each step (`ReadCts.Cancel`, `Runtime.TerminateAsync(5s)`, token revoke, worktree remove, `_agents.TryRemove`) in its own try/catch; `LaunchFailedAsync` + `AgentUnregisteredAsync` in a `finally`; PID-record delete only on confirmed death, else move the launch context into `_quarantine`. Route the launch catch (`:563`) and the read-loop finally through it.
- [ ] **Step 4: Write the failing test (quarantine + EffectiveCount)** — an unconfirmed-death teardown adds to `_quarantine`; `EffectiveCount` includes it; the heartbeat retry kills a now-dead dummy and drains it; `Snapshot()` surfaces it.

```csharp
[Test]
public async Task Unconfirmed_death_quarantines_counts_and_heartbeat_retry_drains() {
    using var dummy = DummyProcess.StartSleep(30, env: new(){["KCAP_AGENT_ID"]="q1"});
    var q = new AgentKillQuarantine(NullLogger.Instance);
    q.Add(new QuarantineEntry("q1", dummy.Pid, ProcessIdentity.Capture(dummy.Pid)!.Value, "ReviewFlow", DateTimeOffset.UtcNow, null, null));
    await Assert.That(q.Count).IsEqualTo(1);
    q.TryRetryAll(); dummy.WaitForExit(TimeSpan.FromSeconds(8));
    q.TryRetryAll(); // second pass observes confirmed death → drains
    await Assert.That(q.Count).IsEqualTo(0);
}
```

- [ ] **Step 5: Run → PASS**; then wire `_quarantine.TryRetryAll()` into the heartbeat loop and `EffectiveCount` into the `:330` guard.
- [ ] **Step 6: Commit** — `[AI-1313] Phase B D1+D4(2a): single-flight teardown + kill-quarantine + EffectiveCount admission gate`

---

### Task 9: D4(3) — `OrphanReaper` (startup record pass + scoped env-marker scan)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/DaemonRunner.cs` (call at boot under the daemon lock, next to `WorktreeManager.CleanupOrphanedAsync`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (re-run the scan on a heartbeat tick)
- Test: `test/Capacitor.Cli.Daemon.Tests/OrphanReaperTests.cs`

**Interfaces:**
- Consumes: `AgentPidRecordStore` (Task 6), `ProcessIdentity` (Task 5), config `KcapDaemonId`/`KcapDaemonEpoch` (Task 6).
- Produces: `Task ReapOnceAsync()` — (a) record pass: each leftover record whose `(pid, identity)` matches a live process (+ Unix env check) → `killpg`→5s→`SIGKILL`, delete on confirmed death; identity-mismatch → delete record; (b) env-marker scan (Unix): enumerate same-user processes, kill only those with `KCAP_AGENT_ID` **and** `KCAP_DAEMON_ID == this` **and** `KCAP_DAEMON_EPOCH != current` that matched no just-processed record; enumeration failure → logged no-op (retried next tick).

- [ ] **Step 1: Write the failing test (record pass)** — a leftover record pointing at a live dummy (matching identity + env) is killed and its record deleted; an identity-mismatched record is deleted and the dummy spared.

```csharp
[Test]
public async Task Startup_record_pass_kills_matching_dummy_and_deletes_record() {
    var dir = TestDir.Create();
    using var dummy = DummyProcess.StartSleep(30, env: new(){["KCAP_AGENT_ID"]="orphan"});
    var store = new AgentPidRecordStore(dir, NullLogger.Instance);
    store.Write(new AgentPidRecord("orphan", dummy.Pid, ProcessIdentity.Capture(dummy.Pid)!.Value, "ReviewFlow","codex",null,null,"did","old-epoch", DateTimeOffset.UtcNow));
    var reaper = new OrphanReaper(store, daemonId: "did", currentEpoch: "new-epoch", NullLogger.Instance);
    await reaper.ReapOnceAsync();
    dummy.WaitForExit(TimeSpan.FromSeconds(8));
    await Assert.That(dummy.HasExited).IsTrue();
    await Assert.That(store.ReadAll()).IsEmpty();
}
```

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** the record pass.
- [ ] **Step 4: Write the failing test (env-marker scan)** — a **recordless** dummy carrying this daemon's `KCAP_DAEMON_ID` + a stale epoch is killed; a dummy with a **different** daemon id is spared; a dummy with the **current** epoch is spared; enumeration failure → logged no-op.

```csharp
[Test]
public async Task Env_marker_scan_kills_stale_epoch_of_this_daemon_only() {
    using var stale = DummyProcess.StartSleep(30, env: new(){["KCAP_AGENT_ID"]="s","KCAP_DAEMON_ID"="did","KCAP_DAEMON_EPOCH"="old"});
    using var other = DummyProcess.StartSleep(30, env: new(){["KCAP_AGENT_ID"]="o","KCAP_DAEMON_ID"="OTHER","KCAP_DAEMON_EPOCH"="old"});
    using var mine  = DummyProcess.StartSleep(30, env: new(){["KCAP_AGENT_ID"]="m","KCAP_DAEMON_ID"="did","KCAP_DAEMON_EPOCH"="new"});
    var reaper = new OrphanReaper(EmptyStore(), daemonId: "did", currentEpoch: "new", NullLogger.Instance);
    await reaper.ReapOnceAsync();
    stale.WaitForExit(TimeSpan.FromSeconds(8));
    await Assert.That(stale.HasExited).IsTrue();
    await Assert.That(other.HasExited).IsFalse();
    await Assert.That(mine.HasExited).IsFalse();
    other.Kill(); mine.Kill();
}
```

- [ ] **Step 5: Run → PASS**; wire `ReapOnceAsync` at boot (under the daemon lock) and re-run on a heartbeat tick.
- [ ] **Step 6: Commit** — `[AI-1313] Phase B D4(3): startup orphan reap + scoped env-marker scan`

---

### Task 10: docs + PR

**Files:** `docs/gotchas/` (a CLI daemon self-defense note if a relevant gotcha file exists), the CLI `README`/daemon docs if they enumerate config keys (add `reviewer_max_lifetime`/`reviewer_idle_timeout`), this plan's checkboxes.

- [ ] **Step 1** — document the new config keys + the PID-record/orphan-reap model where the daemon's other lifecycle behavior is documented.
- [ ] **Step 2** — full daemon test suite green on this host; note the Linux/Windows-only identity branches are covered by CI runners.
- [ ] **Step 3** — push the branch; open the PR base `main`, title `[AI-1313] Flow-reviewer capacity reaping — daemon self-defense (Phase B)`, body linking the spec + AI-1313 + noting the two deferred follow-ons (native containment, sequenced settlement).
- [ ] **Step 4** — Codex review flow (`mode context-only`, full diff inlined) → iterate to clean; address Qodo (all tiers).
- [ ] **Step 5** — request Alexey; post an AI-1313 Linear comment (do NOT set state); update memory.

---

## Deferred to follow-on plans (independent subsystems — separate plans per the scope rule)

- **Plan B-native (D4 layer 1 — OS containment):** Windows creation-time Job Object via `STARTUPINFOEX` + `PROC_THREAD_ATTRIBUTE_JOB_LIST` in `ConPtyProcess.Spawn` (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, kill-on-close incl. descendants → no survivor class on Windows). Linux `PR_SET_PDEATHSIG(SIGKILL)` on a **single dedicated daemon-lifetime spawner thread** (never pooled; unexpected thread exit fail-fasts the daemon) with an **async-signal-safe post-fork native shim** (`prctl` → `getppid()!=expectedParent ? raise(SIGKILL)` → `execve`; prebuilt argv/envp; no managed re-entry). Pure hardening: this PR's records/scan already recover survivors; layer 1 only makes death immediate. Needs native interop + a C shim + per-OS CI. (Spec §6.4(1); §10 macOS parity remains a follow-up — no OS primitive.)
- **Plan B2 (sequenced-command settlement — bilateral, server + CLI):** `LaunchAgentCommand`/`StopAgentV2` gain `Epoch`/`Seq`; daemon acks fully-processed watermarks (`AckProcessedPrefix`), emits `CommandAck`/`CommandRejected`/`ResolvedStartupCandidates`; server maintains the generation-settlement ledger, the S5 launch queue, and the S3(b)/(c) untracked-reap + retry-until-gone + S6 daemon-authoritative consumers of `DaemonStatusReport`, all capability-gated on `SupportsSequencedCommands`. This is the hardest, most-tested piece (spec §5.5, §7, §8 "Settlement") and the reason the spec took 14 Codex rounds; it needs its own spec-grounded plan and a paired server PR.

---

## Self-Review

**Spec coverage (this PR's scope):** D1 ✓(T8) · D2 metadata+LiveAgents ✓(T1,T2) + status-report send ✓(T3) · D3 ✓(T4) · D4(2) records+identity ✓(T5,T6,T7) · D4(2a) quarantine+EffectiveCount ✓(T8) · D4(3) startup reap + env scan + StopAgent fallback ✓(T7,T9). **Deferred (own plans):** D4(1) native containment; the sequenced protocol + its server consumers + the `DaemonStatusReport` server handler. Compatibility (§7): all additive — verified by T1's old-JSON test + one-way `SendAsync` in T3.

**Placeholder scan:** the two flagged "verify at execution time" items (daemon test-project path/namespace; the exact state-dir/`DaemonLock` accessor near `DaemonRunner.cs:380`) are real repo-location lookups the implementer resolves in Task 1/Task 6 — not design gaps. `DummyProcess`/`TestDir`/`TestOrchestrator`/`FakeServerConnection` helpers are introduced in T5/T6/T2 and reused; if the daemon test project already has equivalents, use those.

**Type consistency:** `LiveAgentInfo`/`QuarantinedAgentInfo`/`DaemonStatusReport`/`AgentPidRecord`/`ProcessStartIdentity` signatures are defined once (T1/T5/T6) and consumed unchanged (T2/T3/T7/T8/T9). `EffectiveCount = ActiveCount + _quarantine.Count`; `ActiveCount` unchanged. Reviewer end-reasons `reviewer_ttl_expired`/`reviewer_idle_expired` consistent (T4). Env markers `KCAP_AGENT_ID`/`KCAP_DAEMON_ID`/`KCAP_DAEMON_EPOCH` consistent (T7/T9).
