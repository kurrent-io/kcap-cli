# Vendor accounts usage bars Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Draw each vendor account's usage reading as bars in Settings, the launcher's Account rows and the composer strip, and publish a real Recording state for Codex accounts whose hooks Codex trusts.

**Architecture:** Core gains the Codex hook-trust classifier (moved from the daemon), a trust fingerprint, two registry fields and an account-aware recording state; the account probe asks `hooks/list` and the publisher watches each Codex account's `hooks.json`/`config.toml`. The app gains one pure projection (`AccountUsage.Project`) of an `AccountViewDto` and a time, a 30 s usage clock, a per-daemon-incarnation "drawable" gate in `RemoteAgentsService`, and an `AccountReadings` service that hands each surface the drawable snapshot for its host.

**Tech Stack:** .NET 10, NativeAOT, Avalonia 12 + ReactiveUI, TUnit.

**Spec:** `docs/superpowers/specs/2026-10-27-vendor-accounts-usage-bars-design.md` (builds on `2026-10-07-vendor-accounts-and-limits-design.md` and `2026-10-21-vendor-accounts-launch-choice-design.md`). Mockups: https://kurrent.kcap.ai/pages/572543741e05450f8f6510f4109f2731

## Global Constraints

- Warning threshold **75%** (bar and percentage turn `KcapWarning*` at a displayed value ≥ 75); danger (`KcapDanger*`) at a displayed 100% or on a blocked window.
- Ready: Recording, signed in, not blocked, ≥ 1 primary window, every primary window confirmed within **1 hour**, not expired, displayed value **< 90%**.
- Main limit ids: `ClaudeStatusLineReading.LimitId` ("claude"), `CodexRateLimits.DefaultLimitId` ("codex").
- Usage clock period **30 s**; publisher poll is the existing **2 s**; refresher cooldown the existing **10 min**.
- Window labels: 300 → "5h", 10080 → "Week", other whole days → "Nd", other whole hours → "Nh", else "Nm".
- Copy, exact: "no reading yet", "usage unknown", "+N per model", "+N per model · K hit", "Limit hit", "Limit hit · resets in …", "resets in 2h 14m", "resets in 12m", "resets in <1m", "resets Thu 09:00", "reset · usage unknown", "updated " + `RelativeTime.Format`, "freshness unknown", "Needs sign-in".
- Identity never takes a status colour: Default and plan are neutral chips; labels use text/muted tokens (CLAUDE.md).
- Kcap control classes for new controls (`kcapPanel`, `kcapGhost`, `kcapChip`); no new icon families.
- No CLI flag, refresh step or exit-code change. README's Settings Accounts paragraph is updated (Task 11).
- Comments follow CLAUDE.md's Comments rules: scarce, no history, no spec coordinates.
- New positional record members and parameters go second from last (team convention), unless something forces otherwise.
- Read Codex's `hooks.json` and `config.toml` with `FileShare.ReadWrite` (the agent writes them).
- Commit subjects: `one clause (#1957)`, ≤ 80 chars, body ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Tests: TUnit; `--treenode-filter "/*/*/ClassName/*"` to run one class. After the last task: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.

## Review Focus

1. A vendor percentage outside 0–100 (Codex can report 101 or a negative after a reset race): the fill is clamped to 0–100 and the text never reads above "100%" or below "0%" — Task 6.
2. A fractional percentage at a threshold edge (74.6, 89.6): thresholds apply to the rounded, displayed integer, so "75%" is never neutral and "90%" is never ready — Task 6.
3. A `ConfirmedAt` or `ResetsAt` ahead of or far behind the app's clock (another machine's skew): age reads "updated just now", never negative; a reset already past on first sight is an expired window — Task 6.
4. A session whose `AccountId` is not in its host's drawable snapshot (account removed, or a stale id): the strip shows label and directory only, no "no reading yet" — Task 9.
5. A window with `WindowMinutes` ≤ 0 (malformed vendor data): labelled "—", still drawn, and does not crash ordering — Task 6.

---

### Task 1: Move the Codex hook-trust classifier into Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Codex/CodexHookTrust.cs` (moved from `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHookTrust.cs`, delete the old file)
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexAppServerHostedAgentRuntime.cs:391-410`
- Move test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexHookTrustTests.cs` → `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexHookTrustTests.cs`

**Interfaces:**
- Produces (namespace `Capacitor.Cli.Core.Harness.Codex`, all `public`): `CodexHookEntry`, `CodexHookTrustDecision` (+ its four nested records), `CodexHookTrust.Classify(IReadOnlyList<CodexHookEntry>)`, and new `CodexHookTrust.ParseHooksList(JsonElement result) -> IReadOnlyList<CodexHookEntry>` (the body of the runtime's current loop over `data[].hooks[]`).

- [ ] **Step 1:** Move the file, change the namespace, make the types public, add `ParseHooksList`; the runtime's `ListHooksAsync` calls it. Keep the type docs; trim any history in them per CLAUDE.md.
- [ ] **Step 2:** Move the tests, fix their namespace, and add `ParseHooksList_reads_every_group_and_tolerates_missing_fields` (two groups, one hook missing `currentHash` → `CurrentHash` null, one missing `trustStatus` → `""`; a result without `data` → empty).
- [ ] **Step 3:** Run `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit -- --treenode-filter "/*/*/CodexHookTrustTests/*"` and the daemon's Codex app-server runtime tests (`--treenode-filter "/*/*/CodexAppServerHostedAgentRuntimeTests/*"` in the daemon test project). Expected: PASS.
- [ ] **Step 4:** Commit `Move the Codex hook-trust classifier into Core (#1957)`.

### Task 2: Trust fingerprint, registry fields and Codex Recording

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Codex/CodexTrustFingerprint.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/HookTrustStates.cs`
- Modify: `src/Capacitor.Cli.Core/Accounts/VendorAccount.cs`, `src/Capacitor.Cli.Core/Accounts/AccountWiring.cs` (the `State(VendorAccount, UserHome)` overload), `src/Capacitor.Cli.Core/Accounts/AccountSnapshotBuilder.cs:24`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexTrustFingerprintTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountWiringTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountSnapshotBuilderTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountStoreTests.cs`

**Interfaces:**
- Produces: `public static class CodexTrustFingerprint { public static string Of(CodexPaths paths); }` — lowercase hex SHA-256 over, for `UserHooksJson` then `ConfigToml`, a one-byte presence flag, an 8-byte little-endian length and the file's bytes (absent file: flag 0, nothing else). Files are read with `FileShare.ReadWrite`.
- Produces: `public static class HookTrustStates { public const string Trusted = "trusted"; public const string Untrusted = "untrusted"; }`
- Produces: `VendorAccount` gains `[property: JsonPropertyName("hook_trust")] string? HookTrust = null` and `[property: JsonPropertyName("hook_trust_fingerprint")] string? HookTrustFingerprint = null`, inserted before `LabelSetByUser`.
- Produces: `AccountWiring.State(VendorAccount account, UserHome home)` returns `Recording` for a Codex account when the vendor/directory state is `Installed`, `HookTrust == HookTrustStates.Trusted` and `HookTrustFingerprint == CodexTrustFingerprint.Of(AccountLayouts.Codex(home, account.Directory))`; otherwise what `State(vendor, directory, home)` returns. `AccountSnapshotBuilder.Build` calls this overload.

- [ ] **Step 1: Write failing tests**
  - `CodexTrustFingerprintTests`: `Same_files_same_fingerprint`; `Editing_hooks_json_changes_it`; `Editing_config_toml_alone_changes_it`; `An_absent_file_differs_from_an_empty_one`; `Moving_bytes_between_the_two_files_changes_it` (hooks "ab"+config "c" ≠ hooks "a"+config "bc").
  - `AccountWiringTests`: `Codex_trusted_for_the_current_files_is_recording`; `Codex_trusted_for_other_files_stays_installed` (edit `config.toml` after recording the fingerprint); `Codex_untrusted_stays_installed`; `Codex_without_trust_fields_stays_installed`; `Codex_not_wired_ignores_a_stored_trust` (hooks.json without the kcap hook, `HookTrust = trusted` → `NotWired`).
  - `AccountSnapshotBuilderTests`: `A_trusted_codex_account_publishes_recording`.
  - `AccountStoreTests`: `A_registry_without_trust_fields_loads_them_null`.
- [ ] **Step 2:** Run the four classes; expected FAIL (missing members).
- [ ] **Step 3:** Implement per Interfaces.
- [ ] **Step 4:** Run the four classes plus `AccountRefreshTests`, `AccountResolutionTests`; expected PASS.
- [ ] **Step 5:** Commit `Publish Recording for a Codex account whose hooks are trusted (#1957)`.

### Task 3: The Codex probe asks for hook trust

**Files:**
- Modify: `src/Capacitor.Cli.Core/Harness/Codex/CodexAccountProbe.cs`, `src/Capacitor.Cli.Core/Accounts/ProbeResult.cs`, `src/Capacitor.Cli.Core/Accounts/AccountRefresh.cs` (`Commit`)
- Create: `src/Capacitor.Cli.Core/Accounts/HookTrustObservation.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexAccountProbeTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountRefreshTests.cs`

**Interfaces:**
- Consumes: Task 1's `ParseHooksList`/`Classify`, Task 2's fingerprint, states and fields.
- Produces: `public sealed record HookTrustObservation(string Trust, string Fingerprint);` (`Trust` is a `HookTrustStates` value). `ProbeResult` gains `HookTrustObservation? HookTrust = null` inserted before `LimitsNote`; null is "no answer".
- Probe: take `CodexTrustFingerprint.Of(AccountLayouts.Codex(home, account.Directory))` before `CliProcess.TryStart`; after the limits step (inside the same deadline and try), request `hooks/list` with `{"cwds":[<psi.WorkingDirectory>]}`; `Proceed` → `Trusted`, every other decision → `Untrusted`; an `RpcError`, timeout, IO failure or unparseable result → null; take the fingerprint again after the answer and use null when the two differ. Never touches `IdentityError`/`LimitsError`.
- Refresh: in `Commit`, whenever the probe commits (`started.ProbeSeq > current.CommittedProbeSeq`), a non-null `result.HookTrust` sets `HookTrust`/`HookTrustFingerprint`; null leaves them. Independent of identity and limits errors. `Steps` unchanged.

- [ ] **Step 1: Write failing tests** (the stub must answer `hooks/list` with the request's id: switch the stub to echo the `id` it read, or answer by position — the id is 4 when limits were asked, 3 when not)
  - `Trusted_hooks_report_trusted_with_the_fingerprint` (all three critical events with kcap commands, `trustStatus: trusted`; `result.HookTrust == new(Trusted, CodexTrustFingerprint.Of(...))`; the log shows `hooks/list` with `"cwds"`).
  - `Untrusted_hooks_report_untrusted`; `Missing_critical_hook_reports_untrusted`.
  - `Unsupported_hooks_list_is_no_answer` (error `-32601`) — and `IdentityError`/`LimitsError` stay null.
  - `Hooks_list_timeout_is_no_answer_and_keeps_identity_and_limits` (stub never answers `hooks/list`, short deadline).
  - `Malformed_hooks_list_is_no_answer` (`result` is a string).
  - `A_file_edited_during_the_probe_is_no_answer` (the stub's `hooks/list` branch appends to `config.toml` before answering).
  - `AccountRefreshTests`: `A_trust_answer_is_stored_with_its_fingerprint`; `No_trust_answer_keeps_the_stored_pair`; `A_trust_answer_commits_beside_an_identity_error`; `A_superseded_probe_stores_no_trust`; `Codex_refresh_steps_are_still_identity_and_limits` (step names exactly `["identity","limits"]` whatever the trust).
- [ ] **Step 2:** Run both classes; expected FAIL.
- [ ] **Step 3:** Implement per Interfaces.
- [ ] **Step 4:** Run both classes, `AccountsCommand` tests in `test/Capacitor.Cli.Tests.Unit` (`--treenode-filter "/*/*/AccountsCommand*/*"`); expected PASS.
- [ ] **Step 5:** Commit `Ask Codex whether it trusts kcap's hooks during an account refresh (#1957)`.

### Task 4: The publisher watches each Codex account's hook files

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AccountsPublisher.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountsPublisherTests.cs`

**Interfaces:**
- Consumes: Task 2's state overload (through the builder).
- Change: `PollAsync`'s `unchanged` check also compares `(string AccountId, string Path, long Length, long Ticks)[]` stats of `UserHooksJson` and `ConfigToml` for every Codex account in the last loaded registry (field `_wiringStat`; absent file → length -1). After a successful publish, for each Codex account whose wiring stat differs from the previous poll's (not on the first poll), call `RequestRefresh?.Invoke(accountId)` (wrapped like `RequestBlockedRefreshes`).

- [ ] **Step 1: Write failing tests**
  - `An_edit_to_config_toml_republishes_without_a_registry_change` (trusted account published Recording; write `config.toml`; advance the fake time one poll; `Current` shows `installed`).
  - `An_edit_to_hooks_json_requests_a_refresh_of_that_account` (a `RequestRefresh` recorder sees the id once).
  - `The_first_poll_requests_no_refresh`.
  - `A_claude_account_has_no_wiring_watch` (editing its settings requests nothing).
- [ ] **Step 2:** Run the class; expected FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `AccountsPublisherTests`, `AccountsPublisherServerTests`, `AccountRefresherTests`; expected PASS.
- [ ] **Step 5:** Commit `Republish accounts when a Codex hook file changes (#1957)`.

### Task 5: Share the live-constraint rule

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/Readings/ReadingMerge.cs:62-74`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/Readings/` (the existing blocked-marker test class)

**Interfaces:**
- Produces: `public static IReadOnlyList<BlockConstraintDto> LiveConstraints(AccountReadingDto reading, DateTimeOffset now)` — empty unless the marker is blocked after its last clear (the current `IsBlocked` marker test); otherwise the constraints whose `ResetsAt` is null or after `now`. `IsBlocked` becomes `LiveConstraints(reading, now).Count > 0`.

- [ ] **Step 1:** Add `LiveConstraints_drops_lapsed_and_keeps_unknown` and `LiveConstraints_is_empty_once_cleared`.
- [ ] **Step 2:** Run; FAIL. **Step 3:** Implement. **Step 4:** Run every class under `Accounts/Readings`; PASS.
- [ ] **Step 5:** Commit `Expose the live block constraints of a reading (#1957)`.

### Task 6: The usage projection

**Files:**
- Create: `src/Capacitor.App/ViewModels/AccountUsage.cs` (projection), `src/Capacitor.App/ViewModels/AccountUsageView.cs` (the view record + `UsageBar` + `UsageTone`, one file: a small closely-related hierarchy), `src/Capacitor.App/ViewModels/UsageText.cs`
- Test: `test/Capacitor.App.Tests.Unit/AccountUsageTests.cs`, `test/Capacitor.App.Tests.Unit/UsageTextTests.cs`

**Interfaces:**
- Consumes: Task 5's `LiveConstraints`; `RelativeTime.Format`; `RecordingStateLabels`.
- Produces:
  - `public enum UsageTone { Neutral, Warning, Danger, Unknown }`
  - `public sealed record UsageBar(string Label, int? Percent, string PercentText, UsageTone Tone, string? ResetText);` — `Percent` null and `PercentText` "—" for an expired window; `ResetText` "reset · usage unknown" then.
  - `public sealed record AccountUsageView(IReadOnlyList<UsageBar> Bars, string? LineText, string? PerModelText, string? BlockedText, string? NextResetText, string? AgeText, bool Ready)` with value equality over `Bars` (override `Equals(AccountUsageView?)`/`GetHashCode`). `LineText` is "no reading yet" / "usage unknown" / "usage unknown · +N per model" / null; `BlockedText` is null, "Limit hit" or "Limit hit · resets in …" (compact surfaces may drop the suffix themselves); `NextResetText` is `UsageText.Reset` of the earliest future primary `ResetsAt`; `AgeText` "updated …" or "freshness unknown" or null without windows.
  - `public static class AccountUsage { public const int WarningPercent = 75; public const int ReadyBelowPercent = 90; public static readonly TimeSpan ReadyFreshness = TimeSpan.FromHours(1); public static AccountUsageView Project(AccountViewDto account, DateTimeOffset now, TimeZoneInfo zone); public static bool IsBlocked(AccountViewDto account, DateTimeOffset now); public static string? NeedsFix(AccountViewDto account); public static DateTimeOffset? NextReset(AccountViewDto account, DateTimeOffset now); }` — `NeedsFix` returns "Needs sign-in" for `SignIn == signed_out`, else `RecordingStateLabels.Label` when `IsAttention`, else null.
  - `public static class UsageText { public static string WindowLabel(int minutes); public static string Reset(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone); }` — `Reset`: under 1 min "resets in <1m"; under 1 h "resets in {m}m"; under 24 h "resets in {h}h {mm}m" (minutes zero-padded to two digits, as "2h 05m"); otherwise `"resets " + local.ToString("ddd HH:mm", InvariantCulture)`. Minutes floor.
- Rules (from the spec, §2): main limit by vendor; per-model = distinct other limit ids; hits = distinct per-model limit ids with a live constraint; bars sorted by `WindowMinutes`; percentage = `Math.Clamp((int)Math.Round(UsedPercent, MidpointRounding.AwayFromZero), 0, 100)`; tone from the rounded value; blocked windows = live main-or-null constraints matching `WindowMinutes` → Danger; blocked-until = latest `ResetsAt` over live main-or-null constraints, any null → "Limit hit" alone; the precedence table for `LineText`; Ready per Global Constraints.

- [ ] **Step 1: Write failing tests** (fixed `now`, `TimeZoneInfo.Utc`)
  - `UsageTextTests`: labels `[300→"5h", 10080→"Week", 2880→"2d", 120→"2h", 90→"90m", 0→"—", -5→"—"]`; reset texts at +30 s, +12 m, +2 h 5 m, +23 h 59 m, +3 d (expect "resets Thu 09:00" for a fixed Thursday 09:00 UTC).
  - `AccountUsageTests`: `Null_reading_is_no_reading_yet`; `Empty_unblocked_reading_is_no_reading_yet`; `Metadata_only_is_no_reading_yet`; `Blocked_before_any_window_is_usage_unknown_with_limit_hit`; `Per_model_only_is_usage_unknown_with_the_count`; `Primary_bars_sort_by_length_and_count_others`; `Warning_from_75_rounded` (74.4 neutral "74%", 74.6 warning "75%"); `Hundred_is_danger`; `Out_of_range_percent_is_clamped` (101 → "100%", -3 → "0%"); `Confirmed_zero_is_drawn_as_zero`; `Expired_window_reads_reset_usage_unknown`; `Blocked_window_is_danger`; `Per_model_hit_does_not_block` (text "+1 per model · 1 hit", `BlockedText` null, Ready true when otherwise ready); `Null_limit_constraint_blocks`; `Blocked_until_is_the_latest_reset_in_either_signal_order`; `Unknown_reset_constraint_reads_limit_hit_alone`; `Next_reset_is_the_earliest_primary_reset`; `Age_from_latest_confirmed`; `Unconfirmed_windows_read_freshness_unknown`; `Future_confirmed_reads_updated_just_now`; `Ready_positive_case`; `Not_ready_when_one_window_is_stale_beside_a_fresh_one`; `Not_ready_at_90_rounded` (89.6); `Not_ready_when_expired`; `Not_ready_when_not_recording_or_signed_out`; `Main_constraint_kept_live_by_an_exhausted_per_model_window_still_blocks` (a reading built through `ReadingMerge.Apply` where the later response has healthy main windows and an exhausted per-model one); `Needs_fix_reasons` (signed out, installed codex, broken, not wired, recording → null); `Equal_views_compare_equal`.
- [ ] **Step 2:** Run both classes; FAIL. **Step 3:** Implement. **Step 4:** Run; PASS.
- [ ] **Step 5:** Commit `Project an account's usage reading for the desktop app (#1957)`.

### Task 7: The usage clock and the drawable gate for remote hosts

**Files:**
- Create: `src/Capacitor.App/Services/UsageClock.cs`
- Modify: `src/Capacitor.App/Services/RemoteAgentsService.cs` (+ `IRemoteAgentsService`), `src/Capacitor.App/Services/NoRemoteAgents.cs`, `test/Capacitor.App.Tests.Unit/FakeRemoteAgents.cs`
- Test: `test/Capacitor.App.Tests.Unit/UsageClockTests.cs`, `test/Capacitor.App.Tests.Unit/RemoteAgentsServiceTests.cs`

**Interfaces:**
- Produces: `public static class UsageClock { public static readonly TimeSpan Period = TimeSpan.FromSeconds(30); public static IObservable<DateTimeOffset> Create(TimeProvider time); }` — emits `time.GetUtcNow()` on subscribe and every `Period` (via `time.CreateTimer`), shared with `Publish().RefCount()`.
- Produces: `IRemoteAgentsService.DrawableDaemonAccounts : IObservable<IReadOnlyList<DaemonAccountsDto>>` (replay-1, seeded `[]`): the subset of the retained accounts whose daemon gate is open. `DaemonAccounts` keeps its current behaviour.
- Gate (under `_lock`): an `int _accountsFetchSeq` incremented when an accounts fetch *starts*; per daemon name a `(string Incarnation, int SeenAtSeq, bool Fetched)` where `Incarnation` joins `OwnerUserId`, `ConnectedAt` (round-trip format) and `MachineId` of every listing entry with that name. On every listing answer: for each name whose incarnation is new or changed, set `SeenAtSeq = _accountsFetchSeq`, `Fetched = false`, and call `RefreshDaemonAccountsAsync()` (its busy/rerun flags coalesce it); names absent from the listing are dropped. A successful accounts fetch that started at seq `s` sets `Fetched = true` for every tracked name with `SeenAtSeq < s`. Lane loss and every Connected status clear all entries. Republish `DrawableDaemonAccounts` after each of these: `_connected && listing answered for the current listing generation ? retained.Where(d => entry(d.DaemonName) is { Fetched: true }) : []`. `NoRemoteAgents` and `FakeRemoteAgents` expose a subject (`DrawableDaemonAccountsSubject` in the fake).

- [ ] **Step 1: Write failing tests**
  - `UsageClockTests`: `Emits_now_on_subscribe_and_every_30_seconds` (fake `TimeProvider` from `Microsoft.Extensions.Time.Testing` if the test project references it, else the project's existing fake time).
  - `RemoteAgentsServiceTests` (drive `FakeServerLane`): `Accounts_become_drawable_after_listing_then_accounts`; `Accounts_become_drawable_when_the_accounts_fetch_finishes_before_the_listing` (the listing's new incarnation schedules a second fetch which opens the gate); `Lane_loss_closes_the_gate_but_keeps_the_accounts` (assert `DaemonAccounts` unchanged, drawable empty); `A_daemon_leaving_the_listing_closes_its_gate`; `Reconnect_closes_the_gate_until_a_fresh_accounts_fetch`; `Leave_and_rejoin_with_a_failed_refetch_stays_closed`; `Leave_and_rejoin_reopens_after_a_successful_fetch`; `Another_host_taking_the_name_with_a_failed_refetch_stays_closed` (changed `MachineId`); `A_new_incarnation_schedules_an_accounts_fetch` (count of `GetDaemonAccountsAsync` calls).
- [ ] **Step 2:** Run both classes; FAIL. **Step 3:** Implement. **Step 4:** Run both plus every existing `RemoteAgentsServiceTests`; PASS.
- [ ] **Step 5:** Commit `Gate remote account readings on the daemon's current connection (#1957)`.

### Task 8: Readings in the launcher's Account rows

**Files:**
- Create: `src/Capacitor.App/Services/AccountReadings.cs`
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs`, `src/Capacitor.App/Views/LauncherPaneView.axaml.cs:506-553,558-562,668-677`, `src/Capacitor.App/App.axaml.cs` (composition: ~678 clock, ~920 accounts, 1685-1695 HomeViewModel)
- Create view helper: `src/Capacitor.App/Views/UsageBarStrip.cs` (builds the compact bars line as controls; reused by Task 9's code-behind if needed)
- Test: `test/Capacitor.App.Tests.Unit/AccountReadingsTests.cs`, `test/Capacitor.App.Tests.Unit/HomeViewModelAccountTests.cs`, `test/Capacitor.App.Tests.Unit/LauncherPaneViewSmokeTests.cs`

**Interfaces:**
- Consumes: Tasks 6–7.
- Produces: `public sealed class AccountReadings(IObservable<AccountsSnapshotDto?> local, IObservable<IReadOnlyList<DaemonAccountsDto>> drawableRemote, IObservable<DateTimeOffset> clock)` with `IObservable<DateTimeOffset> Clock`, `IObservable<AccountsSnapshotDto?> Local` (the local snapshot as given: `AccountsService` already publishes null on detach), and `IObservable<AccountsSnapshotDto?> Remote(string daemonName)` (`drawableRemote.Select(l => l.FirstOrDefault(d => d.DaemonName == daemonName)?.ToSnapshot()).DistinctUntilChanged(AccountsSnapshotEquality)` — equality by `AccountSnapshotBuilder.Equivalent`). Built once in `App.axaml.cs`.
- `HomeViewModel` gains ctor param `AccountReadings? readings = null` inserted before `launchFailed`, and `public IReadOnlyDictionary<string, AccountUsageView> AccountUsage { get; private set; }` (raise on change; `FrozenDictionary.Empty` when nothing) — for the selected machine's drawable snapshot (`readings.Local` or `readings.Remote(machine)`), one entry per registered account id of the selected vendor, recomputed on snapshot, machine/vendor change and every clock tick, published only when it differs by value. Combine before `ObserveOn(MainThreadScheduler)`.
- `LauncherPaneView`: `AccountRow` adds a third line from `vm.AccountUsage[account.Id]` when present: mini bars (label, 46×4 track, percent, coloured by tone: Neutral → `KcapMutedBrush` fill, Warning → `KcapWarningBrush`, Danger → `KcapDangerBrush`, Unknown → `KcapBorderStrongBrush`), then `NextResetText` in faint; or `LineText` in faint; `PerModelText` after the bars. A compact "Limit hit" pill (`KcapDangerDimBrush` background, `KcapDangerBrush` text) beside the label when `BlockedText` is non-null. No line for an id absent from `AccountUsage` (host not drawable). `OnAccountChanged` also reacts to `AccountUsage` and compares the drawn usage dictionary by value before rebuilding.

- [ ] **Step 1: Write failing tests**
  - `AccountReadingsTests`: `Remote_picks_the_named_daemons_drawable_snapshot`; `Remote_is_null_once_the_gate_closes`; `Local_follows_the_accounts_service_including_null_on_detach` (drive a real `AccountsService` with a fake attach status: Connected→snapshot→Disconnected gives null).
  - `HomeViewModelAccountTests` (extend `Rig` with an `AccountReadings` over `FakeRemoteAgents.DrawableDaemonAccountsSubject`, `LocalAccounts` and a `Subject<DateTimeOffset>` clock): `Usage_is_projected_for_the_selected_hosts_accounts`; `A_tick_that_changes_no_text_publishes_nothing`; `A_tick_crossing_a_reset_updates_the_usage`; `Usage_empties_when_the_remote_gate_closes_while_choice_rows_stay`; `Local_usage_empties_on_detach`.
  - `LauncherPaneViewSmokeTests`: `An_open_panel_draws_bars_and_drops_them_when_the_host_goes_away` (open the Agent panel, assert the row's automation peer/visual tree contains the percent text, close the gate, assert it is gone without the panel closing).
- [ ] **Step 2:** Run the three classes; FAIL. **Step 3:** Implement (wire `AccountReadings` in `App.axaml.cs`: `UsageClock.Create(time)`, `accounts.Snapshot`, `remoteAgents?.DrawableDaemonAccounts ?? Observable.Return<IReadOnlyList<DaemonAccountsDto>>([])`). **Step 4:** Run the three classes plus `AccountChooserTests`; PASS.
- [ ] **Step 5:** Commit `Show account usage bars in the launcher's Account rows (#1957)`.

### Task 9: Readings in the composer strip

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (both ctors, `ApplyAccount`), `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs:188-197,341`, `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs:260-268,322`, `src/Capacitor.App/App.axaml.cs` (the `WorkspaceViewModel` and `BuildRemote`/`BuildServer` construction, ~890-907), `src/Capacitor.App/Views/ChatTabView.axaml:553-562`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`

**Interfaces:**
- Consumes: Task 8's `AccountReadings`.
- `ChatTabViewModel`: both ctors gain `IObservable<AccountsSnapshotDto?>? accountReadings = null, IObservable<DateTimeOffset>? usageClock = null` inserted before `signInCompleted`. New properties: `AccountUsageView? AccountUsage`, `bool ShowsAccountUsage` (bars or a line), `bool ShowsAccountDirectory`. Rules: no `AccountId` → directory only; snapshot null (host not drawable) or the id absent from it → directory only; else project with `AccountUsage.Project`; `LineText == "no reading yet"` → directory plus "· no reading yet"; otherwise bars (or usage-unknown line), no directory. Combine session account id, snapshot and clock before `ObserveOn`; publish on value change only.
- `WorkspaceViewModel` passes `readings.Local` and `readings.Clock`; `RemoteSessionViewModel` gains ctor param `AccountReadings? readings = null` (before its last parameter) and passes `readings.Remote(row.MachineBadge)` when `MachineBadge` is non-null, else nothing. `SubagentPaneViewModel` is unchanged.
- `ChatTabView.axaml`: the strip grid becomes glyph, label, then either the directory (+ " · no reading yet" when applicable) or an `ItemsControl` of bars (label, 56×4 track, percent, tone brush via a new `UsageToneBrushConverter` in `src/Capacitor.App/Views/`), then `NextResetText` or the danger pill (`BlockedText`), with `TextTrimming` on the directory.

- [ ] **Step 1: Write failing tests**
  - `ChatTabViewModelTests`: `Strip_shows_bars_for_a_drawable_reading`; `Strip_shows_no_reading_yet_with_the_directory`; `Strip_shows_directory_only_when_the_host_is_not_drawable`; `Strip_shows_directory_only_for_an_unregistered_default`; `Strip_shows_directory_only_for_an_account_missing_from_the_snapshot`; `Strip_blocked_shows_limit_hit_with_its_reset`; `Strip_follows_the_clock`; for the remote lane through `RemoteSessionViewModel` with a real `RemoteAgentsService` over `FakeServerLane`: `Remote_strip_drops_bars_on_lane_loss`, `Remote_strip_drops_bars_when_the_daemon_leaves_and_reopens_after_a_fresh_fetch`.
  - `ChatTabViewSmokeTests`: `The_strip_draws_a_danger_pill_for_a_blocked_account` (headless render, find the pill text).
- [ ] **Step 2:** Run both classes; FAIL. **Step 3:** Implement. **Step 4:** Run both plus `RemoteSessionViewModelTests`, `WorkspaceViewModelTests`; PASS.
- [ ] **Step 5:** Commit `Show account usage in the strip under the composer (#1957)`.

### Task 10: Settings rows, summary cards and Refresh

**Files:**
- Modify: `src/Capacitor.App/ViewModels/AccountsSettingsViewModel.cs`, `src/Capacitor.App/ViewModels/AccountSettingsRow.cs`, `src/Capacitor.App/Services/Mutation/MutationModel.cs`, `src/Capacitor.App/Services/KcapCli.cs:265-280`, `src/Capacitor.App/App.axaml.cs` (Settings VM construction)
- Create: `src/Capacitor.App/ViewModels/AccountUsageSlot.cs`, `src/Capacitor.App/ViewModels/AccountSummaryCard.cs`
- Test: `test/Capacitor.App.Tests.Unit/AccountsSettingsViewModelTests.cs`, the existing `KcapCli` argv tests, `DaemonMutationLaneTests` (account verbs)

**Interfaces:**
- Consumes: Task 6.
- `MutationVerb` gains `AccountRefresh` appended last; `IsAccountVerb` includes it; `KcapCli` argv `["accounts", "refresh", Required(args.AccountId), "--json"]`; `Words` → `("Refreshed", "refresh", "Refreshing")`.
- `public sealed class AccountUsageSlot : ReactiveObject { public AccountUsageView? View { get; set; } }` (raise on change).
- `AccountSettingsRow` gains `string? Email, string? Plan, string? ErrorText, AccountUsageSlot Usage` inserted before `IsDefault`; `ErrorText` is `RefreshError ?? LimitsError`. The VM keeps one slot per account id across snapshots, so a resent snapshot keeps rows equal and `ApplyGroups` does not rebuild; the unregistered default gets a shared slot whose `View` stays null.
- `public sealed record AccountSummaryCard(string Value, string? Detail, bool Attention);` VM properties `ReadyCard`, `NextResetCard`, `NeedsFixCard` over registered accounts only: Ready "N of M" + ready labels joined ", "; Next reset `UsageText.Reset` text + "{label} · {window}" of the earliest primary reset, "—" with null detail when none; Need a fix count + "{first label} · {reason}", `Attention` when count > 0.
- Ctor gains `IObservable<DateTimeOffset>? usageClock = null` inserted before `appLifetime`; ticks update slots and cards only (never `Groups`).
- `RefreshCommand : ReactiveCommand<AccountSettingsRow, Unit>` via `RunAsync(MutationVerb.AccountRefresh, row.Label, new AccountArgs(null, null, id, null))`.

- [ ] **Step 1: Write failing tests**
  - `AccountsSettingsViewModelTests`: `Rows_carry_email_plan_and_error`; `A_tick_updates_usage_without_rebuilding_groups` (same `Groups` instance, slot `View` changed); `A_resent_snapshot_keeps_the_same_slots`; `Cards_count_registered_accounts_only` (an unregistered default does not raise Need a fix); `Ready_card_lists_ready_labels`; `Next_reset_card_names_account_and_window`; `Need_a_fix_card_counts_signed_out_and_needs_trust`; `Refresh_runs_the_refresh_verb_for_the_row` (captured `MutationRequest.Verb == AccountRefresh`, `Account.AccountId`); `Refresh_outcome_messages` (`OutcomeMessage(AccountRefresh, "work", Succeeded)` == "Refreshed work").
  - `KcapCli` argv test for `AccountRefresh`; lane classification test: a refresh result with a failed step is `Failed`.
- [ ] **Step 2:** Run; FAIL. **Step 3:** Implement. **Step 4:** Run the classes plus `SettingsWindowSmokeTests`; PASS.
- [ ] **Step 5:** Commit `Add usage, summary cards and Refresh to the Settings accounts model (#1957)`.

### Task 11: Settings Accounts tab view and README

**Files:**
- Modify: `src/Capacitor.App/Views/AccountsSettingsView.axaml`, `README.md:116`
- Test: `test/Capacitor.App.Tests.Unit/SettingsWindowSmokeTests.cs`

**Interfaces:**
- Consumes: Task 10's properties, Task 9's `UsageToneBrushConverter`.
- View, as in the mockup: three cards (`Border` on `KcapSurfaceBrush`, `KcapBorderBrush`, radius 12) in a 3-column `UniformGrid` above the groups, `Attention` → value in `KcapWarningBrush`; each row: label, Default chip, plan chip (neutral, same style as Default), email in `KcapMutedBrush`, `BlockedText` danger pill; directory and state lines as today; bars (`ItemsControl` over `Usage.View.Bars`: label, track, percent, reset) or `LineText`; footer: `PerModelText`, `AgeText`, `ErrorText` in `KcapWarningBrush`; the row menu gains **Refresh** first (hidden for the unregistered default, like the menu itself).
- README line 116: add, after the menu sentence, that each row shows the plan and email, a bar per usage window with its reset, the reading's age and the last refresh error, that summary cards above the list show which accounts are ready for a long run, the next reset and which need a fix, and that the menu's **Refresh** asks the vendor again (`kcap accounts refresh`). Keep the existing Needs trust wording.

- [ ] **Step 1:** Add `Refresh_in_the_row_menu_runs_the_refresh_command` to `SettingsWindowSmokeTests` (open the row menu, click Refresh, the fake mutation runner saw `AccountRefresh`).
- [ ] **Step 2:** Run; FAIL. **Step 3:** Implement the view and README. **Step 4:** Run `SettingsWindowSmokeTests`; PASS.
- [ ] **Step 5:** Commit `Draw account usage and summary cards in the Settings accounts tab (#1957)`.

### Task 12: Whole-branch verification

- [ ] **Step 1:** `dotnet build Capacitor.slnx` — 0 warnings, 0 errors.
- [ ] **Step 2:** Run the Core, daemon, CLI and App unit suites (`dotnet run --project test/<suite>`; App suite with `env -u KCAP_DAEMON_NAME -u KCAP_PROFILE`). Expected: no new failures (known local flakes are listed in the session memory; compare against main if any fail).
- [ ] **Step 3:** AOT check from Global Constraints prints nothing.
- [ ] **Step 4:** Commit any fixes as their own commits.
