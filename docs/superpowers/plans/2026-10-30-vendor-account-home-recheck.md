# Account Home Re-check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A session's account label always describes the login its home is currently signed in to, the same
login its usage bars come from.

**Architecture:** The daemon's `AccountRefresher` re-probes every registered home on a 10-minute tick. A
detected re-login no longer retires sessions. Instead it records when the new generation began, and it
carries the old reading's session baselines so an idle session's replay cannot enter the new reading.
Producers always record against the home's current generation. The desktop app and the daemon's reports
take the label from the current registry row by account id.

**Tech Stack:** .NET 10, TUnit, `FakeTimeProvider`, ReactiveUI (app)

**Spec:** `docs/superpowers/specs/2026-10-30-vendor-account-home-recheck-design.md` (Part 1 only; Part 2 is
kcap-server#2849)

## Global Constraints

- Periodic interval `TimeSpan.FromMinutes(10)`, the same as `AccountRefresher.Cooldown`.
- A tick skips an account whose `RefreshedAt` is younger than half the interval (5 minutes). The registry
  is shared by every named daemon, and probing at exactly the interval would skip every other tick.
- One `AccountRefresher` consumer: probes never run concurrently within a daemon. `ProbeSeq` ordering is
  unchanged.
- `retired_sessions` in an existing `accounts.json` must still load. The registry context ignores unknown
  members, so deleting the property satisfies this; a test pins it.
- New persisted fields: `VendorAccount.GenerationStartedAt` → `"generation_started_at"`, and
  `ReadingSession.Carried` → `"carried"` (snake_case through `ReadingsJsonContext`). Both are nullable or
  defaulted, so older files load.
- Comments follow `CLAUDE.md`. Nothing narrates the retirement rule being removed.
- AOT: run the publish grep from `CLAUDE.md` before the last commit.

## Review Focus

1. A Claude session idle since before the switch renders its status line after the commit, with the same
   `total_api_duration_ms` → the new reading's windows are unchanged (Task 2).
2. The same session's counter goes backwards after the switch (a restart under the same id) → nothing
   merges on that payload, and its next fresh payload does merge (Task 2).
3. A Codex rollout batch straddles `GenerationStartedAt` → only the lines produced at or after it are
   recorded (Task 3).
4. Two daemons share one registry and both tick → a home refreshed by either one within 5 minutes is not
   probed again (Task 4).
5. The snapshot row for a session's account disappears (account removed) → the label falls back to the
   launch label, and the bars clear in the same update (Task 6).

---

### Task 1: A re-login starts a new generation without retiring anyone

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/VendorAccount.cs`. Delete `RetiredSessions`. Add
  `DateTimeOffset? GenerationStartedAt = null` (`"generation_started_at"`), inserted before
  `LabelSetByUser`.
- Modify: `src/Capacitor.Cli.Core/Accounts/Readings/ReadingSession.cs`. Append `bool? Carried = null`.
- Modify: `src/Capacitor.Cli.Core/Accounts/AccountRefresh.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`,
  `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountStoreTests.cs`

**Interfaces:**
- Produces: `VendorAccount.GenerationStartedAt` and `ReadingSession.Carried`, used by Tasks 2 and 3.

- [ ] **Step 1: Rewrite the retirement tests as no-retirement tests.** Retire the old test names. Every
  case below has a `Differs` login change.
  - Rename `Login_change_bumps_generation_deletes_reading_and_retires_sessions` to
    `Login_change_bumps_generation_and_carries_session_baselines`. It asserts:
    - `Generation == 1`.
    - `GenerationStartedAt` equals the fake clock's time *before* `ProbeAsync` ran. Advance the clock
      inside the fake probe to tell the two times apart.
    - The new reading file is of generation 1, has no windows, and its `Sessions` are `s1` and `s2` with
      their old `ApiDurationMs` and `Carried == true`.
  - `Retired_session_late_observation_is_dropped` and
    `Producer_committing_just_before_retirement_has_its_session_retired` are deleted. Task 2 owns the
    replacement.
  - `Email_change_without_id_then_id_arrives_retires_once` becomes `…_bumps_generation_once`.
  - `Failed_registry_save_leaves_reading_and_retirement_untouched` drops its retirement assertion and
    asserts `GenerationStartedAt` is still null.
  - `First_probe_records_identity_without_bumping_generation` asserts `GenerationStartedAt` is null.
- [ ] **Step 2: Add `Registry_with_retired_sessions_still_loads` to `AccountStoreTests`.** It writes the
  JSON of an entry with `"retired_sessions": ["a"]`, then asserts `Load()` returns the entry.
- [ ] **Step 3: Run and see the failures.**
  `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AccountRefreshTests/*"`
  The renamed tests fail to compile or fail.
- [ ] **Step 4: Implement.**
  - In `RunAsync`, capture `var probeStartedAt = time.GetUtcNow();` right before `probe.ProbeAsync` and
    pass it into `Commit`.
  - When `differs`, `Commit` sets `Generation + 1` and `GenerationStartedAt = probeStartedAt`. Drop
    `MaxRetiredSessions` and the retired list.
  - Extend `Decision` with `ReadingSession[] Carried`, read from the old reading inside `Commit` (it
    already holds `held`).
  - In the readings callback, after `held.Delete(accountId)` on a generation change, write
    `ReadingFile.Empty(account.Generation) with { Sessions = carried }` when `carried` is non-empty. Do this
    before the limits merge, so a Codex probe's own limits land in the same file.
- [ ] **Step 5: Run the Core accounts tests.** Expected: PASS.
  `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/Account*/*"`
- [ ] **Step 6: Commit.** `Start a new account generation without retiring sessions (#2176)`

### Task 2: A carried session's replay never merges

**Files:**
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeStatusLineReading.cs`
- Test: the existing `ClaudeStatusLineReading` tests in `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/`.
  Find the file with `grep -rl ClaudeStatusLineReading test/Capacitor.Cli.Core.Tests.Unit`.

**Interfaces:**
- Consumes: `ReadingSession.Carried` (Task 1).

- [ ] **Step 1: Write the failing tests.** The held file is generation 1 with session `S` carried at
  baseline 1000 and no windows.
  - `Carried_session_replay_leaves_windows_unchanged`: a payload with `total_api_duration_ms: 1000` and
    `seven_day.used_percentage: 80` → `Apply` returns a file whose `Reading.Windows` is empty, and `S` is
    still carried at 1000.
  - `Carried_session_fresh_payload_merges_and_clears_the_mark`: `total_api_duration_ms: 1500`,
    `used_percentage: 8` → the window is recorded at 8 with `ConfirmedAt` set, and `S` is at 1500 with
    `Carried` null.
  - `Carried_session_counter_reset_records_baseline_only`: `total_api_duration_ms: 200` → windows empty,
    `S` is at 200 and not carried. A later payload at 300 merges.
  - `Unknown_session_first_payload_still_merges_unconfirmed`: pins today's behaviour for a session with
    no carried baseline.
- [ ] **Step 2: Run them.** The first three fail.
- [ ] **Step 3: Implement.**
  - In `Apply`, when the held session is `Carried == true` and the payload is not `fresh`, update the
    baseline as today, but apply an observation with no windows. Limits and plan are absent from status
    line payloads, so an empty `Windows` list is the whole change.
  - Any replacement `ReadingSession` is written with `Carried` null.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit.** `Keep an idle session's replay out of a new account generation (#2176)`

### Task 3: Producers record into the home's current generation

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/Readings/ReadingStore.cs`. The new `TryRecord` signature is
  below.
- Modify: `src/Capacitor.Cli.Core/Accounts/ResolvedAccount.cs`. Delete `Generation`.
- Modify: `src/Capacitor.Cli.Core/Accounts/AccountResolution.cs:89`
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeStatusLineEntry.cs:93-100`
- Modify: `src/Capacitor.Cli/Harness/Codex/CodexRolloutReadings.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AccountReadingSink.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/Readings/ReadingStoreTests.cs`,
  `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountReadingSinkTests.cs`, the `CodexRolloutReadings`
  tests in `test/Capacitor.Cli.Tests.Unit`, and every test that constructs `ResolvedAccount` with a
  generation (fix them so they compile).

**Interfaces:**
- Produces:
  `public bool TryRecord(string accountId, Func<ReadingFile, DateTimeOffset?, ReadingFile?> update)`. The
  second argument is the account's `GenerationStartedAt`. When `update` returns null, nothing is written
  and the method returns false. The account must be registered. The file read is always of the account's
  current generation. Other contracts are unchanged: it never throws for IO, lock or parse failures, and
  it is not re-entrant.

- [ ] **Step 1: Write the failing tests.**
  - `ReadingStoreTests`:
    - `TryRecord_writes_into_the_current_generation`: the account is at generation 2 → the written file's
      `Reading.Generation == 2`.
    - `TryRecord_passes_the_generation_start`: the update receives `GenerationStartedAt`.
    - `TryRecord_writes_nothing_when_update_declines`: the update returns null → returns false and no file
      is written.
    - Replace `TryRecord_skips_a_stale_generation` with these. Drop the `retired` parameter from the
      `Codex(...)` helper.
  - `AccountReadingSinkTests`:
    - `Observation_before_the_generation_start_is_skipped`: `Processed` reports false.
    - `Observation_after_a_re_login_is_recorded_without_relaunch`: post with a `ResolvedAccount` resolved
      at generation 0, while the registry is at generation 1 and started before the observation →
      recorded.
  - `CodexRolloutReadings` tests:
    - `Lines_before_the_generation_start_are_dropped_from_a_batch`: a batch of two `token_count` lines
      timestamped either side of `GenerationStartedAt` → only the later one's window is recorded.
    - `Batch_entirely_before_the_generation_start_writes_nothing`.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.**
  - `TryRecord`: drop the generation and retired checks, pass `account.GenerationStartedAt`, and write
    only a non-null result.
  - `AccountReadingSink`: the update returns null when `observation.ProducedAt < start`.
  - `CodexRolloutReadings`: keep only observations with `ProducedAt >= start` (or all of them when start is
    null), and return null when none are left.
  - `ClaudeStatusLineEntry`: pass `(held, _) => ClaudeStatusLineReading.Apply(held, payload, now)`. The
    status line's replay guard is Task 2's carried baseline, and `now` is always after the start. The
    pre-check against `ReadingFile.Empty(account.Generation)` stays.
  - Remove `ResolvedAccount.Generation` and update its doc comment so it no longer mentions generation.
- [ ] **Step 4: Build the solution and run the three affected suites' account tests.** Expected: build
  clean, tests PASS.
  `dotnet build Capacitor.slnx`
- [ ] **Step 5: Commit.** `Record readings into an account's current generation (#2176)`

### Task 4: Re-probe every registered home on a timer

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AccountRefresher.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountRefresherTests.cs`

**Interfaces:**
- Produces: `public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);` on `AccountRefresher`.

- [ ] **Step 1: Write the failing tests.** Use the class's `CountingProbe`, `Register` and `_time`.
  - `Every_registered_account_is_probed_again_after_the_interval`: register `Work` and `Spare`, wait until
    idle (2 probes), advance `Interval` → 4 probes.
  - `An_account_added_after_start_is_probed_on_the_next_tick`: register `Spare` after start without a
    `Request`, advance `Interval` → `probe[Spare] == 1`.
  - `A_home_refreshed_elsewhere_within_half_the_interval_is_skipped`: after the startup probe, set `Work`'s
    `RefreshedAt` in the store to `now + Interval - 1 min`, the way another daemon's commit would. Advance
    `Interval` → `probe[Work]` unchanged and `probe[Spare]` incremented.
  - `A_removed_account_is_not_probed_by_the_tick`.
  - `Dispose_stops_the_tick`: dispose, advance `Interval` → no new probes.
- [ ] **Step 2: Run them.** They fail.
  `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AccountRefresherTests/*"`
- [ ] **Step 3: Implement.**
  - At the end of the startup block in `ExecuteAsync`, create
    `time.CreateTimer(_ => Tick(store), null, Interval, Interval)`. Hold it in a field that `Dispose`
    disposes under `_lock`.
  - `Tick` loads the registry, catching the same exceptions as the startup load, and calls `Request(id)`
    for each account whose `RefreshedAt` is null or at least `Interval / 2` old.
  - `Tick` never throws.
  - Update the class summary: start, then every interval, then on request.
- [ ] **Step 4: Run.** Expected: PASS, the existing cooldown tests included.
- [ ] **Step 5: Commit.** `Re-probe every account home every ten minutes (#2176)`

### Task 5: The daemon reports the home's current label

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`. The sites are lines 2135, 2151, 5305
  and 5803.
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:170,189`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorAccountTests.cs`

**Interfaces:**
- Consumes: the existing `AccountsSnapshot` (`Func<AccountsSnapshotDto?>`).
- Produces: `string? CurrentAccountLabel(string? accountId, string? launchLabel)` in `AgentOrchestrator`.
  It returns the snapshot row's `Label` for `accountId`, or `launchLabel` when there is no row.

- [ ] **Step 1: Write the failing tests.** Launch with an account labelled `old@x`, then replace the
  publisher's snapshot so the same id is labelled `new@x`.
  - `Heartbeat_reports_the_current_account_label`: `BuildLiveAgents()` carries `new@x`.
  - `Status_reports_the_current_account_label`: the `AgentStatusDto` from the status IPC carries `new@x`.
  - `Ledger_status_row_reports_the_current_account_label`: an interrupted row from the resume ledger
    carries `new@x`.
  - `Unknown_account_id_keeps_the_launch_label`.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.** Route the six sites through `CurrentAccountLabel`. Resume ledger writes
  (`AgentOrchestrator.Resume.cs:388,458`) keep the launch label.
- [ ] **Step 4: Run** `AgentOrchestratorAccountTests` and `AgentOrchestratorResumeAccountTests`. Expected:
  PASS.
- [ ] **Step 5: Commit.** `Report the account home's current label for hosted sessions (#2176)`

### Task 6: The app takes the label and the bars from one row

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs:486-520, 812-818, 1125-1134`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: the existing `accountReadings` (`IObservable<AccountsSnapshotDto?>`).

- [ ] **Step 1: Write the failing tests.** The session info has `AccountId = "a1"` and
  `AccountLabel = "old@x"`, and the snapshot subject has row `a1` labelled `new@x` with a reading.
  - `Account_label_follows_the_snapshot_row`: `AccountLabel == "new@x"`.
  - `Account_label_and_usage_change_in_one_update`: push a row with a new label and a new reading. Record
    `PropertyChanged` order and assert that no observed state pairs the new label with the old usage, or
    the reverse.
  - `Account_label_falls_back_to_the_launch_label_without_a_row`: an empty snapshot → `"old@x"` and
    `AccountStripUsage == null`.
  - `Account_label_without_readings_source_is_the_launch_label`: `accountReadings` is null → `"old@x"`.
- [ ] **Step 2: Run them.** They fail.
  `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"`
  (with `KCAP_DAEMON_NAME` and `KCAP_PROFILE` unset).
- [ ] **Step 3: Implement.**
  - Replace `StripUsage` with one projection, `(AccountUsageView? Usage, string? Label)`, computed from the
    same row lookup.
  - The subscriber sets `AccountStripUsage` and `AccountLabel` back to back.
  - `ApplyAccount` keeps the launch fallback in a field (today's expression) and pushes it so the
    projection can fall back.
- [ ] **Step 4: Run.** Expected: PASS.
- [ ] **Step 5: Commit.** `Show the account home's current label in the session strip (#2176)`

### Task 7: Whole-branch verification

- [ ] **Step 1:** `dotnet build Capacitor.slnx`. Expected: 0 warnings.
- [ ] **Step 2:** `dotnet test --solution Capacitor.slnx`. Expected: no new failures. The known
  environment failures are in memory: App identity tests need `KCAP_*` unset.
- [ ] **Step 3:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`.
  Expected: empty. Do the same for the daemon project.
- [ ] **Step 4:** Check `README.md` and `help-*.txt` for any mention of when accounts refresh. Update them
  to say "every 10 minutes" if they mention it.
