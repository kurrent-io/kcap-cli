# Daemon restart with agent resume (core) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A daemon restart no longer waits for idle: it stops every interactive hosted agent for resume, restarts, and brings each one back through its vendor's native resume with a nudge, falling back to kcap continue.

**Architecture:** A durable per-agent resume ledger under the daemon's state directory is written at launch and read at boot. Shutdown gains an intent (keep agents or end them) carried over a new IPC frame, and the restart gate stops counting interactive agents as busy. At boot the successor reaps survivors, claims its agents on connect, spares their worktrees, and relaunches each through the normal launch path with the vendor's resume id.

**Tech Stack:** .NET 10 NativeAOT, System.Text.Json source generation, TUnit, SignalR client. Daemon `src/Capacitor.Cli.Daemon`, shared `src/Capacitor.Cli.Core`, CLI `src/Capacitor.Cli`.

**Spec:** `docs/superpowers/specs/2026-10-17-daemon-restart-agent-resume-design.md`. This plan covers the daemon and CLI core: the ledger, restart for resume, boot resume for Claude, Codex, Pi, Muse and Antigravity, stop semantics, hook and watcher suppression, CLI surface and docs. The server changes (spec §7), the ACP vendors (§4.4) and the desktop app (§8) are separate plans.

## Global Constraints

- `FrameType` values are append-only and never reused: new client frames continue from 27, new daemon frames from 82. Every new frame is advertised in `LocalControlCapabilities.Current` beside a live handler.
- The daemon assembly never calls `Process.Kill(bool)`; process trees go through `ProcessTree.Kill`. Ambiguity never kills: an unresolvable predecessor is reported, not reaped.
- A launch bypasses `LaunchConsentGate` only on the resume lane, for ledger entries the daemon wrote, and writes a `ConsentDecisionRecord` naming the original decision.
- Env knobs and defaults, verbatim from the spec: `KCAP_RESTART_TURN_WAIT_SECONDS` 300, `KCAP_RESUME_MAX_AGE_SECONDS` 86400, `KCAP_RESUME_CONCURRENCY` 2; config key `daemon.auto_resume` default true; heartbeat every 60 s; ring cap `TerminalOutputBuffer.MaxBytes`; ledger `schema_version` 1.
- Ledger states and reasons, verbatim: `running`, `resuming`, `interrupted`; reasons `too_old`, `auto_resume_off`, `predecessor_process_unresolved`, `ledger_newer_than_daemon`, `stopped_by_operator`, `worktree_missing`; server reason `resume_failed:<reason>`; end reason `superseded`.
- On-disk JSON goes through a `JsonSerializerContext` (NativeAOT). `JsonArray` uses the constructor, never a collection expression. Publish with `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` before the final task and fix every hit.
- Tests follow CLAUDE.md: `TempDir` / `[TempDaemonPaths]` fixtures, `EnvScope.Exclusive` with bare `[NotInParallel]` for env vars, `ConsoleOutput.StartCapture()` for console, no `Path.Combine(tmp.Path, …)`. Filter one class with `--treenode-filter "/*/*/ClassName/*"`.
- One type per file, named after the type; an enum may share a file with its extensions. No `InternalsVisibleTo` to a production assembly.
- Comments: scarce, present-tense, no change narration, no ticket ids, no review artefacts.
- README and the `help-*.txt` resources change in the same PR as the CLI surface (Task 16).
- Commit subjects are one imperative clause under 80 characters with no issue reference until one exists; every commit ends with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.

## Review Focus

1. A ledger entry whose worktree directory is gone at boot (deleted by hand): the agent becomes `interrupted` with reason `worktree_missing`, never a crash or a fresh checkout. Test in Task 8.
2. A `.ring` file that is truncated or larger than `TerminalOutputBuffer.MaxBytes`: the replay appends what fits and the resume proceeds. Test in Task 5.
3. A hook whose daemon's resume directory is unreadable: suppression fails open and the session end posts as today. Test in Task 15.
4. `kcap daemon stop` when the daemon acks the shutdown but never exits: the CLI kills it after its bound and marks the ledger `interrupted`. Test in Task 14.
5. A resume whose vendor process exits before its handshake: the lane records the exit code in `last_error`, tries kcap continue once, and never loops. Test in Task 8.

---

### Task 1: Resume ledger types and store (Core)

**Files:**
- Create: `src/Capacitor.Cli.Core/Resume/ResumeState.cs`
- Create: `src/Capacitor.Cli.Core/Resume/ResumeLaunchDescriptor.cs`
- Create: `src/Capacitor.Cli.Core/Resume/ResumeLedgerEntry.cs` (also holds `ResumeLedgerJsonContext`, as `HelloIpc.cs` holds its context)
- Create: `src/Capacitor.Cli.Core/Resume/ResumeLedgerStore.cs`
- Modify: `src/Capacitor.Cli.Core/DaemonStore.cs:106` (add two path helpers)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Resume/ResumeLedgerStoreTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace Capacitor.Cli.Core.Resume;
  public enum ResumeState { Running, Resuming, Interrupted }   // wire: snake_case via JsonStringEnumMemberName
  public sealed record ResumeLaunchDescriptor(
      string? Prompt, string? Model, string? Effort, string RepoPath, string WorktreePath, WorkLocation Work,
      string? GitRelativeCwd, string? SandboxPolicy, string? ApprovalPolicy, string? PermissionPreset,
      string? McpConfigPath, string? Title, bool IsPrivate, string Origin /* "server" | "local" */,
      string? RequesterUserId, string? RequesterDisplay, string? ConsentSource, string? ConsentAt,
      string? FlowRunId, string? FlowRole);
  public sealed record ResumeLedgerEntry(
      int SchemaVersion, string AgentId, string DaemonId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
      string Vendor, string Transport, string? SessionId, string? VendorSession, ResumeLaunchDescriptor Launch,
      ResumeState State, int Attempts, string? LastError, string? PredecessorInstanceId, DateTimeOffset? ResumingAt) {
      public const int CurrentSchemaVersion = 1;
  }
  public sealed class ResumeLedgerStore(string resumeDirectory) {
      public static ResumeLedgerStore For(DaemonStore store, string daemonName);
      public static string FileStem(string agentId);               // lower-hex sha256 of the agent id
      public string EntryPath(string agentId); public string RingPath(string agentId);
      public void Write(ResumeLedgerEntry entry);                   // temp file + File.Move overwrite
      public bool Delete(string agentId);                           // entry and ring; false when absent
      public ResumeLedgerEntry? TryRead(string agentId);            // null when absent; unparseable → renamed to .corrupt, null
      public IReadOnlyList<ResumeLedgerEntry> ReadAll();
      public int MarkInterrupted(string reason, Func<ResumeLedgerEntry, bool>? where = null);
      public static ResumeLedgerEntry? FindByAgentId(DaemonStore store, string agentId); // scans <daemons>/*/resume
  }
  // DaemonStore
  public string ResumeDirectory(string daemonName) => Path.Combine(StateDirectory(daemonName), "resume");
  public string HeartbeatPath(string daemonName)   => File(daemonName, "heartbeat");
  ```
- A `TryRead` of an entry with `SchemaVersion > CurrentSchemaVersion` returns it with `State = Interrupted` and `LastError = "ledger_newer_than_daemon"`. Unknown JSON members are ignored.

- [ ] **Step 1: Write the failing tests** in `ResumeLedgerStoreTests` using `using var tmp = new TempDaemonStore();`:
  - `Write_then_TryRead_round_trips_every_field` (build a full entry, assert equality).
  - `Delete_removes_entry_and_ring_and_reports_absence` (write entry + a ring file at `RingPath`; `Delete` true then false; both files gone).
  - `TryRead_quarantines_an_unparseable_file` (`tmp.CreateFile("resume/<stem>.json", "{not json")` → null, and a `.corrupt` sibling exists).
  - `ReadAll_ignores_unknown_members` (write JSON with an extra `"future": 1`).
  - `Newer_schema_reads_as_interrupted_with_reason` (SchemaVersion 2 → `Interrupted`, `ledger_newer_than_daemon`).
  - `MarkInterrupted_rewrites_matching_entries_only` (two entries, predicate on one; reason copied to `LastError`, `UpdatedAt` advanced).
  - `FindByAgentId_scans_every_daemon_directory` (two daemon names, entry under the second; found; a corrupt file under the first does not throw).
- [ ] **Step 2: Run** `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ResumeLedgerStoreTests/*"` → FAIL (types missing).
- [ ] **Step 3: Implement** the four Core files and the two `DaemonStore` helpers. Atomic write copies `AgentPidRecordStore.Write` (`src/Capacitor.Cli.Daemon/Services/AgentPidRecordStore.cs:20-28`); quarantine copies its `.corrupt` rename (`:109-178`). `FindByAgentId` enumerates `Directory.EnumerateDirectories(store.Directory)` and calls `TryRead` on each `resume` child, swallowing `IOException`/`UnauthorizedAccessException` per directory.
- [ ] **Step 4: Run** the filter above → PASS.
- [ ] **Step 5: Commit** `git add src/Capacitor.Cli.Core/Resume src/Capacitor.Cli.Core/DaemonStore.cs test/Capacitor.Cli.Core.Tests.Unit/Resume` and `git commit -m "Add the resume ledger store" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"`.

### Task 2: Shutdown and agent-resume IPC frames (Core)

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs:41,63`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs:22` (factories beside `RestartAck`)
- Create: `src/Capacitor.Cli.Core/LocalIpc/ShutdownIpc.cs` (`ShutdownRequestDto` + `ShutdownIpcJsonContext`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecShutdownTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  // FrameType additions
  Shutdown    = 28, // Text = ShutdownRequestDto JSON
  AgentResume = 29, // Text = agent id
  ShutdownAck    = 83, // Text = "stopping"
  AgentResumeAck = 84, // Text = "resuming" | "not_interrupted" | "unknown"
  public sealed record ShutdownRequestDto(string Agents /* "keep" | "end" */, string Exit /* "relaunch" | "final" */);
  public static LocalFrame Shutdown(ShutdownRequestDto dto); public static LocalFrame ShutdownAck(string status);
  public static LocalFrame AgentResume(string agentId);     public static LocalFrame AgentResumeAck(string status);
  public static ShutdownRequestDto? ShutdownIpc.Parse(string text); // null on malformed or unknown values
  ```

- [ ] **Step 1: Write the failing tests**: `Shutdown_frame_round_trips_through_the_codec` (write with `FrameCodec.WriteAsync`, read back, `Type == FrameType.Shutdown`, parsed dto equals input); `Parse_rejects_unknown_agents_or_exit_values` (`{"agents":"maybe","exit":"final"}` → null); `Numeric_values_are_pinned` (`(int)FrameType.Shutdown == 28`, `AgentResume == 29`, `ShutdownAck == 83`, `AgentResumeAck == 84`). Model on `FrameCodecHelloTests.cs`.
- [ ] **Step 2: Run** `--treenode-filter "/*/*/FrameCodecShutdownTests/*"` → FAIL.
- [ ] **Step 3: Implement** the enum members, the DTO with a snake-case `JsonSerializerContext` like `HelloIpcJsonContext` (`HelloIpc.cs:29-32`), the factories, and `Parse`.
- [ ] **Step 4: Run** → PASS. Also run `FrameCodecHelloTests` to confirm nothing shifted.
- [ ] **Step 5: Commit** `Add shutdown and agent-resume IPC frames`.

### Task 3: Ledger writes at launch, id discovery and normal end (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (partial class)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:49-62` (new `AgentInstance` members), `:2118` (`HandleAdmittedLaunchAsync`), `:2712` and `:5259` (session id assignment), `:5447` (`CleanupAgentAsync`)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:305-394` (local spawn writes the ledger too, `Origin = "local"`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorResumeLedgerTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces:
  ```csharp
  // AgentInstance
  public bool ResumePending { get; set; }            // set by Task 5; CleanupAgentAsync keeps worktree, records and ledger when true
  public string? VendorSession { get; set; }         // the id a resume takes; Task 9 pins it for Claude
  // AgentOrchestrator (Resume partial)
  internal ResumeLedgerStore? ResumeLedger { get; }   // null when _config.Store is null (tests without a store)
  internal ResumeLedgerEntry BuildLedgerEntry(AgentInstance agent, LaunchAgentCommand? cmd, string origin, LaunchConsentOutcome? consent);
  void WriteLedger(AgentInstance agent);              // Write(BuildLedgerEntry(...)) with State Running; swallows IOException with a warning
  void UpdateLedgerSession(AgentInstance agent);      // rewrites SessionId / VendorSession, bumps UpdatedAt
  static bool IsInteractive(AgentInstance a) => a.Kind == LaunchKind.Default;
  ```
- `HandleAdmittedLaunchAsync` calls `WriteLedger` after the consent decision and before the runtime starts. `UpdateLedgerSession` runs where `agent.SessionId` is assigned (`:2712`, `:5259`). `CleanupAgentAsync` calls `ResumeLedger?.Delete(agentId)` unless `agent.ResumePending`. Flow participants (`Kind != LaunchKind.Default`) get no ledger entry.

- [ ] **Step 1: Write the failing tests** with `AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), launchers)` and `orch.SubmitLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch(...))` or `SeedAgentForTest`:
  - `Admitted_launch_writes_a_running_ledger_entry_before_the_runtime_starts` (spy launcher asserts the entry exists when `StartAsync` runs; `State == Running`, `Origin == "server"`, `ConsentSource` equals the gate's `Source`).
  - `Learning_the_session_id_rewrites_the_entry` (set `agent.SessionId` through the discovery path; `TryRead(...).SessionId` equals).
  - `Normal_end_deletes_the_entry` (`CleanupAgentForTest`; `TryRead` null).
  - `A_pending_resume_keeps_the_entry_and_the_pid_record` (`ResumePending = true` then cleanup; entry and PID record remain).
  - `A_flow_participant_writes_no_entry` (`SeedAgentForTest(kind: a review-flow kind)`; `ReadAll()` empty).
- [ ] **Step 2: Run** `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorResumeLedgerTests/*"` → FAIL.
- [ ] **Step 3: Implement.** `ResumeLedger` is built in the constructor from `_config.Store` and `_config.Name` (`ResumeLedgerStore.For`). The descriptor copies `Prompt`, `Model`, `Effort`, `RepoPath`, `Worktree.Path`, `Work`, `Worktree.GitRelativeCwd`, `SandboxPolicy`, `ApprovalPolicy`, `PermissionPreset`, `McpConfigPath`, `Title`, `IsPrivate`, `RequesterUserId`, `RequesterDisplay`, `FlowRunId`, `FlowRole`; `ConsentAt` is `time.GetUtcNow().ToString("O")`.
- [ ] **Step 4: Run** → PASS; run `ShutdownAgentReportTests` and `WorktreeManagerTests` to confirm no regression.
- [ ] **Step 5: Commit** `Write the resume ledger through an agent's life`.

### Task 4: Daemon heartbeat (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/DaemonHeartbeat.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs:613` (register beside `RestartCoordinator`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DaemonHeartbeatTests.cs`

**Interfaces:**
- Produces: `internal sealed class DaemonHeartbeat(DaemonConfig config, TimeProvider time, ILogger<DaemonHeartbeat> logger) : BackgroundService` with `public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);`, `internal void Beat()` (touches `config.Store.HeartbeatPath(config.Name)`, creating it), and `public static DateTimeOffset? LastBeat(DaemonStore store, string name)` (file mtime, null when absent).

- [ ] **Step 1: Write the failing tests** with `FakeTimeProvider`: `Beat_creates_the_file_and_LastBeat_reads_its_time`; `Loop_beats_once_per_interval` (advance 60 s twice, mtime advances; use `Beat()` directly for the first assertion and the service loop for the second); `LastBeat_is_null_without_a_file`.
- [ ] **Step 2: Run** `--treenode-filter "/*/*/DaemonHeartbeatTests/*"` → FAIL.
- [ ] **Step 3: Implement** with `PeriodicTimer` over `time`, `File.SetLastWriteTimeUtc` on an existing file, swallowing `IOException` with one warning per failure.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Beat a heartbeat file while the daemon runs`.

### Task 5: Shutdown intent and stop-for-resume (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ShutdownIntent.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs`, `AgentOrchestrator.cs:5555` (`ReportAgentsEndedForShutdownAsync`), `:5595-5677` (`DisposeAsync`), `:5447` (`CleanupAgentAsync`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorStopForResumeTests.cs`; extend `ShutdownAgentReportTests.cs`

**Interfaces:**
- Consumes: Task 3.
- Produces:
  ```csharp
  internal enum ShutdownIntent { KeepAgents, EndAgents }
  // AgentOrchestrator
  internal ShutdownIntent ShutdownIntent { get; set; } = ShutdownIntent.KeepAgents;
  internal async Task PrepareForResumeAsync(AgentInstance agent, CancellationToken ct);
  internal static readonly byte[] RestartMarker = "\r\n── kcap daemon restarted; session resumed ──\r\n"u8.ToArray();
  ```
- `PrepareForResumeAsync`: set `ResumePending`; write the ledger with `State Resuming`, `PredecessorInstanceId = _config.InstanceId`, `ResumingAt = now`; close `LocalSinks` and stop `CloudSink`; write `OutputBuffer.Snapshot()` to `ResumeLedger.RingPath`; `await agent.Runtime.RequestGracefulStopAsync()` then `await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5))`.
- `DisposeAsync` under `KeepAgents`: interactive agents go through `PrepareForResumeAsync` in parallel; a reviewer with `IsResumableReviewer` goes through the park path; other agents follow today's terminate-and-report path. `ReportAgentsEndedForShutdownAsync` filters `!a.ResumePending`. `CleanupAgentAsync` skips `_worktreeManager.RemoveAsync`, attachments, the MCP config file and `_pidRecords.Delete` for `ResumePending` agents.
- Under `EndAgents`, every path is today's.

- [ ] **Step 1: Write the failing tests**:
  - `KeepAgents_marks_interactive_agents_resuming_and_reports_no_shutdown_failure` (seed two Running agents with `FakeHostedAgentRuntime`; dispose; `CaptureServerConnection.ShutdownReports` empty; both ledger entries `Resuming` with the daemon's `InstanceId`).
  - `KeepAgents_persists_the_ring_and_keeps_the_worktree_and_pid_record` (append bytes to `OutputBuffer`; ring file equals `Snapshot()`; worktree directory still exists; `AgentPidRecordStore.Exists` true).
  - `KeepAgents_calls_graceful_stop_before_terminate` (`FakeHostedAgentRuntime` records call order).
  - `EndAgents_keeps_todays_shutdown_report` (set `ShutdownIntent.EndAgents`; one report per agent with `daemon_shutdown`; ledger entries deleted).
  - `A_resumable_reviewer_is_parked_not_prepared` (seed with `FakeAppServerRuntime` as in `AgentOrchestratorReviewerParkTests.cs:386`; park report sent; no ledger entry).
  - Review Focus 2, in Task 8's replay helper but pinned here: `Ring_replay_caps_at_MaxBytes_and_tolerates_truncation` (`ReplayRing(agent, bytes)` with `MaxBytes + 1` bytes appends `MaxBytes`; a zero-length file appends only the marker).
- [ ] **Step 2: Run** `--treenode-filter "/*/*/AgentOrchestratorStopForResumeTests/*"` → FAIL.
- [ ] **Step 3: Implement** in the Resume partial; keep `DisposeAsync`'s existing order (lane stop, cancel, cloud sinks, drain, agents, report, cleanup) and branch only at the per-agent step. Add `internal void ReplayRing(AgentInstance agent, byte[] ring)` that appends at most `TerminalOutputBuffer.MaxBytes` then `RestartMarker`.
- [ ] **Step 4: Run** the new class plus `ShutdownAgentReportTests` and `AgentOrchestratorDisposeTests` → PASS.
- [ ] **Step 5: Commit** `Stop interactive agents for resume on a keep-agents shutdown`.

### Task 6: Restart gate (Daemon)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs:36-38,61,105-160`, `src/Capacitor.Cli.Daemon/Services/RestartDecision.cs`, `src/Capacitor.Cli.Daemon/DaemonConfig.cs:12`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs:135` (env parse), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs`
- Test: extend `test/Capacitor.Cli.Daemon.Tests.Unit/Services/RestartCoordinatorTests.cs` and `RestartDecisionTests.cs`

**Interfaces:**
- Consumes: Task 5 (`IsInteractive`).
- Produces:
  ```csharp
  // DaemonConfig
  public TimeSpan RestartTurnWait { get; set; } = TimeSpan.FromMinutes(5);   // KCAP_RESTART_TURN_WAIT_SECONDS
  // AgentOrchestrator
  internal int  LaunchesInAdmission { get; }        // incremented after the fence admits, decremented after WriteLedger
  internal int  FlowParticipantsMidRound => _agents.Values.Count(a => !IsInteractive(a) && a.Status is "Starting" or "Running" && a.ActivityClock.TurnInFlight);
  internal bool InteractiveAgentsAtTurnBoundary => _agents.Values.Where(a => IsInteractive(a) && a.Status is "Starting" or "Running").All(a => a.ActivityClock.AwaitingInput);
  // RestartCoordinator
  internal Func<bool> AtTurnBoundary;               // seam like IsBusy
  // RestartDecision
  public static bool ShouldFire(bool pending, bool busy, bool force, bool selfDetected, bool atTurnBoundary, bool waitExpired)
      => pending && !busy && (force || !selfDetected || atTurnBoundary || waitExpired);
  ```
- `IsBusy` becomes `evalCache.Count > 0 || orchestrator.LaunchesInAdmission > 0 || orchestrator.FlowParticipantsMidRound > 0`. `force` also ignores `FlowParticipantsMidRound`. A self-detected pending restart records `_pendingSince`; `waitExpired` is `now - _pendingSince >= config.RestartTurnWait`. `RequestRestart` clears the self-detected flag so an explicit request never waits.

- [ ] **Step 1: Write the failing tests**:
  - `RestartDecisionTests`: `Self_detected_waits_for_a_turn_boundary`, `Self_detected_fires_when_the_wait_expires`, `Explicit_request_fires_without_a_boundary`, `Force_ignores_busy_flow_participants_but_not_evals` (busy is composed by the caller; assert the truth table).
  - `RestartCoordinatorTests` (using `ForTest` and `SpyStrategy`): `Binary_change_with_agents_mid_turn_waits_until_the_boundary` (`IsBusy = () => false`, `AtTurnBoundary = () => false`, tick → not fired; flip to true, tick → fired), `Binary_change_fires_at_the_bound_without_a_boundary` (FakeTimeProvider advanced past `RestartTurnWait`), `Running_interactive_agents_do_not_block_an_explicit_restart`.
- [ ] **Step 2: Run** `--treenode-filter "/*/*/RestartCoordinatorTests/*"` and the decision class → FAIL.
- [ ] **Step 3: Implement**; keep `Binary_change_while_idle_triggers_restart` passing (`AtTurnBoundary` defaults to `() => true` in `ForTest`). Parse the env knob in `DaemonRunner` next to `:135` with `ParseSecondsEnv("KCAP_RESTART_TURN_WAIT_SECONDS", config.RestartTurnWait)`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Let hosted agents stop blocking a restart`.

### Task 7: Shutdown IPC handler (Daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ShutdownIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs:13-18,45-82`, `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs:26`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs:837-839`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlShutdownTests.cs`; extend `LocalControlCapabilitiesTests.cs`

**Interfaces:**
- Consumes: Task 2, Task 5, Task 6.
- Produces:
  ```csharp
  internal sealed class ShutdownIpc(AgentOrchestrator orchestrator, RestartCoordinator restart, IHostApplicationLifetime lifetime, RestartState restartState, ILogger<ShutdownIpc> logger) {
      public async Task HandleAsync(string text, Stream stream, CancellationToken ct);
  }
  ```
  `HandleAsync`: parse with `ShutdownIpc.Parse`; malformed → `LocalFrame.Error("shutdown: malformed request")`. Set `orchestrator.ShutdownIntent` (`keep` → `KeepAgents`, `end` → `EndAgents`). `Exit == "relaunch"` → `restartState.SupervisedRestart = true` so `RunAsync` returns `ExitCodes.RestartRequested`; `"final"` → leave it false (exit 0). Write `LocalFrame.ShutdownAck("stopping")`, then `lifetime.StopApplication()`.
- `LocalControlCapabilities.Current` gains `"shutdown/1"`. `LocalControlServer` routes `FrameType.Shutdown` to the handler. The `RestartState` type that `DaemonRunner` reads at `:837` is exposed to the handler through DI (builder: find how `restartState` reaches `RunAsync` and register the same instance).

- [ ] **Step 1: Write the failing tests** modelled on `LocalControlProbeTests.cs`: `Shutdown_keep_relaunch_sets_intent_and_restart_and_acks`, `Shutdown_end_final_sets_intent_and_exits_zero` (`SupervisedRestart` false, `StopApplication` called on a `StubHostLifetime`), `Malformed_shutdown_answers_an_error`, and in `LocalControlCapabilitiesTests`: `Current_advertises_shutdown_v1`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS; run `LocalControlHelloTests` for the capability list.
- [ ] **Step 5: Commit** `Accept a shutdown intent over the control socket`.

### Task 8: Resume on boot (Daemon)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs`, `AgentOrchestrator.cs:2118-2330` (factor the post-consent body), `:891` (wire `GetResumingAgents`), `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs:119,658-687`, `src/Capacitor.Cli.Core/Models.cs:2183+` (`DaemonConnect` and `DaemonStatusReport` trailing field), `src/Capacitor.Cli.Daemon/DaemonConfig.cs`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs:177-180,795-816`, `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs:29-143`, `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/ResumeNudge.cs`, `src/Capacitor.Cli.Core/Models/ResumingAgentClaim.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorResumeOnBootTests.cs`, `ResumeNudgeTests.cs`; extend `ServerConnection` connect tests (the class that pins `BuildDaemonConnect`)

**Interfaces:**
- Consumes: Tasks 1, 3, 4, 5.
- Produces:
  ```csharp
  // Core
  public sealed record ResumingAgentClaim(string AgentId, string PredecessorInstanceId, string? SessionId);
  // DaemonConnect, DaemonStatusReport: trailing  ResumingAgentClaim[]? ResumingAgents = null
  // DaemonConfig
  public int      ResumeConcurrency { get; set; } = 2;                            // KCAP_RESUME_CONCURRENCY
  public TimeSpan ResumeMaxAge      { get; set; } = TimeSpan.FromHours(24);        // KCAP_RESUME_MAX_AGE_SECONDS
  public bool     AutoResume        { get; set; } = true;                          // DaemonSettings.AutoResume (Task 16)
  // IHostedAgentRuntimeFactory / IHostedAgentLauncher
  bool SupportsRestartResume(string? runtimeTransport) => false;                   // PtyHostedAgentRuntimeFactory delegates to launcher.SupportsResume
  bool SupportsResume => false;                                                    // IHostedAgentLauncher
  // ResumeNudge
  internal static class ResumeNudge { public static string Build(DateTimeOffset interruptedAt, string? originalPrompt); }
  // AgentOrchestrator
  internal void LoadResumeLedger();                                                // before ConnectAsync: classify entries, fill _resumeCandidates
  internal ResumingAgentClaim[] ResumingClaims();                                  // GetResumingAgents source; private agents excluded
  internal IReadOnlyList<string> LedgerWorktreePaths();                            // CleanupOrphanedAsync keep-set
  internal Task ResumeOnBootAsync(CancellationToken ct);                           // the lane
  internal Task<ResumeOutcome> ResumeEntryAsync(ResumeLedgerEntry entry, CancellationToken ct);
  internal enum ResumeOutcome { Resumed, Continued, Interrupted }
  async Task<CommandOutcome> LaunchAdmittedAsync(LaunchAgentCommand cmd, string agentId, string? resumeSessionId, LaunchConsentOutcome consent, CancellationToken ct); // factored from HandleAdmittedLaunchAsync after the consent decision
  ```
- `LoadResumeLedger` runs after `ReapOrphansOnceAsync`. Per entry: `State Interrupted` → skip; `!AutoResume` → `MarkInterrupted("auto_resume_off")`; last-alive (`max(ResumingAt, DaemonHeartbeat.LastBeat)`) older than `ResumeMaxAge` → `too_old`; the entry's agent id listed in `OrphanReaper.BlockedCandidates` (`src/Capacitor.Cli.Daemon/Services/OrphanReaper.cs:71-92`, the reaper's unresolvable prior-incarnation records) → `predecessor_process_unresolved`; `!Directory.Exists(Launch.WorktreePath)` → `worktree_missing`. The rest become candidates and `ResumingClaims()`.
- Identity in this plan: a native resume always registers under the existing agent id. The new-id branch of spec §3.6 for a native resume needs the server to answer `AgentRegistered` with its outcome, which the server plan adds; until then the daemon treats the same-id registration as accepted. Kcap continue (Task 12) always uses a new id and needs no answer.
- `ResumeOnBootAsync`, after connect and worktree cleanup: `SemaphoreSlim(config.ResumeConcurrency)`; for each candidate `ResumeEntryAsync`. Native path when the vendor's factory `SupportsRestartResume(entry.Transport)` and `VendorSession` is set: `LaunchAdmittedAsync` with the descriptor rebuilt into a `LaunchAgentCommand`, `agentId = entry.AgentId`, `resumeSessionId = entry.VendorSession`, `Prompt = ResumeNudge.Build(...)`, consent `new LaunchConsentOutcome(true, "resume", entry.Launch.ConsentSource ?? "")`, and one `ConsentDecisionRecord` with `Decision "allowed"`, `Source "resume"`. Replay the ring (Task 5). Failure (`CommandOutcome` not accepted, or the runtime exits before the handshake) → `Attempts++`, `LastError`, then Task 12's continue path; continue failing → `MarkInterrupted(reason)` and `_server.ReportAgentEndedForShutdownAsync(entry.AgentId, entry.SessionId, "Failed", $"resume_failed:{reason}", exitCode, ct)`.
- `DaemonRunner`: `orchestrator.LoadResumeLedger()` after `:795`; `CleanupOrphanedAsync(orchestrator.LedgerWorktreePaths())` at `:816`; `_ = orchestrator.ResumeOnBootAsync(lifetime.ApplicationStopping)` after it. Env knobs parsed beside `:135`.

- [ ] **Step 1: Write the failing tests** (harness with a fake runtime factory via `extraRuntimeFactories` whose `SupportsRestartResume` returns true and records `ctx.ResumeSessionId`/`ctx.Prompt`; a `TempDaemonStore` with pre-written ledger entries):
  - `A_resuming_entry_relaunches_under_its_agent_id_with_the_vendor_session_and_nudge`.
  - `Claims_list_non_private_candidates_only` (`ResumingClaims()` excludes `IsPrivate`).
  - `Entries_older_than_the_age_cap_become_interrupted_too_old` (heartbeat file mtime two days back, no `ResumingAt`).
  - `Auto_resume_off_interrupts_everything_and_launches_nothing`.
  - `A_missing_worktree_interrupts_with_worktree_missing` (Review Focus 1).
  - `The_lane_runs_at_most_ResumeConcurrency_launches_at_once` (factory blocks on a gate; assert concurrency 2 with 4 entries).
  - `A_runtime_that_exits_before_handshake_tries_continue_once_then_interrupts_and_reports_resume_failed` (Review Focus 5; continue stubbed to fail; `CaptureServerConnection` sees `Failed` with `resume_failed:runtime_exited`).
  - `Worktree_keep_set_lists_candidate_worktrees`.
  - `ResumeNudgeTests.Nudge_names_the_interruption_and_asks_to_verify_before_repeating` (contains "interrupted", "daemon restart", "working tree", "do not repeat").
  - `BuildDaemonConnect_carries_resuming_claims` in the existing `ServerConnection` connect test class.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** Factor `HandleAdmittedLaunchAsync` so the consent decision is the only thing above the new `LaunchAdmittedAsync`; the fence admission and `MaxConcurrentAgents` check (`:2305`) stay inside it. `GetResumingAgents` is wired at `:891` like `GetLiveAgents`.
- [ ] **Step 4: Run** the new classes, `AgentOrchestratorResumeLedgerTests`, `ShutdownAgentReportTests`, the connect tests → PASS.
- [ ] **Step 5: Commit** `Resume ledgered agents when the daemon boots`.

### Task 9: Claude resume and session pinning (Daemon)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Claude/ClaudeLauncher.cs:109-192`, `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs:96` (`LauncherContext` gains `string? PinnedSessionId`, `string? ResumeSessionId`), `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntimeFactory.cs` (pass both through, delegate `SupportsRestartResume`), `AgentOrchestrator.Resume.cs` (mint the pin)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Claude/ClaudeLauncherResumeTests.cs`

**Interfaces:**
- Consumes: Task 8.
- Produces: `ClaudeLauncher.SupportsResume => true`. `BuildArgs` emits `--session-id <PinnedSessionId>` on a fresh launch when set, and `--resume <ResumeSessionId>` (no `--session-id`) on a resume; the trailing prompt stays `-- <Prompt>`. The orchestrator mints `Guid.NewGuid().ToString()` for vendor `claude` at admission, stores it as `agent.VendorSession`, sets `agent.SessionId` to the same value, and writes it to the ledger.

- [ ] **Step 1: Write the failing tests** modelled on `ClaudeLauncherPermissionModeTests.cs:34`: `Fresh_launch_pins_the_session_id`, `Resume_launch_passes_resume_and_no_session_id`, `Resume_keeps_the_other_flags_in_order` (byte-exact argv), and in `AgentOrchestratorResumeLedgerTests`: `A_claude_launch_writes_the_pinned_id_as_vendor_and_kcap_session`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** plus `ClaudeLauncherPermissionModeTests` and `ClaudeLauncherReviewFlowTests` → PASS.
- [ ] **Step 5: Commit** `Pin and resume Claude sessions by id`.

### Task 10: Codex resume on both routes (Daemon)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs:113-121`, `src/Capacitor.Cli.Daemon/Harness/Codex/CodexLauncher.cs:149`
- Test: extend `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexLauncherTests.cs`; new test in the app-server launch args class

**Interfaces:**
- Consumes: Task 8.
- Produces: `CodexHostedAgentRuntimeFactory.SupportsRestartResume(_) => true`; the PTY-route `throw` at `:117-119` is removed. `CodexLauncher.BuildArgs` with `ResumeSessionId` set emits `resume <id>` first, keeps only the `-c key=value` overrides it would otherwise emit (model and posture ride `-c`), and the prompt last. The app-server route keeps `thread/resume`. The orchestrator stores the thread id discovered by `CodexSessionRolloutLocator` as `VendorSession` (same place it sets `SessionId`, `:5259`).

- [ ] **Step 1: Write the failing tests**: `Resume_launch_uses_the_resume_subcommand_with_overrides_and_prompt` (byte-exact), `Resume_on_the_pty_route_no_longer_throws` (factory with `CodexAppServerActive = false` and `ResumeSessionId` set starts the PTY launcher), `App_server_resume_still_sends_thread_resume` (via `FakeCodexAppServer`).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** Builder verifies against `codex resume --help` which top-level flags the subcommand accepts and keeps only those; the test pins the chosen shape.
- [ ] **Step 4: Run** the Codex test classes → PASS.
- [ ] **Step 5: Commit** `Resume Codex sessions on the PTY route too`.

### Task 11: Pi, Muse and Antigravity resume (Daemon)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Pi/PiRpcHostedAgentRuntimeFactory.cs:403-424`, `src/Capacitor.Cli.Daemon/Harness/Pi/PiRpcHostedAgentRuntime.cs:492-506`, `src/Capacitor.Cli.Daemon/Harness/Muse/MuseHostedAgentRuntimeFactory.cs:276-290`, `src/Capacitor.Cli.Daemon/Harness/Antigravity/AntigravityHostedAgentRuntimeFactory.cs:543-548`, `AntigravityHostedAgentRuntime.cs:899-910`
- Test: extend `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/PiHostedLaunchTests.cs`, the Muse factory tests, the Antigravity runtime tests

**Interfaces:**
- Consumes: Task 8.
- Produces: `SupportsRestartResume` true on the Pi, Muse and Antigravity factories (Muse only when `!ctx.IsReviewFlow`). Pi `BuildPsi` appends `"--session", ctx.ResumeSessionId` after `--mode rpc` when set; `PiRpcHostedAgentRuntime.ApplyState` also reads `sessionFile` into `internal string? SessionFile`. Muse passes `ResumeSessionId` through its existing `SessionResume` path unchanged. Antigravity seeds `_conversationId` from `ctx.ResumeSessionId` so turn 1 runs with `--conversation`.

- [ ] **Step 1: Write the failing tests**: Pi `BuildPsi_with_resume_appends_session_after_mode_rpc` (byte-exact, next to `BuildPsi_NoModel_ArgvIsExactlyModeRpc`), `ApplyState_records_the_session_file`; Muse `Interactive_resume_is_supported_and_review_flow_is_not`; Antigravity `A_seeded_conversation_id_is_passed_on_the_first_turn`.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the three vendors' classes, including `Reviewer_argv_is_byte_exact` → PASS.
- [ ] **Step 5: Commit** `Resume Pi, Muse and Antigravity sessions after a restart`.

### Task 12: Kcap continue fallback (Core and Daemon)

**Files:**
- Move: `src/Capacitor.Cli/Continuation/{SessionTakeover,TakeoverReport,TakeoverResult,DeclaredPlanState}.cs` → `src/Capacitor.Cli.Core/Continuation/` (namespace `Capacitor.Cli.Core.Continuation`, `public`)
- Create: `src/Capacitor.Cli.Core/Continuation/ISessionLivenessSource.cs`; move `SessionLiveness` to Core if it is not there
- Modify: `src/Capacitor.Cli/AgentSessions.cs` (implements `ISessionLivenessSource`), callers in `src/Capacitor.Cli/Commands/RecapCommand*.cs` and `McpHandoffServer.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/ProvenExitedLiveness.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs`
- Test: move `test/Capacitor.Cli.Tests.Unit/Continuation/*` to `test/Capacitor.Cli.Core.Tests.Unit/Continuation/`; new `AgentOrchestratorContinueTests.cs`

**Interfaces:**
- Consumes: Task 8.
- Produces:
  ```csharp
  public interface ISessionLivenessSource { void Reap(); SessionLiveness Liveness(SessionId session); }
  public sealed class SessionTakeover(ISessionLivenessSource local, TimeProvider time) { /* unchanged RunAsync */ }
  internal sealed class ProvenExitedLiveness : ISessionLivenessSource { Reap() {} Liveness(_) => SessionLiveness.Exited; }
  // AgentOrchestrator
  internal async Task<ResumeOutcome> ContinueEntryAsync(ResumeLedgerEntry entry, string reason, CancellationToken ct);
  ```
- `ContinueEntryAsync`: `_server.ReportAgentEndedForShutdownAsync(entry.AgentId, entry.SessionId, "Completed", "superseded", null, ct)`; mint a new agent id; prompt = original prompt, one line "This session continues <old session id>, which was interrupted (<reason>).", the summary text from `GET /api/sessions/{oldSessionId}/summary` through the daemon's authenticated client when it answers within 10 s, and the nudge; `LaunchAdmittedAsync` with no resume id; after the new `SessionId` is learned, `new SessionTakeover(new ProvenExitedLiveness(), time).RunAsync(client, baseUrl, previous: entry.SessionId, current: newSessionId, force: false, ct)`; delete the old ledger entry.

- [ ] **Step 1: Move the Continuation types and tests first** (mechanical; keep behaviour). Run `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` → green, then the moved tests → PASS. Commit `Move session takeover into Core`.
- [ ] **Step 2: Write the failing tests** in `AgentOrchestratorContinueTests`: `Continue_ends_the_old_pair_as_superseded_then_launches_a_new_agent_id` (`CaptureServerConnection` sees `Completed`/`superseded` for the old id; the fake factory sees a new agent id, no `ResumeSessionId`, a prompt containing the old session id); `Continue_attaches_the_new_session_to_the_old_work_items` (WireMock server answering the work-items and plans routes as `McpWorkItemsServerTests` do; assert the declare POSTs); `Continue_is_used_when_the_vendor_declares_no_resume_support`.
- [ ] **Step 3: Run** → FAIL. **Step 4: Implement.** **Step 5: Run** → PASS.
- [ ] **Step 6: Commit** `Fall back to kcap continue when a resume cannot run`.

### Task 13: Agent-resume IPC (Daemon)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`, `LocalControlCapabilities.cs`, `AgentOrchestrator.Resume.cs`
- Test: extend `LocalControlShutdownTests.cs` (or a sibling `LocalControlAgentResumeTests.cs`)

**Interfaces:**
- Consumes: Tasks 2, 8.
- Produces: `internal Task<string> ResumeInterruptedAsync(string agentId, CancellationToken ct)` returning `"resuming"` after scheduling `ResumeEntryAsync` for an `Interrupted` entry (state flipped to `Resuming`, `Attempts` kept), `"not_interrupted"` for any other state, `"unknown"` when absent. Capability `"resume/1"`; `FrameType.AgentResume` routes to it and answers `LocalFrame.AgentResumeAck(status)`.

- [ ] **Step 1: Write the failing tests**: `AgentResume_on_an_interrupted_entry_acks_resuming_and_schedules_the_lane`, `AgentResume_on_a_running_entry_acks_not_interrupted`, `AgentResume_on_an_unknown_id_acks_unknown`, `Current_advertises_resume_v1`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Retry an interrupted agent over the control socket`.

### Task 14: CLI stop, restart, refresh and status (CLI)

**Files:**
- Create: `src/Capacitor.Cli/Commands/DaemonShutdownClient.cs`
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs:357-500` (stop), `:563-628` (restart text), status rendering, `:1043` (usage)
- Modify: `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs:159-249` (`stop --keep-agents`, refresh), `src/Capacitor.Cli/Services/LaunchdServiceManager.cs:174-235`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/DaemonCommandsStopTests.cs`; extend `DaemonCommandsServiceRefreshTests.cs`, `DaemonRestartCommandTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2.
- Produces:
  ```csharp
  static class DaemonShutdownClient {
      public static Task<LocalFrame?> RequestAsync(DaemonStore store, string name, ShutdownRequestDto request, CancellationToken ct); // mirrors DaemonRestartClient.RequestAsync
      public static Task<bool> StopAndWaitAsync(DaemonStore store, string name, ShutdownRequestDto request, TimeSpan exitWait, TimeProvider time, CancellationToken ct); // ack received and the pid file's process gone within exitWait
  }
  ```
- `kcap daemon stop [--keep-agents]`: `StopAndWaitAsync(…, new("keep"|"end", "final"), TimeSpan.FromSeconds(30))`; only when it returns false does the existing kill path run, and that path then calls `ResumeLedgerStore.For(store, name).MarkInterrupted("stopped_by_operator")`. Same for `service stop`, before `bootout`.
- `LaunchdServiceManager.RefreshUnit`'s `Func<bool> requestRestart` is renamed `stopForReload` and `DaemonServiceCommands.RequestRestart` becomes `StopForReload(serviceId)` = `StopAndWaitAsync(…, new("keep", "final"), exitWait: 30 s)`; `ReloadBudget` grows by that wait. The `bootout` then unloads an already-exited job.
- `kcap daemon restart` keeps its modes; its output for `restarting` reads `restart accepted; agents are stopped and resumed on the new daemon`.
- `kcap daemon status --name N` prints `resuming: <n>  interrupted: <n> (<reasons>)` from `ResumeLedgerStore.For(store, N).ReadAll()`.

- [ ] **Step 1: Write the failing tests** (console via `ConsoleOutput.StartCapture()`, bare `[NotInParallel]`, a fake socket server as `DaemonRestartCommandTests` uses): `Stop_sends_end_agents_final_and_does_not_kill_when_the_daemon_exits`, `Stop_keep_agents_sends_keep`, `Stop_falls_back_to_kill_and_marks_the_ledger_interrupted_when_nothing_answers`, `Stop_falls_back_when_the_daemon_acks_but_never_exits` (Review Focus 4; the fake acks and keeps its pid file alive), `Refresh_waits_for_the_exit_before_bootout` (spy `stopForReload` order against the recorded `launchctl` calls), `Status_lists_resuming_and_interrupted_counts`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** the three classes → PASS.
- [ ] **Step 5: Commit** `Stop and reload the daemon through a shutdown intent`.

### Task 15: Hook and watcher suppression (CLI)

**Files:**
- Create: `src/Capacitor.Cli/ResumeEndSuppression.cs`
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs` (before the session-end handoff spawn and before the post at `:359`), `src/Capacitor.Cli/Commands/WatchCommand.cs:1221` (guard the parent-exit post)
- Test: `test/Capacitor.Cli.Tests.Unit/ResumeEndSuppressionTests.cs`; extend `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookCommandTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `static class ResumeEndSuppression { public static bool ShouldSuppress(HostedAgent hosted, Func<string, ResumeLedgerEntry?> lookup); }` → true only when `hosted.AgentId` is set, `lookup` returns an entry and its `State == Resuming`; any exception from `lookup` reads as false. Production lookup is `id => ResumeLedgerStore.FindByAgentId(DaemonStore.FromEnvironment(), id)`.
- In the Claude hook, a suppressed `session-end` returns 0 before `ClaudeSessionEndHandoff.TrySpawn` and before any post or spool. In the watcher, a suppressed parent-exit skips `PostSessionEndOnParentExitAsync` and logs `session end suppressed: agent <id> is resuming`.

- [ ] **Step 1: Write the failing tests**: `Suppresses_only_a_resuming_entry` (Running → false, Resuming → true, null → false, no agent id → false), `An_unreadable_ledger_fails_open` (Review Focus 3: lookup throws → false), and in the hook tests: `Session_end_for_a_resuming_hosted_agent_posts_nothing` (set `KCAP_AGENT_ID` with `EnvScope.Exclusive`, write a `Resuming` entry under a `TempDaemonStore` pinned as the daemons dir, WireMock receives no request).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `Hold session ends while an agent resumes`.

### Task 16: Agent verbs, config key, copy and docs (CLI)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/AgentCommand.cs:24,56-59,304-410` (`ls` eighth column `state`, new `resume <id>`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`AgentList` rows gain the column and ledger-only `Interrupted` rows), `src/Capacitor.Cli.Core/Config/AppConfig.cs:30-42` (`[JsonPropertyName("auto_resume")] public bool? AutoResume { get; init; }`), `src/Capacitor.Cli/Commands/ConfigCommand.cs:192-197,240-245,253`, `src/Capacitor.Cli/Commands/ProfileCommand.cs:141`, `src/Capacitor.Cli.Daemon/DaemonRunner.cs:177-180` (`config.AutoResume = profileDaemon?.AutoResume ?? true`), `src/Capacitor.Cli/Commands/UpdateCommand.cs:171-173`
- Docs: `README.md:1032-1034,1056` and the `### Local agents` section at `:2099`; `src/Capacitor.Cli.Core/Resources/help-daemon.txt:7-8,29-44`, `help-agent.txt`, `help-update.txt:26-27`
- Test: extend `AgentCommandRoutingTests.cs`, `ConfigCommandTests.cs`; a daemon test for the eighth column in the `AgentList` handler's test class

**Interfaces:**
- Consumes: Tasks 1, 13.
- Produces: `kcap agent ls` renders `STATE` as `running`, `resuming` or `interrupted (<reason>)`; `kcap agent resume <id>` sends `FrameType.AgentResume` and prints the ack. The row validator at `AgentCommand.cs:398-402` accepts 8 columns. `kcap config set daemon.auto_resume true|false` and `unset`. The update notice on macOS and Linux reads `A running daemon restarts on the new version; its agents are stopped and resumed.`
- `kcap agent attach <id>` (`AgentCommand.cs:98`) reconnects when the socket drops: it retries the attach by agent id every second for up to `KCAP_ATTACH_RECONNECT_SECONDS` (default 90, covering the stop, the relaunch and the resume lane), prints `daemon restarting; waiting for <id> to resume…` once, and when the ledger entry for the id is `interrupted` it prints the reason and exits 1. The first frame after reconnecting is the replay that already carries the restart marker.

- [ ] **Step 1: Write the failing tests**: `Ls_accepts_eight_columns_and_shows_state`, `Resume_routes_to_the_agent_resume_frame`, `ApplySet_accepts_daemon_auto_resume_booleans_only`, `Interrupted_ledger_entries_appear_in_the_agent_list`, `Attach_reconnects_after_a_dropped_socket_and_replays` (fake socket server that closes then accepts; modelled on `AgentAttachClientTests.cs`), `Attach_exits_with_the_reason_when_the_entry_is_interrupted`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement**, then edit the docs: README restart lines describe the new default, `--when-idle` and `--force`; the update paragraph at `:1056` drops "once it is idle" and names the resume; `help-daemon.txt` lines 7-8 and 36-44 likewise; `help-agent.txt` adds `resume <agent-id>` and the `STATE` column; `help-update.txt:26-27` drops the manual-restart sentence for macOS and Linux.
- [ ] **Step 4: Run** the CLI unit suite and the daemon class → PASS. Run the AOT publish grep from Global Constraints → no output.
- [ ] **Step 5: Commit** `Show and retry interrupted agents, and document the restart`.
