# Vendor account readings (CLI and daemon) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Record per-account usage readings and identity on each host and publish them in the daemon's accounts snapshot (kcap-cli side of #1956).

**Architecture:** A pure reading model and merge in Core is shared by every producer: the `kcap statusline` wrapper (Claude), the rollout watcher, the app-server runtime and the idle refresh (Codex), and the Claude limit-hit detector. Producers write `accounts/readings/<id>.json` under `readings.lock`; the account entry gains identity, `generation` and status line bookkeeping under `accounts.lock`. The daemon's `AccountsPublisher` folds readings into the public account view it already publishes locally and to the server.

**Tech Stack:** .NET 10 NativeAOT, System.Text.Json source generation, TUnit.

**Spec:** `docs/superpowers/specs/2026-10-07-vendor-accounts-and-limits-design.md` (Part B: 3.2 identity and refresh, 5.1–5.5, 6.1–6.2), read with `docs/superpowers/specs/2026-10-21-vendor-accounts-launch-choice-design.md` Sections 4.3, 9, 10 item 4.

**Scope decisions (confirmed with the user):** slice 4 builds the full identity probe and refresh of spec 1 Section 3.2, which slices 1–3 did not build, including `kcap accounts refresh`. The server store stays in memory; persistence across a server restart is a declared loose end. The server half (merge rules, serving to the owner) is a separate plan in kcap-server, written after this PR opens.

## Global Constraints

- Every DTO and persisted-record addition is trailing and optional, so an older daemon, server, app or registry file keeps binding; persisted `accounts.json` entries without the new fields load with defaults (`generation` 0).
- Lock order is always `accounts.lock` before `readings.lock`. Producers take only `readings.lock`, with a budget of at most 50 ms, and skip the observation on contention.
- The reading file is replaced atomically (`AtomicFile.Replace`, owner-only 0600); the readings directory is 0700.
- Merging keeps the maximum `confirmedAt` per window and never restamps a producer time; it is one function for producer, daemon and server.
- kcap never opens a vendor credential file, never calls a usage endpoint itself, and never publishes a status line backup or original command.
- A producer or wrapper failure never blocks or fails the agent or the status line it rides on.
- Readings and limit hits attribute to the agent's resolved account; an unregistered default (null account id) records nothing.
- The transcript projection and `TranscriptIds` are untouched; Codex producers are side outputs.
- `kcap statusline` runs before repository, profile, telemetry, server-URL and update-notice work, and before `NestedHarness.ShedInheritedHosting`, so the original command sees the environment Claude gave it.
- README.md and `src/Capacitor.Cli.Core/Resources/help-*.txt` change in the same PR for `kcap statusline` and `kcap accounts refresh`.
- Commit subjects end with `(#1956)`; no Linear ids in source; comments follow CLAUDE.md's Comments rules.
- No AOT warnings: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.

## Review Focus

1. A Claude settings file whose `statusLine` is not an object (a string, an array) or whose `command` is not a string: wiring leaves it untouched and reports the step failed, never throws or overwrites. Test in Task 3.
2. Two Claude sessions on one account feeding the wrapper alternately: each keeps its own `total_api_duration_ms` baseline, so session A's counter never confirms session B's replay. Test in Task 4.
3. A Codex `token_count` line whose `rate_limits` is `null` writes nothing; one whose `primary` and `secondary` are both null records only its plan and credits, never clears a block; neither throws on the watcher. Test in Tasks 5 and 6.
4. A reading file that is truncated or not JSON: the producer replaces it with a fresh reading (the observation still lands), and the daemon keeps publishing the previous good reading rather than dropping the account. Test in Tasks 2 and 8.
5. The wrapper run with stdin closed immediately (empty) or with non-JSON stdin: it still runs the original with the same (empty) bytes and passes its exit code through. Test in Task 4.

---

## File map

Core (`src/Capacitor.Cli.Core/`):
- `Accounts/Readings/` (namespace `Capacitor.Cli.Core.Accounts.Readings`): `UsageWindowDto.cs`, `CreditsDto.cs`, `BlockConstraintDto.cs`, `BlockedMarkerDto.cs`, `AccountReadingDto.cs`, `ReadingObservation.cs`, `LimitSignal.cs`, `ReadingMerge.cs`, `ReadingFile.cs`, `ReadingSession.cs`, `ReadingStore.cs`, `ReadingsJsonContext.cs`.
- `Accounts/`: `VendorAccount.cs` (new trailing fields), `StatusLineBackup.cs`, `AccountIdentity.cs`, `SignInStates.cs`, `AccountStore.cs` (held-lock mutation, after-save hook), `AccountViewDto.cs` (trailing fields), `AccountSnapshotBuilder.cs`, `AccountWiring.cs`, `AccountPaths.cs`, `ResolvedAccount.cs`, `AccountResolution.cs`, `AccountRefresh.cs`, `IAccountProbe.cs`, `ProbeResult.cs`.
- `Harness/Claude/`: `ClaudeStatusLine.cs` (wiring), `ClaudeStatusLineCommand.cs` (guard text and ownership test), `ClaudeStatusLineReading.cs` (payload to observation, freshness), `ClaudeIdentityProbe.cs`.
- `Harness/Codex/`: `CodexRateLimits.cs` (snapshot parsing, both wire spellings), `CodexAccountProbe.cs` (minimal app-server JSON-RPC over stdio).

CLI (`src/Capacitor.Cli/`): `Commands/Harness/ClaudeStatusLineEntry.cs` (the early-dispatched command), `Program.cs`, `Commands/AccountsCommand.cs` (refresh verb, remove deletes reading), `Commands/UninstallCommand.cs` (unchanged behaviour; verify status line unwire), `Harness/Codex/CodexRolloutReadings.cs`, `Commands/WatchCommand.cs`, `Commands/CommandServices.cs`.

Daemon (`src/Capacitor.Cli.Daemon/`): `Services/AccountReadingSink.cs`, `Services/AccountRefresher.cs`, `Services/AccountsPublisher.cs`, `Services/AgentOrchestrator.cs` (limit hit), `Harness/Codex/CodexAppServerHostedAgentRuntime.cs`, `DaemonRunner.cs`.

Remote models: `src/Capacitor.Remote.Models/ListedAccountDto.cs` and its JSON context.

Docs: `README.md`, `src/Capacitor.Cli.Core/Resources/help-*.txt`.

---

### Task 1: Reading model and merge

**Files:**
- Create: the `Accounts/Readings/` DTOs above, `ReadingMerge.cs`, `ReadingObservation.cs`, `LimitSignal.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/Readings/ReadingMergeTests.cs`, `BlockedMarkerTests.cs`

**Interfaces:**
- Produces (all `public sealed record`, snake_case on the wire through the contexts of Tasks 2 and 8):
  - `UsageWindowDto(string LimitId, int WindowMinutes, double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset? ConfirmedAt)`; the key is `(LimitId, WindowMinutes)`. Claude uses `LimitId` `"claude"` with 300 and 10080.
  - `CreditsDto(bool HasCredits, bool Unlimited, string? Balance)`
  - `BlockConstraintDto(string? LimitId, int? WindowMinutes, DateTimeOffset? ResetsAt, DateTimeOffset SignaledAt)`; a null `ResetsAt` is the unknown constraint.
  - `BlockedMarkerDto(DateTimeOffset? BlockedAt, DateTimeOffset? ClearedAt, BlockConstraintDto[] Constraints)`
  - `AccountReadingDto(long Generation, UsageWindowDto[] Windows, string? Plan, CreditsDto? Credits, BlockedMarkerDto? Blocked)`
  - `LimitSignal(string? LimitId, int? WindowMinutes, bool Unbounded = false)`: both set names one window; only `LimitId` names a limit; both null names nothing. `Unbounded` is a block no quota reset lifts (Codex credit depletion, a workspace spend cap): it always adds the unknown constraint.
  - `ReadingObservation(DateTimeOffset ProducedAt, bool Fresh, IReadOnlyList<UsageWindowDto> Windows, string? Plan, CreditsDto? Credits, IReadOnlyList<LimitSignal> Limits, bool LimitReachedReported, bool LimitStateKnown)`; `Windows` carry no `ConfirmedAt` (the merge sets it). One observation is one vendor response, however many limit ids it covers, so a complete response can clear. `LimitReachedReported` is any Codex reached type in the response being non-null; Claude producers pass false. `LimitStateKnown` says the response states the limit state outright rather than omitting it: true for a Claude status line payload, a rollout `token_count` snapshot (it always writes `rate_limit_reached_type`, null included) and an `account/rateLimits/read` response; false for the sparse `account/rateLimits/updated` notification, whose missing fields mean unavailable, not cleared.
  - `static class ReadingMerge`:
    - `AccountReadingDto Apply(AccountReadingDto held, ReadingObservation observation)`
    - `AccountReadingDto Merge(AccountReadingDto held, AccountReadingDto incoming)` (snapshot merge; same generation assumed, the caller handles generation)
    - `bool IsBlocked(AccountReadingDto reading, DateTimeOffset now)`
    - `DateTimeOffset? BlockedUntil(AccountReadingDto reading)` (latest window reset, null when any constraint is unknown)
    - `AccountReadingDto Empty(long generation)`

Rules the implementation must follow (spec 5.1, 5.4):
- Window merge per key: incoming later `ResetsAt` replaces; same keeps the higher `UsedPercent`; earlier is ignored. A non-null reset beats a null one; two nulls keep the higher percentage.
- `ConfirmedAt`, one rule for `Apply` and `Merge`: when the window is the same window (equal `ResetsAt`, both null included) it is the maximum of both sides; when a later reset replaces the window it is the incoming side's value, null included, because a confirmation of the previous window says nothing about the new one; an ignored stale window changes nothing.
- `Apply` stamps `ConfirmedAt = ProducedAt` on each observed window only when `Fresh`; a replay with an equal value changes nothing; a fresh equal value moves `ConfirmedAt` forward.
- Windows the observation does not mention are kept (sparse updates never clear).
- `Plan` and `Credits` take the observation's non-null value; an observation with no windows still records them.
- Limit signals: each signal adds constraints — an `Unbounded` signal: one unknown constraint, whatever the windows show; a named window with its reset (observed, else held); a named limit: that limit's windows at or above 100%, else that limit's only window when it has exactly one, else one unknown constraint; a signal naming nothing: every window at or above 100%, else one unknown constraint. `SignaledAt = ProducedAt`. A signal at or before `ClearedAt` adds nothing. `BlockedAt = max(BlockedAt, ProducedAt)` otherwise.
- Clearing: a `Fresh`, `LimitStateKnown` observation with `ProducedAt > BlockedAt` and `ProducedAt > ClearedAt`, `LimitReachedReported == false`, no `Limits`, at least one window, covering every window key the held reading has, all below 100%, sets `ClearedAt = ProducedAt` and drops constraints with `SignaledAt <= ClearedAt`. An observation with no windows never clears.
- `Apply` ends by normalizing the marker with the merge rule below, so `Apply` and `Merge` keep the same monotonic evidence.
- Marker merge: max `BlockedAt`, max `ClearedAt`, union of constraints with `SignaledAt > ClearedAt` (dedupe by all fields).
- `IsBlocked`: `BlockedAt > (ClearedAt ?? MinValue)` and any constraint is unknown or has `ResetsAt > now`.

- [ ] **Step 1: Write the failing tests** — one test per spec 5.5 "Readings" and "Limit hit" bullet that is pure merge logic:
  - `Fresh_equal_value_moves_confirmedAt` / `Replay_equal_value_changes_nothing` / `Replay_may_raise_percentage_without_moving_confirmedAt`
  - `Lower_value_same_reset_is_ignored`, `Later_reset_replaces_window`, `Earlier_reset_is_stale`
  - `Several_limit_ids_coexist`, `Sparse_observation_keeps_unmentioned_windows`
  - `Snapshot_merge_twice_is_idempotent_and_never_restamps` (merge A into B, then again; equal result)
  - `Block_before_any_reading_is_unknown_and_blocked`
  - `Two_exhausted_windows_block_until_the_later_reset`
  - `Partial_newer_observation_does_not_clear`
  - `Unknown_block_survives_time_and_a_replayed_all_below_observation`
  - `Fresh_complete_all_below_observation_clears`, `Passing_the_last_reset_unblocks`
  - `Primary_then_secondary_signals_accumulate` and the reverse order: passing the earlier reset keeps it blocked
  - `Block_and_clear_merged_in_either_order_converge` (merge(block, clear) equals merge(clear, block) and `IsBlocked` matches)
  - `Rewound_older_all_below_observation_after_a_newer_block_does_not_clear`
  - `Older_snapshot_replayed_after_expiry_stays_unblocked`
  - `Unknown_constraint_with_expired_window_constraints_stays_blocked`
  - `Explicit_signal_blocks_below_100_percent`
  - `Late_older_clear_never_moves_clearedAt_back` (block t10, clear t30, fresh all-below t20, then merge a block snapshot of t25: still cleared at t30, not blocked)
  - `Replayed_old_signal_after_clear_cannot_poison_a_later_block` (clear t30, unknown signal t20, named block t40: one constraint, the named one)
  - `Named_limit_signal_constrains_only_that_limit`, `Named_window_below_100_uses_its_reset`
  - `Metadata_only_observation_records_plan_and_never_clears` (unknown block held, observation with plan and credits and no windows)
  - `Limit_only_observation_still_blocks`
  - `Unbounded_signal_stays_blocked_past_every_window_reset` until a known-state clearing observation
  - `Sparse_notification_covering_every_window_never_clears` (held unknown block; an observation with `LimitStateKnown == false`, all windows below 100%; then a known-state one clears)
  - `Complete_multi_limit_response_clears_an_unknown_block` (held limits A and B, one observation covering both below 100%)
  - `Replacement_by_later_reset_takes_incoming_confirmation` (null and older incoming `ConfirmedAt`), `Same_window_keeps_max_confirmation`, and both orderings of a null against a non-null reset
- [ ] **Step 2: Run** `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ReadingMergeTests/*"`; expected: build fails (types missing).
- [ ] **Step 3: Implement** the records and `ReadingMerge` in the files listed.
- [ ] **Step 4: Run** both test classes; expected: all pass.
- [ ] **Step 5: Commit** `Add the account reading model and its merge (#1956)`

### Task 2: Reading store, registry fields and removal

**Files:**
- Create: `Accounts/Readings/ReadingStore.cs`, `ReadingFile.cs`, `ReadingSession.cs`, `ReadingsJsonContext.cs`, `Accounts/StatusLineBackup.cs`
- Modify: `Accounts/VendorAccount.cs`, `Accounts/AccountStore.cs`, `Accounts/AccountRegistryJsonContext.cs`, `src/Capacitor.Cli/Commands/AccountsCommand.cs` (`Remove`), `src/Capacitor.Cli/Commands/PluginCommand.cs` (any path that drops a registry entry)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/Readings/ReadingStoreTests.cs`, additions to `AccountStoreTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/AccountsCommandTests.cs` (or the existing accounts command test class)

**Interfaces:**
- Consumes: Task 1 records.
- Produces:
  - `VendorAccount` trailing fields (JSON names in brackets): `Generation` long = 0 [`generation`], `RetiredSessions` string[]? [`retired_sessions`], `StatusLine` `StatusLineBackup?` [`status_line`], plus the Task 5 identity fields declared here so the record changes once: `Email`, `OrgId`, `OrgName`, `Plan` string? [`email`, `org_id`, `org_name`, `plan`], `VendorAccountId` string? [`vendor_account_id`] (Codex `chatgptAccountId` as `account/rateLimits/read` reports it in `accountId`), `SignIn` string? [`sign_in`], `ProbeSeq` long [`probe_seq`], `CommittedProbeSeq` long [`committed_probe_seq`], `RefreshedAt` DateTimeOffset? [`refreshed_at`], `RefreshError` string? [`refresh_error`], `LimitsError` string? [`limits_error`], `LabelSetByUser` bool? [`label_set_by_user`]. A null `LabelSetByUser` (every slice-3 entry) counts as user-set unless the label equals `Path.GetFileName` of the directory, the default `kcap accounts add` gave it; rename writes true, and a refresh that replaces the label with the email writes false.
  - `StatusLineBackup(string? Original)` [`original`]: null property on the account means no backup; `Original == null` means the backup is "none".
  - `ReadingSession(string SessionId, long? ApiDurationMs)`; `ReadingFile(AccountReadingDto Reading, ReadingSession[] Sessions, DateTimeOffset[]? RefreshOrigins = null)`. `RefreshOrigins` holds the `ProducedAt` of the last 16 observations a refresh merged, written in the same atomic replace as the merge, so every daemon on the host can tell refresh-produced block evidence from an agent's (Task 7). Local only; never published.
  - `AccountStore`:
    - `ReadingStore Readings { get; }` (directory `readings/` beside `accounts.json`, lock file `readings.lock`)
    - `(T Result, long Revision) MutateWithReadings<T>(Func<AccountRegistry, HeldReadings, (AccountRegistry Next, T Result)> change, Action<AccountRegistry, HeldReadings>? afterSave = null)` — takes `accounts.lock`, then `readings.lock`, loads the registry, runs `change`, saves when `Next` is a new reference, runs `afterSave` with the saved registry, and releases both. Both locks are held from the first read to the last write, so no producer can commit between collecting a reading's sessions and retiring them. `change` only reads (`held.Read`) and computes the next registry; every reading delete or write happens in `afterSave`, which does not run when the save throws, so a failed registry save leaves the readings as they were.
    - `(T Result, long Revision) MutateHeld<T>(change)` — the plain registry save without taking `accounts.lock`; the caller holds `Lock()` (status line backups during wiring)
    - `VendorAccount? FindHeld(string id)` — a fresh read by id for a caller holding `Lock()`
  - `HeldReadings` (constructed only inside `MutateWithReadings`): `ReadingFile Read(string accountId, long generation)` — missing, unparseable, or of another generation reads as `ReadingFile(Empty(generation), [])`, the same normalization as `TryRecord`; `void Write(string accountId, ReadingFile file)`; `void Delete(string accountId)`.
  - `ReadingStore`:
    - `IDisposable? TryLock(TimeSpan budget)` — null on contention (budget 50 ms for producers)
    - `bool TryRecord(string accountId, long producerGeneration, string? sessionId, Func<ReadingFile, ReadingFile> update)` — takes `TryLock(50 ms)`; reads `accounts.json` without `accounts.lock`; writes nothing when the account is gone, its generation differs from `producerGeneration`, or `sessionId` is in `RetiredSessions`; otherwise loads the file — missing, unparseable, or of another generation than the validated one all read as `ReadingFile(Empty(generation), [])` — applies `update`, and replaces atomically. False when nothing was written. Never throws for IO, lock or parse failures. Must not be called while holding `readings.lock` (not re-entrant): code inside `MutateWithReadings` uses `HeldReadings`.
    - `IReadOnlyDictionary<string, ReadingFile> LoadAll()` — skips unreadable files
    - `(long Length, long Ticks)[] Stat()` — for the publisher's change detection
    - Seam: `internal Action? AfterMembershipCheck { get; set; }`
  - Removal (`kcap accounts remove`) uses `MutateWithReadings` and deletes the reading in `afterSave`.

- [ ] **Step 1: Write the failing tests:**
  - `Registry_entry_without_new_fields_loads_with_generation_zero` (a slice-3 `accounts.json` literal)
  - `TryRecord_writes_owner_only_file_and_directory` (unix modes 0600/0700)
  - `TryRecord_skips_an_unregistered_account`, `TryRecord_skips_a_stale_generation`, `TryRecord_skips_a_retired_session`
  - `TryRecord_skips_when_the_lock_is_held` (hold `Lock()` on another thread; returns false within 1 s)
  - `Writer_paused_after_membership_while_removal_runs_does_not_recreate_the_file` (use `AfterMembershipCheck` to run a removal on another thread, which must block on `readings.lock` and then delete; the file is absent at the end)
  - `Unparseable_reading_file_is_replaced_by_the_next_observation`
  - `Reading_of_an_older_generation_is_reset_by_the_next_observation` (registry saved at generation 2 with the generation-1 file left behind, as after a crash between save and delete)
  - `Legacy_label_counts_as_user_set_unless_it_is_the_directory_name`
  - `Remove_deletes_the_reading` (command-level, through `AccountsCommand`)
- [ ] **Step 2: Run** `--treenode-filter "/*/*/ReadingStoreTests/*"`; expected: compile failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `ReadingStoreTests`, `AccountStoreTests` and the accounts command tests; all pass.
- [ ] **Step 5: Commit** `Store per-account readings beside the registry (#1956)`

### Task 3: Claude status line wiring

**Files:**
- Create: `Harness/Claude/ClaudeStatusLine.cs`, `Harness/Claude/ClaudeStatusLineCommand.cs`
- Modify: `Accounts/AccountWiring.cs` (Claude wire and unwire add a `statusline` step), every `AccountWiring.Wire`/`Unwire` call site (`AccountsCommand`, `AccountSetupStep`, `PluginCommand`, uninstall), and the default-directory paths that bypass `AccountWiring`: `PluginCommand.InstallClaudeLayout` / `RemoveClaudeLayout` (the `UserAccountsAsync` filter excludes the default) and `AccountSetupStep`'s loop, which skips the environment default. Each calls `AccountWiring.WireStatusLine` / `UnwireStatusLine` for the registered default under `accounts.lock`, so setup, `plugin --if-installed`, `plugin remove` and uninstall all cover it. Install runs that step after adoption, not inside `InstallClaudeLayout`: in `PluginCommand.InstallClaude` after `WireUserAccountsAsync` (which adopts the default), only for user scope and only when the default layout was installed (not `skipDefault`); in `AccountSetupStep` after `EnsureDefault`. Remove runs it before `RemoveClaudeLayout`, while the default is still registered.
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeStatusLineTests.cs`, `ClaudeStatusLineCommandTests.cs`, additions to `Accounts/AccountWiringTests.cs`

**Interfaces:**
- Consumes: `StatusLineBackup`, `AccountStore.MutateHeld` (Task 2).
- Produces:
  - `static class ClaudeStatusLineCommand`:
    - `string For(string accountId, string? original, bool posixShell)` — posix: `if command -v kcap >/dev/null 2>&1; then exec kcap statusline --account <id>; else exec sh -c '<original quoted>'; fi`, with `:` as the else branch when `original` is null; quoting is POSIX single quotes with each `'` written as `'\''`. Not posix: `kcap statusline --account <id>`.
    - `bool IsOurs(string? command)` — true for either shape produced by `For`, any account id.
    - `bool PosixShell(Func<string, string?> env)` — true off Windows; on Windows true when Git Bash is found (`CLAUDE_CODE_GIT_BASH_PATH` names an existing file, or `git.exe` on PATH has `..\bin\bash.exe` beside it).
  - `static class ClaudeStatusLine`:
    - `SettingsEdit Install(string settingsPath, string accountId, StatusLineBackup? backup, Func<StatusLineBackup, bool> saveBackup, bool posixShell)` — reads the current `statusLine.command`; if it is ours, rewrites only when the id differs and returns; otherwise calls `saveBackup(new(currentOrNull))` first and writes ours only when it returned true. Owns `command` and `type` (`"command"`) only.
    - `(SettingsEdit Edit, bool BackupMissing) Remove(string settingsPath, StatusLineBackup? backup)` — while ours: restore the original, or remove `command` and `type` and an emptied `statusLine`; a missing backup removes `command`/`type` and reports `BackupMissing`. When not ours: no edit.
  - `AccountWiring.Wire(VendorAccount account, UserHome home, WiringOptions options, AccountStore store)` and `Unwire(VendorAccount account, UserHome home, AccountStore store)`; both run under the caller's held `accounts.lock`. The account a caller passes may have been resolved before the lock, so the status line step re-reads it with `store.FindHeld(account.Id)` and works from that entry's `StatusLine`; an absent entry gives a failed `statusline` step ("account removed") and no settings edit. Backups are saved or cleared with `store.MutateHeld`. Unwire discards the backup on success, keeps it on failure.
  - `AccountWiring.WireStatusLine(string accountId, UserHome home, AccountStore store) : WiringStep` and `UnwireStatusLine(string accountId, UserHome home, AccountStore store) : WiringStep` — the Claude status line step alone, for the default-directory paths named under Files; same held-lock contract.
  - `JsonSettingsFile.Edit` stays the only writer: a non-object `statusLine` or a non-string `command` returns `SettingsEdit.Malformed` from the edit callback path (add a way for the callback to refuse, e.g. throw a private exception mapped to `Malformed`).

- [ ] **Step 1: Write the failing tests:**
  - `Install_then_unwire_round_trips_settings` — `JsonSettingsFile` re-serializes indented, so the criterion is semantic JSON equality (`JsonNode.DeepEquals`) with the original, property order and every unrelated key and `padding` preserved; the input fixture is compact, single-line JSON with an escaped character so the test cannot pass by already matching the serializer's format
  - `Stale_account_snapshot_unwire_uses_the_current_backup` (resolve the account, then rewire with a different original through another `AccountStore` before the unwire takes the lock; the unwire restores the newer original)
  - `Default_directory_gets_the_wrapper_from_setup_and_plugin_refresh` and `Plugin_remove_and_uninstall_restore_the_default_original` (command-level, through `PluginCommand` and `AccountSetupStep` with the environment default registered)
  - Starting from an empty registry: `Fresh_user_install_adopts_and_wraps_the_default`, `If_installed_upgrade_of_a_pre_registry_install_adopts_and_wraps_the_default`, `If_installed_with_the_default_not_installed_wraps_nothing`, `Project_scope_install_wraps_nothing`
  - `Install_with_no_original_records_none_and_unwire_removes_statusLine`
  - `User_edit_then_rewire_backs_up_the_edit_and_reinstalls`
  - `Rewire_with_ours_installed_changes_nothing`
  - `User_edit_then_unwire_keeps_the_edit_and_discards_backup`
  - `Padding_edit_with_ours_installed_survives_unwire`
  - `Our_command_is_never_adopted_as_original`
  - `Missing_backup_with_ours_installed_removes_command_and_reports`
  - `Malformed_settings_left_untouched_and_account_reports_broken`
  - `Non_object_statusLine_is_refused_unchanged` (Review Focus 1)
  - `Interrupted_install` — four cases with `FaultPoints`: before the backup save, after backup and before the settings write, during the settings write (`rename:` fault), after; each asserts unrelated settings intact and that `rewire` completes it.
  - `Codex_config_toml_stays_owner_only_after_rewrite` — confirm the existing `RegisterKcapMcpServers_writes_owner_only_files_on_unix` still passes; no new test.
  - Guard tests (unix only, real `/bin/sh`; use `sh -n` and `sh -c`, never a copied coreutil): `Guard_parses_with_sh_n` and `Guard_runs_the_original_when_kcap_is_absent` for originals with a trailing `# comment`, single quotes, multiple lines, and a heredoc; PATH set to a `TempDir` with no `kcap`.
- [ ] **Step 2: Run** the new classes; expected: compile failure.
- [ ] **Step 3: Implement**, then thread `store` through the call sites.
- [ ] **Step 4: Run** `ClaudeStatusLineTests`, `ClaudeStatusLineCommandTests`, `AccountWiringTests`, the accounts/plugin/uninstall command tests; all pass.
- [ ] **Step 5: Commit** `Wrap each Claude account's status line for readings (#1956)`

### Task 4: `kcap statusline`

**Files:**
- Create: `Harness/Claude/ClaudeStatusLineReading.cs` (Core), `src/Capacitor.Cli/Commands/Harness/ClaudeStatusLineEntry.cs`
- Modify: `src/Capacitor.Cli/Program.cs`, `README.md`, `src/Capacitor.Cli.Core/Resources/help-*.txt`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudeStatusLineReadingTests.cs`, `test/Capacitor.Cli.Tests.Integration/StatusLineWrapperTests.cs`

**Interfaces:**
- Consumes: `ReadingStore.TryRecord`, `ReadingMerge.Apply`, `ClaudeStatusLineCommand.PosixShell`.
- Produces:
  - `static class ClaudeStatusLineReading { ReadingFile? Apply(ReadingFile held, JsonElement payload, DateTimeOffset now); }` — null when the payload has no `rate_limits`. Windows: `five_hour` → (`claude`, 300), `seven_day` → (`claude`, 10080); `used_percentage`, `resets_at` epoch seconds. Fresh only when `cost.total_api_duration_ms` exceeds the session's recorded baseline; the first sighting records the baseline and merges not-fresh; a payload without the counter merges its windows not-fresh and records or moves no baseline. Observations carry `LimitStateKnown: true` and no limit signals. Sessions keyed by `session_id`; cap the list at 64, dropping the oldest.
  - `static class ClaudeStatusLineEntry { Task<int> RunAsync(string[] args, AccountStore accounts, CancellationToken ct); }`:
    1. Reads stdin to end as bytes.
    2. If `--account <id>` names a registered Claude account and stdin parses as a JSON object with `rate_limits`, calls `TryRecord(id, account.Generation, sessionId, f => ClaudeStatusLineReading.Apply(f, payload, now) ?? f)`. Every exception is swallowed.
    3. If the account's `StatusLine?.Original` is non-null, runs `sh -c <original>` (argument vector), or `powershell -NoProfile -Command <original>` when not posix, with the same stdin bytes, inherited environment and working directory; copies stdout and stderr through and returns its exit code. Otherwise prints nothing and returns 0.
    4. On SIGTERM, SIGINT or SIGHUP (`PosixSignalRegistration`) kills the child's tree and exits.
  - `Program.cs`: `if (command == "statusline") return await ClaudeStatusLineEntry.RunAsync(args, AccountStore.Beside(DaemonStore.FromEnvironment()), CancellationToken.None);` placed before `NestedHarness.ShedInheritedHosting()`.

- [ ] **Step 1: Write the failing unit tests** (`ClaudeStatusLineReadingTests`): `First_run_records_baseline_and_leaves_freshness_unknown`, `Unchanged_repeat_after_baseline_stays_unknown`, `Api_duration_increase_confirms`, `Repeated_payload_with_unchanged_counter_does_not_move_confirmedAt`, `New_response_with_equal_values_moves_confirmedAt`, `Payload_without_counter_keeps_its_windows_but_is_never_fresh` (percentages and resets stored, `ConfirmedAt` null, no baseline), `Missing_rate_limits_writes_nothing`, `One_window_only_merges_that_window`, `Two_sessions_keep_separate_baselines` (Review Focus 2).
- [ ] **Step 2: Write the failing integration tests** (`StatusLineWrapperTests`, spawning the real binary through `KcapProcess` with a `TempDaemonPaths` store, no server config, and `HTTPS_PROXY`/`HTTP_PROXY`/`ALL_PROXY` pointed at a closed local port; mark `[ParallelLimiter<SubprocessLimit>]`):
  - `Reading_merged_from_stdin` (reading file appears with both windows)
  - `Original_gets_identical_stdin_env_and_cwd_and_passes_output_and_exit_code` (original is `sh` script written by `tmp.CreateExecutable` that echoes stdin, an env marker and `pwd`, writes to stderr, exits 3)
  - `No_original_prints_nothing_and_exits_zero`
  - `Held_readings_lock_skips_capture_and_original_still_prints_promptly` (hold `readings.lock` from the test through `Readings.TryLock`; wrapper finishes under 2 s)
  - `Slow_original_is_killed_when_the_wrapper_is_terminated` (original `sleep 30` via `sh`; SIGTERM the wrapper; `PidIdentity` of the child reports gone)
  - `Empty_or_non_json_stdin_still_runs_the_original` (Review Focus 5)
  - `No_network_call` — the proxy port records no connection
- [ ] **Step 3: Run** both classes; expected: failures.
- [ ] **Step 4: Implement**; add `kcap statusline` to README (`## CLI commands`, noting it is installed by wiring and not run by hand) and to the help text.
- [ ] **Step 5: Run** both classes; all pass.
- [ ] **Step 6: Commit** `Record Claude usage readings from the status line (#1956)`

### Task 5: Identity probe and refresh

**Files:**
- Create: `Accounts/IAccountProbe.cs`, `Accounts/ProbeResult.cs`, `Accounts/AccountIdentity.cs`, `Accounts/SignInStates.cs`, `Accounts/AccountRefresh.cs`, `Harness/Claude/ClaudeIdentityProbe.cs`, `Harness/Codex/CodexAccountProbe.cs`, `Harness/Codex/CodexRateLimits.cs`
- Modify: `src/Capacitor.Cli/Commands/AccountsCommand.cs` (`refresh [<id>]`, `add` refreshes after wiring, `rename` sets `LabelSetByUser`), `README.md`, help text
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`, `Harness/Claude/ClaudeIdentityProbeTests.cs`, `Harness/Codex/CodexAccountProbeTests.cs`, `Harness/Codex/CodexRateLimitsTests.cs`, accounts command tests

**Interfaces:**
- Consumes: Task 1 merge, Task 2 store and fields.
- Produces:
  - `AccountIdentity(string? Email, string? OrgId, string? OrgName, string? Plan, string? VendorAccountId)` with `static bool Differs(HarnessId vendor, AccountIdentity recorded, AccountIdentity probed)`. Claude: differs when `OrgId` or `Email` differ. Codex (`account/read` has no account id, `account/rateLimits/read` carries it as `accountId`): when both sides carry a `VendorAccountId`, differs exactly when the ids differ (the id is the stable identity; Codex may return `email: null`); otherwise, when both sides carry an email, differs when the emails differ; otherwise does not differ, since a missing field is not evidence of another login. Committed fields: a field the probe returned replaces the recorded one; a field the probe left null keeps the recorded value unless the identity differs, in which case it is cleared, so a login change never commits beside the previous login's id or email. Never differs while the recorded side has no email and no id (first probe).
  - `ProbeResult(string? SignIn, AccountIdentity? Identity, string? IdentityError, ReadingObservation? Limits, string? LimitsError)` — `SignIn` is `signed_in` / `signed_out` from an authoritative answer, else null (left unchanged as `unknown`).
  - `interface IAccountProbe { Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct); }`
  - `ClaudeIdentityProbe(HarnessRegistry harnesses, UserHome home, TimeProvider time) : IAccountProbe` — runs `claude auth status --json` with the account's `CLAUDE_CONFIG_DIR` plan from `VendorHomeVariable.Plan`, 10 s timeout; `loggedIn`, `email`, `orgId`, `orgName`, `subscriptionType`. No limits (Claude readings come from the wrapper).
  - `CodexAccountProbe(HarnessRegistry harnesses, UserHome home, TimeProvider time) : IAccountProbe` — starts `codex app-server` with `CODEX_HOME` planned the same way, cwd the system temp directory, 10 s overall; sends `initialize` (`clientInfo` name `kcap`), then `account/read` (`{}`), then `account/rateLimits/read` (`{"excludeResetCreditDetails":true}`); answers any server-to-client request with a JSON-RPC error; kills the process at the end. `account` null with `requiresOpenaiAuth` true is `signed_out`; a `chatgpt` account gives email and `planType`; an error response for one method fills that method's error and leaves the other's result.
  - `static class CodexRateLimits`:
    - `ReadingObservation? FromRollout(JsonElement rateLimits, DateTimeOffset producedAt)` — snake_case shape (`limit_id`, `primary`/`secondary` with `used_percent`, `window_minutes`, `resets_at`, `plan_type`, `credits{has_credits,unlimited,balance}`, `rate_limit_reached_type`)
    - `ReadingObservation? FromAppServer(JsonElement snapshot, DateTimeOffset producedAt)` — camelCase (`limitId`, `usedPercent`, `windowDurationMins`, `resetsAt`, `planType`, `credits{hasCredits,unlimited,balance}`, `rateLimitReachedType`)
    - both: `Fresh = true`; a null `limitId` maps to `"codex"`; a window without a duration is skipped; null when the snapshot is null or reports no window, plan, credits, reached type or true spend-control flag (so a both-null `primary`/`secondary` with a plan still records the plan, and records nothing else). A non-null reached type sets `LimitReachedReported`. `rate_limit_reached` adds `new LimitSignal(limitId, null)`: it names no window, so the signal names the snapshot's limit and Task 1's named-limit rule picks the window (the one at 100%, else the limit's only window, else unknown). Every other value (the `workspace_*_credits_depleted` and `workspace_*_usage_limit_reached` spend-cap reasons, or one this build does not know) adds `new LimitSignal(limitId, null, Unbounded: true)`, since no quota reset lifts it. A non-null `spendControlReached`/`spend_control_reached` of true does the same. `LimitStateKnown` is true from `FromRollout` and false from `FromAppServer`. From `FromRateLimitsRead` it follows the response's `ordinaryUsageAllowed`: true only when it is `true`; `null` (unavailable, which Codex also returns when the usage identity does not match the active account) gives false; `false` gives false and is denial evidence: when the response also carries a reached type, that type's own mapping above is the signal (a `rate_limit_reached` denial keeps its finite window constraint and lapses at that reset; credit and spend-cap reasons stay `Unbounded`); with no reached type it adds `new LimitSignal(null, null)`, which Task 1 turns into the windows at or above 100% or, when none is, one unknown constraint.
    - `ReadingObservation? FromRateLimitsRead(JsonElement result, DateTimeOffset producedAt)` — one observation for the whole response: the windows and signals of every entry of `rateLimitsByLimitId` when present, else of `rateLimits`, so a complete all-below response can clear.
    - `string? AccountIdFromRateLimitsRead(JsonElement result)` — the response's `accountId`.
  - `static class AccountRefresh { Task<RefreshOutcome> RunAsync(AccountStore store, string accountId, IAccountProbe probe, TimeProvider time, CancellationToken ct); }` with `RefreshOutcome(bool Found, IReadOnlyList<WiringStep> Steps, long Revision)` (steps `identity` and, for Codex, `limits`). Algorithm (spec 3.2):
    1. Under `accounts.lock`: `ProbeSeq += 1`; remember `seq`, `generation`.
    2. Probe outside every lock.
    3. Commit with `MutateWithReadings` (both locks held throughout). In `change`: always stamp `RefreshedAt` and bump the revision. If `seq <= CommittedProbeSeq` (a newer probe committed first), stop there: no identity, error or limits change. Otherwise set `CommittedProbeSeq = seq`, then `RefreshError` from the identity step and `LimitsError` from the limits step (cleared on success). If the identity step answered: set `SignIn` and the identity fields; when `AccountIdentity.Differs`, `Generation += 1` and append the session ids of `held.Read(id, oldGeneration)` to `RetiredSessions` (cap 256, oldest dropped); replace a label that is not user-set with the email. In `afterSave`: on a generation change `held.Delete(id)`; then, when the probe returned limits and the generation is now either the one the identity commit produced or, for a failed identity step, still `generation`, `held.Write(id, …)` merging them into `held.Read(id, currentGeneration)` and appending the observation's `ProducedAt` to `RefreshOrigins` in that same write. A superseded or dropped probe writes no origin.
  - `kcap accounts refresh [<id>]`: refreshes one or every account; text output per account and `--json` as `AccountActionResultDto` with action `refresh` and the committed revision. `kcap accounts add` refreshes after its wiring commit; its result reports the wiring steps plus the refresh steps and the later revision.

- [ ] **Step 1: Write the failing tests:**
  - `CodexRateLimitsTests`: the rollout line captured from a real Codex 0.164 session (single `codex` window of 10080 minutes, credits, plan `pro`) as a literal; both windows; several limit ids; credits; limit reached with a window at 100% and without; null `rate_limits`, and both windows null (Review Focus 3); the app-server camelCase shape; `rateLimitsByLimitId` preferred over `rateLimits`.
  - `ClaudeIdentityProbeTests` (stub `claude` executable from `tmp.CreateExecutable` printing canned JSON and recording its `CLAUDE_CONFIG_DIR`): signed in, signed out, crash (non-zero, no JSON) gives an error and no sign-in, `CLAUDE_CONFIG_DIR` removed for `~/.claude` and set for another directory.
  - `CodexAccountProbeTests` (stub app-server script replying line by line): identity and limits; `account/read` success with limits failure; unsupported method (JSON-RPC `-32601`) for `account/rateLimits/read`; a server request mid-stream is answered with an error and the probe still completes; no reply within the deadline gives errors on both steps.
  - `AccountRefreshTests` (fake `IAccountProbe` with controllable completion): `First_probe_records_identity_without_bumping_generation`; `Login_change_bumps_generation_deletes_reading_and_retires_sessions`; `Single_refresh_discovering_login_B_leaves_B_identity_and_B_limits`; `Overlapping_refreshes_completing_in_reverse_order_leave_the_newer_login`; `Superseded_refresh_drops_its_limits`; `Failed_probe_sets_error_and_keeps_identity_and_reading`; `Failed_probe_merges_limits_only_under_unchanged_generation`; `Limits_failure_records_limits_error_and_keeps_identity`; `Unchanged_refresh_still_bumps_revision_and_stamps_refreshedAt`; `Retired_session_late_observation_is_dropped` (TryRecord after the bump); `Producer_committing_just_before_retirement_has_its_session_retired` (a producer holding `readings.lock` writes session S while the refresh waits; S ends up in `RetiredSessions`); `User_label_survives_identity`; `Legacy_renamed_label_survives_the_first_refresh` (slice-3 registry literal); `Superseded_probe_leaves_errors_as_the_newer_probe_set_them` (reversed completion for success-then-error and error-then-success, and identity success with a limits error); `Same_email_different_codex_account_id_bumps_generation`; `Codex_probe_without_account_id_compares_emails_only`; `Email_change_without_id_then_id_arrives_retires_once` (A/idA → B/no id → B/idB: never B with idA, generation bumped once); `Null_email_with_matching_id_is_not_a_login_change` (A/idX → null/idX → A/idX: no retirement, email A kept); `Refresh_merge_records_its_origin_and_a_superseded_probe_records_none`; `Failed_registry_save_leaves_reading_and_retirement_untouched` (fault at `rename:` of `accounts.json` during a login-change refresh); `Refresh_after_crash_resets_an_older_generation_reading` (the Task 2 crash fixture driven through `AccountRefresh.RunAsync`).
  - `CodexRateLimitsTests` additionally: `Reached_type_with_cached_percentages_below_100_constrains_the_limits_only_window`, `Reached_type_with_two_windows_at_different_resets_constrains_the_one_at_100`, `Both_windows_null_with_plan_records_only_the_plan`, `Rate_limits_read_with_two_limit_ids_is_one_observation`, `Credits_depleted_and_spend_cap_signals_are_unbounded` (fixtures for each, asserting the account stays blocked after the quota window resets), `App_server_notification_is_not_limit_state_known`, `Rate_limits_read_with_ordinary_usage_null_cannot_clear`, `Rate_limits_read_with_ordinary_usage_false_and_no_reason_blocks_unknown`, `Ordinary_usage_false_with_rate_limit_reached_unblocks_at_its_reset` (through `AccountRefresh.RunAsync`: blocked now, unblocked after the 100% window's reset with no further observation), `Ordinary_usage_false_with_credits_depleted_survives_the_reset`, `Spend_control_only_snapshot_blocks` (both wire spellings, every other field absent or null, no prior reading).
  - Accounts command: `refresh --json reports steps and revision`; `add` result carries the refresh revision.
- [ ] **Step 2: Run** the classes; expected: compile failure.
- [ ] **Step 3: Implement.** Document `kcap accounts refresh [<id>]` in README and help text.
- [ ] **Step 4: Run** the classes; all pass.
- [ ] **Step 5: Commit** `Probe account identity and limits on refresh (#1956)`

### Task 6: Codex rollout producer

**Files:**
- Create: `src/Capacitor.Cli/Harness/Codex/CodexRolloutReadings.cs`
- Modify: `Accounts/AccountPaths.cs` (account lookup), `src/Capacitor.Cli/Commands/WatchCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Harness/Codex/CodexRolloutReadingsTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountPathsTests.cs`

**Interfaces:**
- Consumes: `CodexRateLimits.FromRollout`, `ReadingStore.TryRecord`, `ReadingMerge.Apply`.
- Produces:
  - `AccountPaths.CodexAccountForRollout(string rolloutPath, AccountRegistry registry, UserHome home) : VendorAccount?` (the existing `CodexForRollout` keeps working, built on it).
  - `sealed class CodexRolloutReadings(ReadingStore store, VendorAccount account, string sessionId, TimeProvider time)` with `void Observe(string rawLine)` — handles only `event_msg` lines whose payload type is `token_count` and has `rate_limits`; `ProducedAt` is the line's `timestamp`; never throws. `static CodexRolloutReadings? For(AccountStore accounts, string rolloutPath, string sessionId, UserHome home, TimeProvider time)` — null when the rollout is in no registered account.
  - `WatchCommand`: for `vendor == "codex"`, resolve once at watcher start and call `Observe` for each line of `drainRead.Lines` beside the existing raw-line consumers.

- [ ] **Step 1: Write the failing tests:** fixtures as JSONL literals for primary only, both windows, several limit ids, credits, limit reached with and without a matching window, and a limit-reached event while cached percentages are below 100%, each asserting the stored reading's windows, `confirmedAt` equal to the line timestamp, `IsBlocked` and `BlockedUntil`; `Line_in_no_registered_account_records_nothing`; `Null_rate_limits_writes_nothing` (Review Focus 3); `Replayed_older_line_never_lowers_confirmedAt`.
- [ ] **Step 2: Run**; expected: failures.
- [ ] **Step 3: Implement** and wire into `WatchCommand`.
- [ ] **Step 4: Run** the classes plus `WatchCommandTests`; all pass.
- [ ] **Step 5: Commit** `Record Codex readings from rollout token counts (#1956)`

### Task 7: Daemon producers and refresh

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AccountReadingSink.cs`, `Services/AccountRefresher.cs`
- Modify: `Accounts/ResolvedAccount.cs` and `Accounts/AccountResolution.cs` (Core), `Harness/Codex/CodexHostedAgentRuntimeFactory.cs` (`StartAppServerAsync` passes `ctx.Account` and the sink into the runtime), `Harness/Codex/CodexAppServerHostedAgentRuntime.cs` (and `CodexAppServerLaunch` if that is where the runtime's construction arguments live), `Services/AgentOrchestrator.cs` (`NoteScreen`), `Services/AccountsPublisher.cs` (limit-hit refresh trigger), `DaemonRunner.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountReadingSinkTests.cs`, `AccountRefresherTests.cs`, additions to the app-server runtime and orchestrator usage-limit tests

**Interfaces:**
- Consumes: Tasks 1, 2, 5.
- Produces:
  - `ResolvedAccount(string Directory, string? AccountId, string Label, long Generation = 0)` — resolution copies the registry entry's generation; the resume ledger keeps its existing three fields, and a resume re-resolves (so the generation is current).
  - `sealed class AccountReadingSink(AccountStore? store, TimeProvider time, ILogger<AccountReadingSink> logger)` with `void Post(ResolvedAccount? account, ReadingObservation observation)` — non-blocking (bounded channel of 256, drop oldest); a background loop calls `TryRecord(account.AccountId, account.Generation, null, f => f with { Reading = ReadingMerge.Apply(f.Reading, observation) })`. Drops when `account?.AccountId` is null.
  - `sealed class AccountRefresher(...) : BackgroundService` — refreshes every registered account once at daemon start, sequentially, through `AccountRefresh.RunAsync` with the Task 5 probes; `void Request(string accountId)` queues one, coalescing a request for an account already queued or running.
  - Limit-hit refresh, one path for every producer (Claude menu, Codex rollout in the watcher process, app-server notification, refresh): the publisher, on each new snapshot, calls `AccountRefresher.Request` for every account whose reading's `Blocked.BlockedAt` advanced past the value it last saw. The publisher skips an advance whose `BlockedAt` is in that reading file's `RefreshOrigins`: the refresh wrote the origin in the same atomic replace as the block, so any daemon on the host reading the file sees both together, and no refresh — this daemon's or another's sharing the registry — triggers another. Every other request is honoured: one arriving within 10 minutes of that account's last refresh is kept as a single deferred request and run when the 10 minutes are up, never dropped.
  - The runtime receives `ResolvedAccount? Account` and `Action<ResolvedAccount?, ReadingObservation>? PostReading` as constructor arguments, passed by `CodexHostedAgentRuntimeFactory.StartAppServerAsync` from `ctx.Account` and the daemon's sink (the factory takes the sink through its own constructor, registered in `DaemonRunner`). `HandleNotification` adds `case "account/rateLimits/updated"` posting `CodexRateLimits.FromAppServer(params.rateLimits, receivedAt)`; posting never blocks the read loop.
  - `NoteScreen`: when `usageLimit` turns non-null for a Claude agent, posts `new ReadingObservation(now, Fresh: false, [], null, null, [new LimitSignal(null, null)], LimitReachedReported: false, LimitStateKnown: false)` for `agent.Account`; it does not wait.

- [ ] **Step 1: Write the failing tests:** `Posts_for_an_unregistered_default_record_nothing`; `Post_never_blocks_when_the_lock_is_held`; `App_server_rateLimits_update_lands_on_the_agents_account_not_the_default` (a runtime built by `CodexHostedAgentRuntimeFactory.StartAppServerAsync` with a stub app-server, two registered Codex accounts, the agent on the non-default one, asserting the captured account and its launch generation); `Notification_from_a_retired_generation_is_dropped`; `Claude_limit_menu_blocks_the_agents_account`; `Advanced_blockedAt_requests_one_refresh` (from a Codex rollout-written reading too); `Refresh_reporting_a_reached_limit_does_not_trigger_itself`; `Two_daemons_sharing_a_registry_settle_after_a_persistent_reached_limit` (two publisher and refresher pairs on one `AccountStore`, a probe that always reports a reached limit, several cooldown periods of fake time: the probe count stops growing after the initial refreshes); `Genuine_hit_right_after_startup_refresh_runs_once_the_cooldown_ends` (fake time, no further reading changes); `Duplicate_requests_coalesce`; `Refresher_probes_every_account_at_start`; `Refresh_failure_never_stops_the_daemon`.
- [ ] **Step 2: Run**; expected: failures.
- [ ] **Step 3: Implement** and register the sink and refresher in `DaemonRunner`.
- [ ] **Step 4: Run** the daemon classes touched; all pass.
- [ ] **Step 5: Commit** `Record readings and limit hits from hosted agents (#1956)`

### Task 8: Publishing the reading

**Files:**
- Modify: `Accounts/AccountViewDto.cs`, `Accounts/AccountSnapshotBuilder.cs`, `Accounts/AccountsSnapshotJsonContext.cs`, `src/Capacitor.Cli.Core/Models.cs` (hub context registrations for the new types), `src/Capacitor.Cli.Daemon/Services/AccountsPublisher.cs`, `src/Capacitor.Remote.Models/ListedAccountDto.cs` and its context, `test/fixtures/daemon-connect.v2.json` only if the connect fixture asserts the account shape
- Test: `AccountSnapshotBuilderTests.cs`, `AccountsSnapshotDtoTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountsPublisherTests.cs`, `test/Capacitor.Remote.Models.Tests.Unit/DaemonAccountsWireTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `AccountViewDto` trailing optional members: `string? Email, string? OrgId, string? OrgName, string? Plan, string? VendorAccountId, string? SignIn, long Generation = 0, DateTimeOffset? RefreshedAt, string? RefreshError, string? LimitsError, AccountReadingDto? Reading`. Never the status line backup, retired sessions or Claude baselines.
  - `AccountSnapshotBuilder.Build(registry, hostId, defaults, home, IReadOnlyDictionary<string, ReadingFile> readings)` — attaches `ReadingFile.Reading` only when its `Generation` equals the entry's.
  - `AccountsPublisher` republishes when the registry revision, the host id, or `Readings.Stat()` changes, and announces only when the built snapshot differs by value (same revision allowed); an unreadable reading file keeps the reading from the previous snapshot for that account (Review Focus 4). The 30-second trailing server throttle is unchanged.
  - `ListedAccountDto` (Remote.Models) gains the same trailing fields with a `ListedReadingDto` mirror of `AccountReadingDto`, so the app binds what the server will serve.

- [ ] **Step 1: Write the failing tests:** `Reading_of_a_retired_generation_is_omitted`; `Retired_account_without_reading_publishes_its_new_generation`; `Published_view_never_contains_the_original_command` (an original holding `SECRET-MARKER-123`; serialize the snapshot through both the local and the hub contexts and assert the marker is absent); `Reading_change_publishes_within_the_poll` (write a reading with no registry change; snapshot updates within 5 s of fake time); `Burst_of_reading_changes_sends_the_last_to_the_server` (trailing throttle); `Unreadable_reading_keeps_the_previous_one`; `Snapshot_from_slice_3_daemon_binds` and `New_snapshot_round_trips` (DTO tests); `ListedAccount_binds_reading_and_identity` and the older-server row binds nulls.
- [ ] **Step 2: Run**; expected: failures.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the classes; all pass.
- [ ] **Step 5: Commit** `Publish account identity and readings in the snapshot (#1956)`

### Task 9: Whole-branch verification

- [ ] **Step 1:** `dotnet build Capacitor.slnx`; zero errors and no new warnings.
- [ ] **Step 2:** `dotnet test --solution Capacitor.slnx`; read the summary counts. Known unrelated flakes (memory: daemon timing suites, `VendorCliWatcherTests.Adding_an_account_probes_it_and_extends_the_watch_set`) are rerun once in isolation before being called unrelated.
- [ ] **Step 3:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing; repeat for the daemon project if it publishes separately.
- [ ] **Step 4:** Manual check on this machine with the published binary: `kcap accounts refresh --json`, then a `kcap statusline --account <id>` run fed a captured status line payload, and inspect `~/.config/kcap/accounts/readings/`.
- [ ] **Step 5:** Commit any fixes with their own subjects.
