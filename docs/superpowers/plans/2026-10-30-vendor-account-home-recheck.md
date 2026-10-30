# Account Home Re-check Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A session's account label always describes the login its home is currently signed in to, the same
login its usage bars come from.

**Architecture:**
- A detected re-login no longer retires sessions. It records when the new generation began, and that one
  registry field guards the new reading:
  - observations produced before it are dropped;
  - a Claude status-line payload counts only when it is fresh.
- Producers always record against the home's current generation.
- The daemon re-probes every registered home once it is 10 minutes stale, coordinated through the shared
  registry.
- The daemon's reports and the desktop app take the label from the current registry row by account id.

**Tech Stack:** .NET 10, TUnit, `FakeTimeProvider`, ReactiveUI (app)

**Spec:** `docs/superpowers/specs/2026-10-30-vendor-account-home-recheck-design.md` (Part 1 only; Part 2 is
kcap-server#2849)

## Global Constraints

- Periodic interval `TimeSpan.FromMinutes(10)`, the same as `AccountRefresher.Cooldown`. Tick every
  `TimeSpan.FromMinutes(2)`.
- A home is due when `max(RefreshedAt, ProbeStartedAt)` is null or at least the interval old. The due check
  and the `ProbeStartedAt` stamp happen in one registry mutation.
- One `AccountRefresher` consumer: probes never run concurrently within a daemon. `ProbeSeq` ordering is
  unchanged.
- New persisted `VendorAccount` fields, both nullable so older files load:
  - `GenerationStartedAt` → `"generation_started_at"`
  - `ProbeStartedAt` → `"probe_started_at"`
  - Insert both before `LabelSetByUser`.
- `retired_sessions` in an existing `accounts.json` must still load. The member is deleted, and the registry
  context ignores unknown members; a test pins this.
- Comments follow `CLAUDE.md`: scarce, and nothing narrates the retirement rule being removed.
- Each task must build the solution and pass its own suites before its commit: stage deletions with their
  consumers.

## Review Focus

1. A Claude session idle since before the switch renders after the commit with an unchanged counter →
   the new reading's windows are unchanged (Task 3).
2. A counter goes backwards after the switch (1000 → 200 → 200 → 300) → only 300 records (Task 3).
3. A session whose baseline was evicted, or whose payload lacks `total_api_duration_ms` → it does not
   merge after a switch. 65 sessions round-robin still make progress (Task 3).
   - "After a switch" includes an older registry's nonzero generation without a timestamp.
4. Two daemons sharing a registry tick at the same moment, or out of phase → each home is probed once per
   due period (Task 4).
5. The account row disappears from the snapshot → the label falls back to the launch label and the bars
   clear, with no observer seeing a mixed pair (Task 6).

---

### Task 1: A re-login starts a new generation without retiring anyone

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/VendorAccount.cs`. Delete `RetiredSessions`. Add
  `DateTimeOffset? GenerationStartedAt = null` before `LabelSetByUser`.
- Modify: `src/Capacitor.Cli.Core/Accounts/AccountRefresh.cs`
- Modify: `src/Capacitor.Cli.Core/Accounts/Readings/ReadingStore.cs`. Delete the retired check at line 58
  and update the `TryRecord` doc comment. The signature is unchanged in this task.
- Test:
  - `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`
  - `AccountStoreTests.cs` (lines 231 and 255–265 use `RetiredSessions`)
  - `AccountSnapshotBuilderTests.cs:168`
  - `Readings/ReadingStoreTests.cs:14–65`. Drop the `retired` parameter of `Codex(...)`, and delete the
    retired-session `TryRecord` test.

**Interfaces:**
- Produces, used by Tasks 2 and 3:
  - `VendorAccount.GenerationStartedAt`
  - `[JsonIgnore] public bool HasSwitched => Generation > 0 || GenerationStartedAt is not null;` on
    `VendorAccount`

- [ ] **Step 1: Rewrite the tests.**
  - Rename `Login_change_bumps_generation_deletes_reading_and_retires_sessions` to
    `Login_change_bumps_generation_and_deletes_reading`. It asserts `Generation == 1`, the reading is gone,
    and `GenerationStartedAt` equals the fake clock *before* `ProbeAsync` ran. Advance the clock inside the
    fake probe to tell the two apart.
  - `Retired_session_late_observation_is_dropped` →
    `A_session_seen_before_the_switch_records_into_the_new_generation`:
    `Seed("s1", generation: 1)` is true, and `Seed("s1", generation: 0)` is false (stale generation).
  - `Producer_committing_just_before_retirement_has_its_session_retired` →
    `Producer_committing_just_before_the_switch_is_not_fenced`. Keep its interleaving rig. Assert the
    producer returned true, the reading is null afterwards (deleted by the bump), `GenerationStartedAt` is
    set, and `Seed("S", generation: 1)` is true. Task 3 extends this test.
  - `Email_change_without_id_then_id_arrives_retires_once` → `…_bumps_generation_once`.
  - `Failed_registry_save_leaves_reading_and_retirement_untouched` →
    `Failed_registry_save_leaves_reading_and_generation_untouched`. `GenerationStartedAt` stays null.
  - `First_probe_records_identity_without_bumping_generation` also asserts `GenerationStartedAt` is null.
  - `AccountStoreTests`: replace the `RetiredSessions` cases with
    `Registry_with_retired_sessions_still_loads`. Write raw JSON for an entry with
    `"retired_sessions": ["a"]` and assert `Load()` returns the entry.
  - `AccountSnapshotBuilderTests:168`: drop the `RetiredSessions` argument.
  - `HasSwitched_holds_for_a_legacy_nonzero_generation_without_timestamp`: raw JSON with
    `"generation": 1` and no `generation_started_at` → `HasSwitched` is true. Also cover generation 0 with
    no timestamp (false) and generation 0 with a timestamp (true).
- [ ] **Step 2: Run them and see the failures.**
  `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/Account*/*"`
- [ ] **Step 3: Implement.**
  - In `RunAsync`, take `var probeStartedAt = time.GetUtcNow();` just before `probe.ProbeAsync`, and pass
    it into `Commit`.
  - When `differs`, `Commit` sets `Generation + 1` and `GenerationStartedAt = probeStartedAt`. Delete
    `MaxRetiredSessions` and the retired list.
  - Remove the retired check in `TryRecord`.
- [ ] **Step 4: Build and run.** Expected: build clean, PASS.
  `dotnet build Capacitor.slnx`, then the Core accounts filter above plus
  `--treenode-filter "/*/*/ReadingStoreTests/*"`.
- [ ] **Step 5: Commit.** `Start a new account generation without retiring sessions (#2176)`

### Task 2: Producers record into the home's current generation

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/Readings/ReadingStore.cs`. The new `TryRecord` is below.
- Modify: `src/Capacitor.Cli.Core/Accounts/ResolvedAccount.cs`. Delete `Generation` and its doc sentence.
- Modify: `src/Capacitor.Cli.Core/Accounts/AccountResolution.cs:89`
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeStatusLineEntry.cs:95-100`
- Modify: `src/Capacitor.Cli/Harness/Codex/CodexRolloutReadings.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AccountReadingSink.cs`
- Modify: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`. Update its `Seed` helper to
  the new signature. A "stale generation" case no longer exists, so drop the `generation: 0` assertion
  from Task 1's test.
- Test:
  - `Readings/ReadingStoreTests.cs`
  - `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountReadingSinkTests.cs`
  - the `CodexRolloutReadings` tests (`grep -rl CodexRolloutReadings test/`)
  - `AccountResolutionTests.Every_resolution_carries_the_entrys_current_generation` (delete it)
  - any test constructing `ResolvedAccount` with a generation (`grep -rn "new ResolvedAccount(" test/`)

**Interfaces:**
- Produces:
  `public bool TryRecord(string accountId, Func<ReadingFile, VendorAccount, ReadingFile?> update)`.
  - The second argument is the registry entry as read under the lock. Producers use its
    `GenerationStartedAt` and `HasSwitched`.
  - The account must be registered. The file read and written is always of its current generation.
  - When `update` returns null, nothing is written and the method returns false.
  - Unchanged: it never throws for IO, lock or parse failures, and it is not re-entrant.

- [ ] **Step 1: Write the failing tests.**
  - `ReadingStoreTests`:
    - `TryRecord_writes_into_the_current_generation`: account at generation 2 → the file's
      `Reading.Generation == 2`.
    - `TryRecord_passes_the_registry_entry`: the update receives the entry, including
      `GenerationStartedAt`.
    - `TryRecord_writes_nothing_when_update_declines`: returns false and no file exists.
    - These replace `TryRecord_skips_a_stale_generation`.
  - `AccountReadingSinkTests`:
    - `Observation_before_the_generation_start_is_skipped`: `Processed` reports false.
    - `Observation_after_a_re_login_is_recorded_without_relaunch`: the `ResolvedAccount` was taken before
      the bump, the registry is at generation 1 with a start before the observation → recorded.
  - `CodexRolloutReadings` tests:
    - `Lines_before_the_generation_start_are_dropped_from_a_batch`: one `token_count` line on each side
      of the start → only the later window is recorded.
    - `Batch_entirely_before_the_generation_start_writes_nothing`.
- [ ] **Step 2: Run them.** They fail to compile or fail.
- [ ] **Step 3: Implement.**
  - `TryRecord`: drop the generation parameter and check, pass the entry, and write only a non-null
    result.
  - `AccountReadingSink`: the update returns null when `observation.ProducedAt < GenerationStartedAt`.
  - `CodexRolloutReadings`: keep observations with `ProducedAt >= GenerationStartedAt` (all of them when it
    is null), and return null when none remain.
  - `ClaudeStatusLineEntry`:
    `(held, _) => ClaudeStatusLineReading.Apply(held, payload, now)`. Task 3 uses the entry. The pre-check
    against `ReadingFile.Empty(account.Generation)` stays.
- [ ] **Step 4: Build the solution, then run the Core, Cli and Daemon suites' account and reading tests.**
  Expected: PASS.
- [ ] **Step 5: Commit.** `Record readings into an account's current generation (#2176)`

### Task 3: After a switch, a status-line payload counts only when fresh

**Files:**
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudeStatusLineReading.cs`
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeStatusLineEntry.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeStatusLineReadingTests.cs`,
  `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`

**Interfaces:**
- Consumes: `TryRecord`'s entry argument and `VendorAccount.HasSwitched` (Tasks 1–2).
- Produces:
  `public static ReadingFile? Apply(ReadingFile held, JsonElement payload, bool afterSwitch, DateTimeOffset now)`.
  - When `afterSwitch` is true and the payload is not fresh, the baseline updates as today but no window
    merges.
  - When `afterSwitch` is false, behaviour is today's.
  - `MaxSessions` becomes 256, still evicting the least recently seen.

- [ ] **Step 1: Write the failing tests.**
  - In `ClaudeStatusLineReadingTests`, all with `afterSwitch: true`:
    - `After_a_switch_a_session_without_baseline_records_baseline_only`: counter 1000,
      `seven_day.used_percentage: 80` → `Reading.Windows` is empty and `S` is at 1000.
    - `After_a_switch_an_unchanged_counter_does_not_merge`: held `S` at 1000, payload 1000 → no windows.
    - `After_a_switch_a_fresh_payload_merges`: held `S` at 1000, payload 1500 with `used_percentage: 8` →
      the window is at 8 with `ConfirmedAt` set.
    - `After_a_switch_a_counter_reset_merges_only_once_it_advances`: run the sequence 1000 → 200 → 200 →
      300 from an empty held file. Only the 300 payload yields windows.
    - `After_a_switch_a_payload_without_counter_does_not_merge`.
    - `After_a_switch_an_evicted_baseline_does_not_merge`: held has 256 other sessions and `S` absent,
      payload for `S` → no windows.
    - `After_a_switch_many_sessions_round_robin_still_make_progress`: 65 sessions each send counter 100,
      then each sends 100 again, then each sends 200. The first two rounds leave windows empty, and the
      third records.
  - `Without_a_switch_a_first_payload_still_merges_unconfirmed` (`afterSwitch: false`) pins today's
    behaviour.
  - In `AccountRefreshTests`, extend `Producer_committing_just_before_the_switch_is_not_fenced` so the
    producer and the follow-ups go through `TryRecord` with
    `(held, account) => ClaudeStatusLineReading.Apply(held, payload, account.HasSwitched, now)`:
    - the producer records counter 1000 under generation 0;
    - after the commit, the same payload (1000, 80%) leaves the generation-1 reading with no windows;
    - a payload at 1500 with 8% records 8% into generation 1.
  - `Legacy_switched_account_replay_does_not_merge`, in `AccountRefreshTests`:
    - raw registry JSON with `"generation": 1`, no `generation_started_at`, and
      `"retired_sessions": ["A"]`;
    - an unchanged-identity refresh runs;
    - then `A`'s replay (no baseline) goes through `TryRecord` → no windows.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.**
  - Add the `afterSwitch` parameter. When it is true and the payload is not fresh, build the observation
    with an empty `Windows` list. Raise `MaxSessions` to 256.
  - `ClaudeStatusLineEntry` passes `account.HasSwitched` in its update and `false` in its pre-check.
- [ ] **Step 4: Run both test classes.** Expected: PASS.
- [ ] **Step 5: Commit.** `Count only fresh status-line payloads after an account switch (#2176)`

### Task 4: Re-probe every registered home once it is due

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/VendorAccount.cs`. Add `DateTimeOffset? ProbeStartedAt = null`
  before `LabelSetByUser`.
- Modify: `src/Capacitor.Cli.Core/Accounts/AccountRefresh.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AccountRefresher.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`,
  `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountRefresherTests.cs`

**Interfaces:**
- Produces:
  - `AccountRefresh.RunIfDueAsync(AccountStore store, string accountId, IAccountProbe probe, TimeProvider time, TimeSpan interval, CancellationToken ct)`,
    returning `Task<RefreshOutcome?>`.
    - It returns null, changing nothing, when the account is not due.
    - Otherwise it behaves as `RunAsync`.
    - The due check runs inside the same `MutateWithRevision` that bumps `ProbeSeq`. Both `RunAsync` and
      `RunIfDueAsync` stamp `ProbeStartedAt = now` there.
  - `AccountRefresher.Interval = TimeSpan.FromMinutes(10)` and `AccountRefresher.Tick = TimeSpan.FromMinutes(2)`.
  - Test seams on `AccountRefresher`:
    - `internal bool Periodic { get; init; } = true;`
    - `internal Func<string, CancellationToken, Task>? BeforePeriodicCheck { get; set; }`. The consumer
      awaits it after it marks a periodic check running, and before `RunIfDueAsync`.

- [ ] **Step 1: Write the failing Core tests in `AccountRefreshTests`.**
  - `RunIfDue_skips_a_home_refreshed_within_the_interval`: the result is null, and `ProbeSeq` and the probe
    count are unchanged.
  - `RunIfDue_skips_a_home_whose_probe_started_within_the_interval`: `ProbeStartedAt` is 1 minute ago and
    `RefreshedAt` is old.
  - `RunIfDue_probes_a_due_home_and_stamps_probe_started_at`.
  - `RunIfDue_probes_a_home_never_refreshed`.
- [ ] **Step 2: Write the failing refresher tests.**
  - Set `Periodic = false` wherever the class builds a refresher for the existing tests. Their scenarios
    are about requests. `Duplicate_requests_coalesce`,
    `Two_daemons_sharing_a_registry_settle_after_a_persistent_reached_limit` and
    `A_soon_request_replaces_a_pending_deferral` advance whole cooldowns and would otherwise see periodic
    probes.
  - New tests use `Periodic = true`:
    - `Every_registered_account_is_probed_again_once_due`: 2 accounts, startup probes run, advance
      12 minutes → 4 probes.
    - `A_home_not_yet_due_is_not_probed_by_the_tick`: advance 8 minutes → 2 probes.
    - `Out_of_phase_daemons_probe_each_home_once_per_due_period`: two refreshers on one store, the second
      started 3 minutes after the first. Advance 30 minutes in 1-minute steps, waiting for idle at each
      step. Each home's probes after startup fall in [2, 3], not the 5–6 that independent clocks give.
    - `Simultaneous_ticks_probe_a_due_home_once`: two refreshers on one store, `Gate` holds the first
      probe. Advance to due and let both tick → one probe for that period. Release the gate.
    - `An_account_added_after_start_is_probed_on_the_next_tick`: no `Request`, advance 2 minutes →
      probed once.
    - `A_removed_account_is_not_probed_by_the_tick`.
    - `Dispose_stops_the_tick`.
    - `A_tick_stands_down_for_a_home_with_a_deferred_request`:
      - Work finished at minute 0, and `Request(Work)` at minute 1 is deferred to minute 10.
      - Advance to minute 12.
      - Work is probed exactly once (at 10), and the ticks queued no check of Work while the request was
        deferred.
    - `A_deferred_request_expiring_behind_a_periodic_check_still_probes`:
      - `BeforePeriodicCheck` holds the consumer on a periodic check of Spare.
      - Work's deferred request reaches its deadline meanwhile and is queued as a request.
      - The shared row's `RefreshedAt` for Work is set fresh, as if another daemon had refreshed it.
      - Release the hold. Work is probed: a request never goes through the due check.
    - `A_skipped_check_spends_no_soon_budget`:
      - `RequestSoon(Work)` probes at minute 0, and a second `RequestSoon(Work)` at minute 2 is deferred to
        minute 10.
      - Advance to 10: Work probes.
      - `RequestSoon(Work)` at minute 12 waits for minute 20 (the budget was stamped at 10).
    - `A_request_racing_a_not_due_check_waits_from_the_last_real_refresh`:
      - Work's last real probe was at minute 0, so a check at minute 4 is not due.
      - Hold the tick's check of Work at minute 4 with a `BeforePeriodicCheck` that blocks only for Work at
        or after minute 4.
      - `Request(Work)` arrives during the hold (`_running == Work`). Release the hold: the check is not
        due and probes nothing.
      - Work is probed at minute 10, not at minute 14.
- [ ] **Step 3: Run them.** They fail.
  `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AccountRefresherTests/*"`
- [ ] **Step 4: Implement `RunIfDueAsync`.** It shares `RunAsync`'s body through a private core that takes
  `TimeSpan? dueAfter`.
- [ ] **Step 5: Implement the refresher.**
  - After the startup requests, when `Periodic` is set, start
    `time.CreateTimer(_ => OnTick(store), null, Tick, Tick)`. Keep it in a field that `Dispose` disposes
    under `_lock`.
  - The work channel carries `(string Id, bool Periodic)`. `Request`, `RequestSoon` and the deferral
    timers are unchanged; `EnqueueHeld` writes `(id, false)`. `_queued` holds only request ids.
  - `OnTick` loads the registry, catching what the startup load catches. For each id that is not in
    `_queued`, `_deferred` or `_periodicQueued` and is not `_running`, it adds the id to
    `_periodicQueued` and writes `(id, true)`. It never throws.
  - The consumer, for `(id, true)`:
    - removes the id from `_periodicQueued`;
    - skips it if the id is now in `_queued` or `_deferred`;
    - otherwise marks it `_running`, awaits `BeforePeriodicCheck`, and calls
      `RunIfDueAsync(…, Interval, …)`.
    - It never touches `_soonPending` or `_lastSoon`.
  - When the check probed, the `finally` is today's: `_lastFinished`, a null deferral deferred by
    `Cooldown`, and `Finished`.
  - When the check returned null (not due), the `finally` leaves `_lastFinished` and `Finished` alone.
    It removes a null `_deferred` entry and calls `Request(id)` after leaving the lock, judging that
    request against the real `_lastFinished`.
  - For `(id, false)`, the consumer is today's code.
  - Update the class summary: start, then whenever due, then on request.
- [ ] **Step 6: Run both test classes.** Expected: PASS.
- [ ] **Step 7: Commit.** `Re-probe every account home once it is ten minutes stale (#2176)`

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
  - `Status_reports_the_current_account_label`: the live `AgentStatusDto` carries `new@x`.
  - `Ledger_status_row_reports_the_current_account_label`: an interrupted row from the resume ledger
    carries `new@x`.
  - `Unknown_account_id_keeps_the_launch_label`.
- [ ] **Step 2: Run them.** They fail.
- [ ] **Step 3: Implement.** Route the six sites through `CurrentAccountLabel`. The resume ledger writes
  (`AgentOrchestrator.Resume.cs:388,458`) keep the launch label.
- [ ] **Step 4: Run** `AgentOrchestratorAccountTests` and `AgentOrchestratorResumeAccountTests`. Expected:
  PASS.
- [ ] **Step 5: Commit.** `Report the account home's current label for hosted sessions (#2176)`

### Task 6: The app takes the label and the bars from one row, swapped as one value

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs:486-520, 812-818, 1125-1134`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: the existing `accountReadings` (`IObservable<AccountsSnapshotDto?>`).
- Produces:
  - A private record `AccountStripState(string? Label, AccountUsageView? Usage)` with one backing field.
  - `AccountLabel` and `AccountStripUsage` become get-only projections of that field.
  - One setter swaps the field, then raises `PropertyChanged` for every dependent property:
    `AccountLabel`, `AccountStripUsage`, `ShowsAccountUsage`, `ShowsAccountDirectory` and
    `AccountReadingNote`.

- [ ] **Step 1: Write the failing tests.** The session info has `AccountId = "a1"` and
  `AccountLabel = "old@x"`. The snapshot subject has row `a1` labelled `new@x`, with a reading.
  - `Account_label_follows_the_snapshot_row`: `AccountLabel == "new@x"`.
  - `Account_label_and_usage_never_mix_when_the_row_changes`: subscribe to `PropertyChanged`, and on every
    notification record the pair (`AccountLabel`, `AccountStripUsage`). Push a row with a new label and a
    new reading, then an empty snapshot. Every recorded pair is one of the three whole states: old row,
    new row, or the fallback (`"old@x"`, null).
  - `Account_label_without_readings_source_is_the_launch_label`: `accountReadings` is null → `"old@x"`.
- [ ] **Step 2: Run them.** They fail. Unset `KCAP_DAEMON_NAME` and `KCAP_PROFILE` first.
  `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ChatTabViewModelTests/*"`
- [ ] **Step 3: Implement.**
  - Replace `StripUsage` with a projection to `AccountStripState` from the same row lookup, falling back to
    the launch label.
  - `ApplyAccount` stores the launch fallback (today's expression) in a field and pushes the account id.
    The projection combines id, fallback, snapshot and clock.
  - Without `accountReadings`, `ApplyAccount` sets the state directly to (fallback, null).
- [ ] **Step 4: Run.** Expected: PASS, the existing strip tests included.
- [ ] **Step 5: Commit.** `Show the account home's current label in the session strip (#2176)`

### Task 7: Whole-branch verification

- [ ] **Step 1:** `dotnet build Capacitor.slnx`. Expected: 0 warnings.
- [ ] **Step 2:** `dotnet test --solution Capacitor.slnx`. Expected: no new failures. The App identity tests
  need `KCAP_*` unset.
- [ ] **Step 3:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`.
  Expected: empty. Do the same for the daemon project.
- [ ] **Step 4:** Check `README.md` and `src/Capacitor.Cli.Core/Resources/help-*.txt` for text on when
  accounts refresh. If any exists, update it to say "every 10 minutes".
