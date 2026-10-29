# Flow participant restart resume (PR 1: machinery + Claude) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Claude review-flow reviewer (owned worktree) survives a keep-agents daemon restart under its own agent id with its session context, and every shared mechanism the design needs is in place for PR 2 (Cursor, OpenCode).

**Architecture:** Participant entries go into a `participants/` subdirectory of the resume ledger, invisible to older daemons. The orchestrator stops them for resume on a keep-agents shutdown, and resumes them on boot through the existing native-resume path with a reviewer nudge, held input, seeded clocks and a bounded startup-completeness hold. Session ends are fenced against resume by a compare-and-set on the ledger entry under a per-entry file lock.

**Tech Stack:** .NET 10, NativeAOT, TUnit, System.Text.Json source generation.

**Spec:** `docs/superpowers/specs/2026-10-22-flow-participant-restart-resume-design.md`

## Global Constraints

- Claim lifetime: a participant entry is eligible only within 15 minutes of its `ResumingAt`.
- Recovery deadline: 90 s from daemon start for every participant resume attempt.
- Eligibility also requires `entry.PredecessorInstanceId == DaemonLock.PriorInstanceId`; a null prior instance fails closed.
- No new `StartupCandidateUnresolvedReason` value; no wire change to the server.
- Participants never take kcap continue, never become `Interrupted` entries, never `kcap agent resume`.
- Codex app-server reviewers keep `ParkForShutdownAsync`; nothing about the park path changes.
- `SupportsReviewerRestartResume` is true for no vendor until Task 10's probe passes, and then only for Claude with `WorkLocation.OwnedWorktree`.
- Follow CLAUDE.md test conventions (`TempDir`, `[NotInParallel]` rules, `EnvScope`) and comment rules; AOT-clean (`dotnet publish -c Release` shows no IL2026/IL3050).
- Failure end reason format: `resume_failed:<reason>`, via the existing `ReportResumeFailedAsync`.

## Review Focus

- A ledger whose `participants/` directory is unreadable or holds a corrupt file: interactive resume still works, the participant is treated as failed, nothing throws out of boot. (Task 5)
- A session end for an agent with no ledger entry at all (a plain terminal session, a private agent): posts exactly as today, no lock file created. (Task 2)
- An end-agents shutdown (`kcap daemon stop` without `--keep-agents`) while participant resumes are queued: none publishes afterwards and their entries are removed. (Task 7)
- A daemon started with `daemon.auto_resume false`: participant entries are reported failed at boot, not left on disk. (Task 5)
- A reviewer whose vendor session id was never learned before the stop: not marked for resume, ended as today. (Task 4)

---

### Task 1: Ledger model, participant store and the entry lock

**Files:**
- Create: `src/Capacitor.Cli.Core/Resume/ParticipantResume.cs`
- Create: `src/Capacitor.Cli.Core/Resume/ResumeEntryLock.cs`
- Modify: `src/Capacitor.Cli.Core/Resume/ResumeLedgerEntry.cs`, `src/Capacitor.Cli.Core/Resume/ResumeLedgerStore.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Resume/ResumeLedgerStoreTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Resume/ResumeEntryLockTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ParticipantResume(LaunchKind Kind, string[]? McpAllowlist, string[]? Tools, ReviewerCertificationRequirement? Certification, ulong ActivitySeq, ulong AgeMs, string? LastRoundInput, DateTimeOffset? LastRoundAt)` (registered in `ResumeLedgerJsonContext`).
  - New trailing optional members on `ResumeLedgerEntry`: `ParticipantResume? Participant = null`, `string? RunEpoch = null`, `string? LaunchEpoch = null`, `DateTimeOffset? EndCommittedAt = null`, `string? EndCommittedEpoch = null`. Inserted before `StartedAt` per the house rule for new parameters.
  - `ResumeLedgerStore.ParticipantsFor(DaemonStore store, string daemonName, TimeProvider? time = null)` returning a store rooted at `<resume>/participants`.
  - `ResumeLedgerStore.FindByAgentId` also looks in each daemon's `resume/participants`.
  - `public sealed class ResumeEntryLock : IDisposable` with `public static ResumeEntryLock? TryAcquire(string entryPath, TimeSpan wait)`: an exclusive `FileStream` on `<entryPath>.lock` (`FileShare.None`, owner-only create mode), retried every 20 ms until `wait`. Null on timeout.
  - `ResumeLedgerStore.TryUpdateUnderLock(string agentId, Func<ResumeLedgerEntry, ResumeLedgerEntry?> change, TimeSpan wait)` returns `LockedUpdate` (`enum LockedUpdate { NoEntry, Unchanged, Written, LockTimeout }`): reads, applies `change` (null means leave it), writes, all under the lock. `Delete` also removes the `.lock` file.

- [ ] **Step 1: Write the failing tests**
  - `ParticipantEntriesAreInvisibleToTheInteractiveStore`: write through `ParticipantsFor`, assert `For(...).ReadAll()` is empty and `ParticipantsFor(...).ReadAll()` has one entry.
  - `FindByAgentIdSeesAParticipantEntry`.
  - `ParticipantFieldsRoundTrip`: every `ParticipantResume` field and the four new top-level fields survive `Write`/`TryRead`.
  - `AnEntryWrittenWithoutTheNewFieldsReadsThemAsNull`: a JSON file with none of the new fields.
  - `TryUpdateUnderLockTimesOutWhileAnotherHolderHasIt`: hold `ResumeEntryLock.TryAcquire` and expect `LockTimeout` with a 50 ms wait.
  - `TryUpdateUnderLockReturnsNoEntryForAMissingAgent` and `UnchangedWhenTheChangeReturnsNull`.
- [ ] **Step 2: Run** `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ResumeLedgerStoreTests/*"`; expect compile failures.
- [ ] **Step 3: Implement** the types and members above.
- [ ] **Step 4: Run** both test classes; expect PASS.
- [ ] **Step 5: Commit** `Add participant entries and an entry lock to the resume ledger`.

### Task 2: Session-end fence

**Files:**
- Create: `src/Capacitor.Cli/SessionEndFence.cs` (replaces `src/Capacitor.Cli/ResumeEndSuppression.cs`, deleted)
- Modify: `src/Capacitor.Cli.Core/HostedAgent.cs` (add `public const string DaemonEpochVar = "KCAP_DAEMON_EPOCH"` and `string? DaemonEpoch { get; init; }` read in `FromEnvironment`), `src/Capacitor.Cli/ClaimOrigin.cs` (add `string? AgentId = null, string? DaemonEpoch = null`), `src/Capacitor.Cli/WatcherManager.cs:350`, `src/Capacitor.Cli/AgentSessions.cs` (persist and read the two new origin fields), `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs:259,540`, `src/Capacitor.Cli/Program.cs:150`, `src/Capacitor.Cli/Commands/WatchCommand.cs:153,1271`, `src/Capacitor.Cli/Commands/AgentHookPoster.cs` (`SpoolEnds`)
- Test: `test/Capacitor.Cli.Tests.Unit/SessionEndFenceTests.cs` (replaces `ResumeEndSuppressionTests.cs`)

**Interfaces:**
- Consumes: Task 1's `TryUpdateUnderLock`, `FindByAgentId`.
- Produces: `static class SessionEndFence` with `public static bool ShouldHold(string? agentId, string? endingEpoch, Func<string, (ResumeLedgerStore Store, ResumeLedgerEntry Entry)?> locate, TimeProvider time)` and an overload `ShouldHold(HostedAgent hosted)` over `DaemonStore.FromEnvironment()`. Decision, under the entry lock:
  - no agent id, or no entry in either store: `false`, no lock taken;
  - entry `Resuming`: hold;
  - entry has `RunEpoch` and `endingEpoch` differs: hold. A null `endingEpoch` holds only when `RunEpoch != LaunchEpoch` (the run has been resumed);
  - otherwise stamp `EndCommittedAt = now, EndCommittedEpoch = endingEpoch` (only if not already stamped) and return `false`;
  - lock timeout or any exception: `false`. A broken ledger never keeps a real end from posting.
- Every former `ResumeEndSuppression.ShouldSuppress` call site calls `ShouldHold` before it posts or spools. `SpoolEnds` passes the origin's `AgentId`/`DaemonEpoch`.

- [ ] **Step 1: Write the failing tests** (use a `locate` delegate over a `TempDir` store, never the real home):
  - `NoEntryPostsAndLeavesNoLockFile`.
  - `ResumingEntryHolds`.
  - `RunningEntryWithMatchingEpochPostsAndStampsTheEntry`: assert `EndCommittedAt` and `EndCommittedEpoch` are set.
  - `RunningEntryWithADifferentEpochHolds` (the predecessor end after publication).
  - `NullEpochHoldsOnlyAfterAResume`: `RunEpoch == LaunchEpoch` posts; `RunEpoch != LaunchEpoch` holds.
  - `LockTimeoutPosts`.
  - `SpoolEndsSkipsAHeldSession` in `AgentHookPosterTests` (or its existing spool-ends test class): an origin whose entry is `Resuming` spools nothing.
- [ ] **Step 2: Run** the Cli unit suite filtered to these classes; expect failures.
- [ ] **Step 3: Implement** and switch the call sites.
- [ ] **Step 4: Run** the filtered tests, then the whole `test/Capacitor.Cli.Tests.Unit` project; expect PASS.
- [ ] **Step 5: Commit** `Fence session ends against a resume of the same run`.

### Task 3: Daemon side of the fence

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (`TryMarkResuming`, `NewRunningEntry`, `WriteResumedLedger`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorStopForResumeTests.cs`

**Interfaces:**
- Consumes: Task 1's `TryUpdateUnderLock`.
- Produces: `TryMarkResuming` marks `Resuming` through `TryUpdateUnderLock` and refuses (returns false, agent not resume-pending) when the entry carries `EndCommittedAt`. `NewRunningEntry` sets `LaunchEpoch = RunEpoch = _daemonEpoch`; `WriteResumedLedger` sets `RunEpoch = _daemonEpoch` and clears `EndCommittedAt`/`EndCommittedEpoch`. Applies to interactive agents too.

- [ ] **Step 1: Write the failing tests:** `AStampedEndKeepsTheAgentFromBeingMarkedResuming` (entry stamped, `PrepareForResumeAsync` leaves it `Running` and the agent not `ResumePending`); `APublishedResumeRecordsItsOwnEpochAndNoStamp`.
- [ ] **Step 2: Run** `--treenode-filter "/*/*/AgentOrchestratorStopForResumeTests/*"`; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** that class and `AgentOrchestratorResumeLedgerTests`; expect PASS.
- [ ] **Step 5: Commit** `Refuse a resume of a run whose end was already committed`.

### Task 4: Participant entries and the stop for resume

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` (new `bool SupportsReviewerRestartResume(string? runtimeTransport, WorkLocation work) => false;`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs`, `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`DeliverInputAsync` records the last round input; the `AgentInstance` keeps the launch command's `McpAllowlist`, `Tools` and `ReviewerCertification`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorParticipantLedgerTests.cs` (new)

**Interfaces:**
- Consumes: Task 1, Task 3.
- Produces:
  - `internal ResumeLedgerStore? ParticipantLedger { get; }`, built beside `ResumeLedger`.
  - `bool IsLedgered(AgentInstance a)`: interactive, or `Kind == ReviewFlow && !IsPrivate && factory.SupportsReviewerRestartResume(a.RuntimeTransport, a.Work)`. `LedgerFor(AgentInstance a)` picks the store. Every ledger read/write/delete in `AgentOrchestrator.Resume.cs` and `CleanupAgentAsync` goes through `LedgerFor`. A participant's pre-start entry is written once the runtime exists (no pre-start entry: its transport is not known before the start).
  - The participant entry's `Participant` holds `Kind`, `McpAllowlist`, `Tools`, `Certification` with `ExpectedDaemonConnectionId` and `ExpectedCliVersion` blanked, and `LastRoundInput`/`LastRoundAt`, rewritten on each successful `DeliverInputAsync`. `TryMarkResuming` adds `ActivitySeq` and `AgeMs` from the clock snapshot.
  - `StopAgentsForShutdownAsync`: `IsResumableReviewer` (park) first, then `IsLedgered` → `PrepareForShutdownResumeAsync`, else terminate. An agent with no `VendorSession` is not marked (`TryMarkResuming` false).
  - `FlowParticipantsMidRound` still counts ledgered participants, so the restart's round wait is unchanged.

- [ ] **Step 1: Write the failing tests** with a fake runtime factory whose `SupportsReviewerRestartResume` returns true:
  - `AResumableParticipantGetsAnEntryInTheParticipantStore` (and none in the interactive store).
  - `AParticipantOfAVendorThatCannotResumeGetsNoEntry`.
  - `DeliveredInputIsRecordedAsTheLastRoundInput`.
  - `KeepAgentsShutdownMarksAParticipantResumingWithItsClock`: entry `Resuming`, `ActivitySeq`/`AgeMs` set, worktree kept.
  - `AParticipantWithNoVendorSessionIsEndedNotMarked`.
  - `ACodexAppServerReviewerStillParks` (existing park test stays green; add one asserting no participant entry).
- [ ] **Step 2: Run** the new class; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** it plus `AgentOrchestratorReviewerParkTests`, `AgentOrchestratorStopForResumeTests`, `AgentOrchestratorResumeLedgerTests`; expect PASS.
- [ ] **Step 5: Commit** `Stop resumable flow participants for resume on a keep-agents shutdown`.

### Task 5: Boot sorting and the participant failure path

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (`LoadResumeLedger`, `SortResumeLedger`, `ResumeEntryAsync`, `RunResumeThroughLaneAsync`), `src/Capacitor.Cli.Daemon/DaemonRunner.cs` or `DaemonConfig` (hand `DaemonLock.PriorInstanceId` to the orchestrator as `DaemonConfig.PriorInstanceId`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorParticipantResumeOnBootTests.cs` (new)

**Interfaces:**
- Consumes: Task 4's `ParticipantLedger`.
- Produces:
  - `static string? ParticipantIneligibility(ResumeLedgerEntry e, string? priorInstanceId, DateTimeOffset now, bool autoResume)`: null when eligible, else one of `auto_resume_off`, `not_planned` (not `Resuming`), `stale` (`now - ResumingAt > 15 min`), `not_predecessor` (prior null or different), `worktree_missing`, `session_missing`.
  - Eligible participant entries go to `_resumeCandidates` and to the front of `_resumeOrder`. Ineligible ones and every later failure go to `FailParticipantAsync(ResumeLedgerEntry entry, string reason)`: reports `resume_failed:<reason>` (skipped for a private entry); if the orphan reaper holds the agent unresolved, rewrites the entry as a tombstone (`State = Interrupted`, `LastError = "tombstone:<reason>"`) and keeps its worktree; otherwise deletes the entry and removes the worktree. `ResumeEntryAsync` sends a participant to `FailParticipantAsync` instead of `ContinueEntry`; `ResumeInterruptedAsync` answers `not_interrupted` for a participant entry.
  - The tombstone is removed, with its worktree, when `BlockedCandidates()` no longer names it (checked from the heartbeat that already drains the quarantine).

- [ ] **Step 1: Write the failing tests:**
  - `ParticipantIneligibility` cases: one `[Arguments]` row per reason plus the eligible row.
  - `AnEligibleParticipantIsQueuedAheadOfInteractiveEntries`.
  - `AnIneligibleParticipantIsReportedFailedAndRemoved`: fake `ServerConnection` records `ReportAgentEndedForShutdownAsync(id, …, "Failed", "resume_failed:not_predecessor", …)`; entry and worktree gone.
  - `AFailedParticipantResumeNeverRunsContinue`: set `ContinueEntry` to a delegate that fails the test.
  - `AnUnresolvedPredecessorLeavesATombstoneWithItsWorktree`, and `TheTombstoneIsClearedOnceResolved`.
  - `AParticipantEntryIsRefusedByAgentResume`.
- [ ] **Step 2: Run** the new class; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** it plus `AgentOrchestratorResumeOnBootTests`; expect PASS.
- [ ] **Step 5: Commit** `Resume only planned participant stops and fail the rest`.

### Task 6: The participant resume launch

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/ReviewerResumeNudge.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (`BuildResumeCommand`, `NativeResumeAsync`), `src/Capacitor.Cli.Daemon/Services/AgentActivityClock.cs`, `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`CreateActivityClock`, `LaunchAdmittedAsync` for a participant resume)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ReviewerResumeNudgeTests.cs`, `AgentOrchestratorParticipantResumeOnBootTests.cs`, `AgentActivityClockTests.cs`

**Interfaces:**
- Consumes: Tasks 4 and 5.
- Produces:
  - `internal static class ReviewerResumeNudge { public static string Build(DateTimeOffset interruptedAt, string? lastRoundInput); }`. The text says: the kcap daemon restarted at `<time>` while you may have been working on a review round; if you already submitted that round's result, do nothing and wait; if `submit_review_result` refuses the round token, the round has ended, so stop; otherwise finish the round and submit it. Then the last round input, under a line `The last round you were sent:`. With no round input, it asks the reviewer to wait for the next round.
  - `AgentActivityClock(TimeProvider time, ulong initialSeq = 1, ulong priorAgeMs = 0)`: `ActivitySeq` starts at `initialSeq`, `AgeMs` adds `priorAgeMs`.
  - For a participant entry, `BuildResumeCommand` sets `Kind`, `McpAllowlist`, `Tools`, `ReviewerCertification` (the recorded one with `ExpectedDaemonConnectionId = _server.CurrentConnectionId`), `Prompt = ReviewerResumeNudge.Build(...)`. `LaunchAdmittedAsync` creates the clock with `initialSeq = ActivitySeq + 1` and `priorAgeMs = AgeMs` when `resume?.Entry.Participant` is set. The existing certification branch then checks vendor, launcher policy and CLI range; the blank `ExpectedCliVersion` skips the swap arm.
  - Every server call and wait in the participant attempt takes the attempt's token (`resume.Token`), registration included.

- [ ] **Step 1: Write the failing tests:**
  - `ReviewerResumeNudge` with and without round input: contains the round text, `submit_review_result`, the time in `u` format.
  - `ClockStartsAboveTheSeededSequenceAndCarriesPriorAge`.
  - `AResumedParticipantLaunchesAsAReviewFlowWithItsRecordedShape`: capture the `RuntimeStartContext` from a fake factory; assert `IsReviewFlow`, `McpAllowlist`, `ResumeSessionId == entry.VendorSession`, `Prompt` starts with the nudge, worktree path equal to the recorded one.
  - `AnOutOfRangeClaudeCliRefusesTheResumeBeforeSpawn`: recorded certification with a range that excludes the probed version; no spawn, `resume_failed:` reported.
  - `TheFirstActivityAfterAResumeIsAboveThePreRestartSequence`: build the status report after publish and compare.
- [ ] **Step 2: Run** these; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** them plus `ResumeNudgeTests` and `StatusReportActivityFieldsTests`; expect PASS.
- [ ] **Step 5: Commit** `Relaunch a participant as the reviewer it was`.

### Task 7: Input and stops while a participant is resuming

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/HeldParticipantInput.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`HandleSendInput`, `HandleStopAgent`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (`TryPublishResumed`, `RunResumeThroughLaneAsync`, `WithdrawRecoveryForEndAgents`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorParticipantInputHoldTests.cs` (new)

**Interfaces:**
- Consumes: Tasks 5 and 6.
- Produces: `sealed class HeldParticipantInput` (per agent id, capacity 8, guarded by `_ledgerLock`) with `bool TryHold(SendInputCommand cmd)`, `IReadOnlyList<SendInputCommand> Release()`, `IReadOnlyList<SendInputCommand> Drop()`.
  - `HandleSendInput`: for an id with no live agent that is a pending participant candidate, hold the input (overflow reports `queue_full`) instead of answering `unknown_agent`.
  - After `TryPublishResumed` and the nudge's delivery, held input is delivered in order through `DeliverInputAsync`. An attempt that ends without publishing reports each held input `unknown_agent`.
  - `HandleStopAgent` of a pending participant candidate withdraws it through `DiscardResumeEntry` (already the path for ledger-only ids); held input is dropped with `unknown_agent`.

- [ ] **Step 1: Write the failing tests:**
  - `InputForAQueuedParticipantIsHeldAndDeliveredAfterPublication` (candidate waiting on a blocked `ResumeLane`).
  - `InputDuringTheHandshakeIsDeliveredAfterTheNudge` (fake runtime records its inputs in order).
  - `InputForAParticipantWhoseResumeFailedIsReportedUnknownAgent`.
  - `TheNinthHeldInputIsReportedQueueFull`.
  - `AServerStopWithdrawsAPendingParticipant` and `AnEndAgentsShutdownWithdrawsQueuedParticipants`: neither publishes later.
- [ ] **Step 2: Run**; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the class and `DeliveryTriggeredStatusReportTests`; expect PASS.
- [ ] **Step 5: Commit** `Hold a resuming participant's input instead of dropping it`.

### Task 8: Startup completeness hold, deadline and the recovery snapshot

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/RecoverySnapshot.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`ComputeStartupReapComplete`, `BuildStatusReport`, wiring at ~930), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (deadline), `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`DaemonConnectCoreAsync`, `BuildDaemonConnect`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorParticipantRecoveryHoldTests.cs` (new), `DaemonStatusReportTests.cs`

**Interfaces:**
- Consumes: Tasks 5–7.
- Produces:
  - `internal sealed record RecoverySnapshot(string[] LiveIds, LiveAgentInfo[] LiveAgents, ResumingAgentClaim[] Claims, bool StartupReapComplete)`; `AgentOrchestrator.TakeRecoverySnapshot()` builds it under `_ledgerLock`. `ServerConnection.GetRecoverySnapshot` (`Func<RecoverySnapshot>?`) is used by connect and status building when wired, replacing the separate `GetLiveAgentIds`/`GetLiveAgents`/`GetResumingAgents`/`GetStartupReapComplete` reads there.
  - `ComputeStartupReapComplete` returns false while any participant candidate has not published and the deadline has not passed, in addition to its existing conditions.
  - `internal TimeSpan ParticipantRecoveryDeadline { get; set; } = TimeSpan.FromSeconds(90);` measured from orchestrator construction with `_time`. At the deadline every unsettled participant attempt is withdrawn (its token cancelled, its runtime terminated, publication fenced by the existing `IsWithdrawn` check) and failed with reason `recovery_deadline`.

- [ ] **Step 1: Write the failing tests** (`FakeTimeProvider`):
  - `CompletenessIsFalseWhileAParticipantIsUnpublished` and `TrueOnceItPublishes`.
  - `TheDeadlineFailsAStalledParticipantAndLiftsTheHold`: completeness then follows the orphan-reaper conditions only.
  - `AStalledParticipantCannotPublishAfterTheDeadline`.
  - `ASnapshotNeverOmitsAParticipantFromBothLiveAndClaimsWhileComplete`: race publication on another thread against `TakeRecoverySnapshot` in a loop; for every snapshot with `StartupReapComplete`, every participant id is in `LiveIds` or `Claims` or has failed.
  - `DaemonConnectUsesTheSnapshot`: a `ServerConnection` with `GetRecoverySnapshot` wired builds a connect whose fields equal the snapshot's.
- [ ] **Step 2: Run**; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the class, `DaemonStatusReportTests`, `CorrelatedStatusReportTests`, `RestartCoordinatorTests`; expect PASS.
- [ ] **Step 5: Commit** `Hold startup completeness while participants resume, within a deadline`.

### Task 9: Local control for resuming participants

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`LedgerStatusRow`, `HandleLocalStopCoreAsync`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (`LedgerOnlyAgents` includes participant candidates)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlAgentResumeTests.cs`

**Interfaces:**
- Consumes: Tasks 4, 5 and 7.
- Produces: `LedgerStatusRow` reports `entry.Participant?.Kind ?? LaunchKind.Default`. In `HandleLocalStopCoreAsync` the kind protection is checked from the ledger entry before `DiscardResumeEntry`: an unforced stop of a pending participant returns the same refusal text a live one gets. `stop --all` lists pending participants as `skipped` without `--force` and withdraws them with it.

- [ ] **Step 1: Write the failing tests:** `AResumingParticipantIsListedAsAFlowAgent`; `AnUnforcedStopRefusesAResumingParticipantAndKeepsItsEntry`; `AForcedStopWithdrawsIt`; `StopAllSkipsThenWithdrawsPendingParticipants`.
- [ ] **Step 2: Run** `LocalControlAgentResumeTests`; expect failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**; expect PASS.
- [ ] **Step 5: Commit** `Protect a resuming participant from an unforced local stop`.

### Task 10: Claude probe, enablement and docs

**Files:**
- Create: `docs/probes/2026-10-23-claude-reviewer-restart-resume/findings.md`
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs` (`bool SupportsReviewerResume => false;`), `src/Capacitor.Cli.Daemon/Harness/Claude/ClaudeLauncher.cs` (`SupportsReviewerResume => true`), `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntimeFactory.cs` (`SupportsReviewerRestartResume(t, work) => work == WorkLocation.OwnedWorktree && launcher.SupportsReviewerResume`), `README.md` (`kcap daemon restart --force` line and the **Daemon restarts** paragraph), `docs/superpowers/specs/2026-10-17-daemon-restart-agent-resume-design.md` §6 (point at the new spec)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PtyHostedAgentRuntimeFactoryTests.cs`

- [ ] **Step 1: Live probe** against a local daemon built from this branch and a real flow: start a `spec-review` flow with `vendor: claude`, let round 1 finish, then `kcap daemon restart`. Confirm the reviewer's agent id is unchanged in `kcap agent ls`, submit round 2 asking about a detail only round 1 saw, and confirm the answer recalls it. Then repeat with a restart mid-round with `--force`, and confirm that round is answered by the same agent id. Record versions, commands and outcomes in `findings.md`. If any step fails, stop and report; Claude stays disabled.
- [ ] **Step 2: Write the failing test** `ClaudeResumesAnOwnedReviewerButNotABorrowedOne`.
- [ ] **Step 3: Implement** the predicate chain.
- [ ] **Step 4: Run** the daemon unit suite; expect PASS. Run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`; expect no output.
- [ ] **Step 5: Docs.** README: the `--force` line reads "a participant mid-round is parked, resumed or ended"; the Daemon restarts paragraph adds one sentence: "Claude review-flow reviewers in a daemon-owned worktree resume too, keeping their earlier rounds; other reviewers are parked (Codex) or ended, and their flow starts a fresh reviewer on its next round."
- [ ] **Step 6: Commit** `Resume Claude reviewers across a daemon restart (#1872)`.
