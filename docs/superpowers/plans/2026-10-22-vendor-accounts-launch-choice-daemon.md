# Vendor accounts at launch, PR 1 (daemon and CLI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A hosted session can be started under a chosen Claude or Codex account: the daemon resolves every launch to a config directory before the process starts, keeps it on the agent, lists the host's accounts and its own defaults to the app and the server, probes the Codex catalog per account, and the CLI can name an account on `kcap agent start` and report its `kcap accounts` verbs machine-readably.

**Architecture:** Account resolution and the snapshot are pure Core code over the Part A registry (`Capacitor.Cli.Core/Accounts`); the daemon adds one background publisher that watches `accounts.json`, builds the snapshot, feeds local subscribers and the server, and nudges the capability refresh. Launchers stop holding a vendor layout and take one per launch from the resolved account; the PTY and app-server environment builders learn a removal set so the vendor's home variable is removed, not merely left out. Every wire change is a trailing optional field.

**Tech Stack:** .NET 10, NativeAOT (System.Text.Json source generation), TUnit, local control socket frames, SignalR hub.

**Spec:** `docs/superpowers/specs/2026-10-21-vendor-accounts-launch-choice-design.md` (Sections 3, 4, 5, 10 item 1, 11, 12), on top of `docs/superpowers/specs/2026-10-07-vendor-accounts-and-limits-design.md` Sections 3, 4, 6.1 and 6.3.

## Global Constraints

- Every wire field is appended and optional: `LaunchAgentCommand`, `DaemonConnect`, `AgentRegistered`, `LiveAgentInfo`, `AgentStatusDto`, `DaemonInfoDto`, `ResumeLaunchDescriptor` keep positional compatibility; `FrameType` values are never reused (next free: 86).
- Null account id means the daemon's default; an explicit id is honoured or refused with `account_unavailable`, never replaced by the default. A resume refuses the same way and never falls through to `kcap continue`.
- The daemon never writes to `accounts.json`; the host registry is read under no lock (`AccountStore.TryLoad`).
- The daemon's default directory per vendor is the one `HarnessRegistry.FromEnvironment` resolved in `DaemonRunner`; the snapshot carries it as a directory and a key, never a registry id.
- Directory keys are computed only where the directory lives: `AccountDirectory.Normalize` then case-fold unless `OperatingSystem.IsLinux()`.
- Child environment: remove `CLAUDE_CONFIG_DIR` / `CODEX_HOME` when the resolved directory is the vendor's built-in location (`AccountLayouts.DefaultDirectory`), set it to the resolved directory otherwise; the daemon's own environment is never changed.
- Local capability token `accounts/1`; hub one-way method `AccountsChanged`; throttle 30 s trailing.
- Project rules from `CLAUDE.md`: one type per file; `FrozenDictionary.Empty` for empty read-only maps; `TempDir` from Helpers; `[NotInParallel]` only for process-global state; no `InternalsVisibleTo` to a production assembly; comments scarce; README and `help-*.txt` change in the same PR; AOT publish shows no IL2026/IL3050; commit subjects end with `(#1806)`.

## Review Focus

- A malformed `accounts.json` mid-run (a hand edit, a half-written replace): the publisher keeps the last good snapshot and a launch naming an id reads the registry as empty, so it is refused, never defaulted. Pinned in Task 6 and Task 7.
- An ambiguous `--account` prefix matching two ids: refused with both candidates listed, nothing spawned. Pinned in Task 15.
- The registry rewritten many times in one burst (the npm refresh wires every account): one debounced snapshot, one trailing server send. Pinned in Task 6 and Task 14.
- A server without the `AccountsChanged` handler: the send is swallowed at debug level, the connection stays up. Pinned in Task 14.
- A Windows registry entry spelled with different casing than the daemon's environment: the keys are equal and the Default badge lands. Pinned in Task 1 and Task 4.

Run one test class with: `dotnet run --project test/<Project>/<Project>.csproj -- --treenode-filter "/*/*/<ClassName>/*"`.

---

### Task 1: Directory keys

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/DirectoryKey.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/DirectoryKeyTests.cs`

**Interfaces:**
- Produces: `public static class DirectoryKey { public static string Of(string directory); }` — `AccountDirectory.Normalize(directory)` then `ToLowerInvariant()` unless `OperatingSystem.IsLinux()`. Throws what `Normalize` throws.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Same_directory_spelled_with_a_trailing_separator_has_one_key()
  // DirectoryKey.Of(tmp.Path) == DirectoryKey.Of(tmp.Path + Path.DirectorySeparatorChar)
[Test] public async Task Symlinked_spelling_keys_to_the_physical_directory()
  // create tmp.CreateDir("real"), symlink tmp.PathTo("link") -> real (skip on Windows without privilege); Of(link) == Of(real)
[Test] public async Task Case_differs_keys_equal_except_on_linux()
  // var a = tmp.CreateDir("Work"); Of(a) vs Of(path with "WORK"): equal unless OperatingSystem.IsLinux(), where the path does not exist -> Normalize of a missing path: assert via Of(a).Equals(Of(a).ToLowerInvariant()) on non-Linux and ordinal on Linux
```

- [ ] **Step 2: Run to verify they fail** — Expected: compile error, `DirectoryKey` not defined.
- [ ] **Step 3: Implement `DirectoryKey.Of`.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Add host-computed directory keys for accounts (#1806)`

### Task 2: Account resolution

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/ResolvedAccount.cs`, `src/Capacitor.Cli.Core/Accounts/AccountResolution.cs`, `src/Capacitor.Cli.Core/Accounts/AccountResolutionOutcome.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountResolutionTests.cs`

**Interfaces:**
- Produces: `public sealed record ResolvedAccount(string Directory, string? AccountId, string Label)`.
- Produces: `public sealed record AccountResolutionOutcome(ResolvedAccount? Account, string? Refusal)` with `public bool Refused => Refusal is not null`.
- Produces: `public static class AccountResolution` with `public const string Unavailable = "account_unavailable";` and
  `public static AccountResolutionOutcome Resolve(AccountRegistry? registry, HarnessId vendor, string? accountId, string defaultDirectory)`:
  null or empty id → the default: `Account = new(defaultDirectory, registered-id-or-null, label)` where the id and label come from a registry entry of `vendor` whose `DirectoryKey.Of` equals the default's, else label is `Path.GetFileName(defaultDirectory)`; an id not in the registry, or in it under another vendor → `Refusal = $"{Unavailable}: no {vendor} account {id}"`; a registered entry whose directory does not exist → `Refusal = $"{Unavailable}: {directory} is missing"`.
  `public static AccountResolutionOutcome ResolveForResume(AccountRegistry? registry, HarnessId vendor, string? accountId, string? directory, string defaultDirectory)`: `directory` null → `Resolve(registry, vendor, null, defaultDirectory)`; `accountId` non-null → must still be registered for `vendor` (any directory) and that directory must exist, else refused; `accountId` null → `directory` must exist, else refused; label as above.
  `public static AccountResolutionOutcome ResolveArgument(AccountRegistry registry, HarnessId vendor, string idPrefixOrDirectory)` for the CLI: a prefix of 4+ chars matching exactly one id of `vendor` → that account; two or more matches → `Refusal` naming them; otherwise a path (`~` expanded by the caller) matched by `AccountDirectory.Same` against `vendor`'s entries; no match → `Refusal = $"{Unavailable}: {arg} is not a registered {vendor} account; run kcap accounts add"`.

- [ ] **Step 1: Write the failing tests** (a registry built in memory with two Claude entries under `tmp.CreateDir`, one Codex)

```csharp
Null_id_resolves_to_the_default_directory_with_its_registered_id()   // Account.AccountId == the entry whose key matches
Null_id_with_an_unregistered_default_has_no_id_and_the_directory_name_as_label()
Unknown_id_is_refused_with_account_unavailable()                        // Refusal starts with "account_unavailable"
Id_registered_for_another_vendor_is_refused()
Registered_id_whose_directory_is_gone_is_refused_naming_the_directory()
Resume_with_null_directory_resolves_as_a_fresh_launch()
Resume_with_a_registered_id_now_removed_is_refused()
Resume_with_null_id_resumes_under_the_stored_directory_while_it_exists()
Argument_prefix_matching_one_id_resolves()
Argument_prefix_matching_two_ids_is_refused_listing_both()
Argument_directory_resolves_by_same_directory()
Argument_unregistered_directory_is_refused_with_the_add_remedy()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement the three files.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Resolve a launch to an account directory before it starts (#1806)`

### Task 3: Wire contracts

**Files:**
- Modify: `src/Capacitor.Cli.Core/Models.cs` (`LaunchAgentCommand`, `DaemonConnect`, `AgentRegistered`, `LiveAgentInfo`), `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` (`AgentStatusDto`, `DaemonInfoDto`), `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`, `src/Capacitor.Cli.Core/Resume/ResumeLaunchDescriptor.cs`
- Create: `src/Capacitor.Cli.Core/Accounts/AccountsSnapshotDto.cs`, `src/Capacitor.Cli.Core/Accounts/AccountViewDto.cs`, `src/Capacitor.Cli.Core/Accounts/DefaultDirectoryDto.cs`, `src/Capacitor.Cli.Core/Accounts/AccountsSnapshotJsonContext.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecAccountsTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecTests.cs` (spawn round-trip), `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/DaemonStatusDtoTests.cs` (exact JSON gains the new members as null), `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountsSnapshotDtoTests.cs`

**Interfaces:**
- `LaunchAgentCommand`: append `string? AccountId = null`.
- `ResumeLaunchDescriptor`: append `string? AccountDirectory = null, string? AccountId = null, string? AccountLabel = null`.
- `AgentStatusDto`, `AgentRegistered`, `LiveAgentInfo`: append the same three.
- `DaemonInfoDto`: append `Dictionary<string, VendorModelOption[]>? VendorModelsByAccount = null` (key: registry id).
- `DaemonConnect`: append `bool SupportsAccountLaunches = false, AccountsSnapshotDto? Accounts = null, Dictionary<string, VendorModelOption[]>? VendorModelsByAccount = null`.
- `AccountsSnapshotDto(string HostId, long Revision, AccountViewDto[] Accounts, DefaultDirectoryDto[] Defaults)`; `AccountViewDto(string Id, string Vendor, string Directory, string DirectoryKey, string Label, string RecordingState)`; `DefaultDirectoryDto(string Vendor, string Directory, string DirectoryKey)`. Vendor is the launch token (`claude`, `codex`); RecordingState is the enum name in lowercase (`recording`, `installed`, `broken`, `not_wired`). `AccountsSnapshotJsonContext` snake_case, `[JsonSerializable(typeof(AccountsSnapshotDto))]`, used by the frame and the hub (register the type in `Models.cs`'s hub JSON context too).
- `FrameType.AccountsSubscribe = 32`, `FrameType.AccountsSnapshot = 87` (Text = JSON).
- `FrameCodec.Spawn(..., AgentStartTitle? title = null, string? accountId = null)`: after the title byte(s) write `WriteLp(ms, accountId ?? "")`; `ParseSpawn` reads it when bytes remain, empty → null; the tuple gains `string? accountId` last. `FrameCodec.AccountsSnapshot(string json)` → `LocalFrame` of type `AccountsSnapshot`, and `LocalFrame.AccountsSubscribe()`.

- [ ] **Step 1: Write the failing tests**

```csharp
// FrameCodecAccountsTests
Spawn_round_trips_an_account_id()                       // Spawn(..., accountId: "abc") -> ParseSpawn.accountId == "abc"
Spawn_without_account_bytes_parses_null()               // a frame built by the old overload / truncated after the title -> null
Spawn_with_account_but_no_title_round_trips()           // title null, account "abc"
AccountsSnapshot_frame_carries_the_json_as_text()
// AccountsSnapshotDtoTests
Snapshot_serializes_snake_case_with_every_field_present()   // exact JSON string for one account, one default
// DaemonStatusDtoTests: extend the exact-JSON expectation with "vendor_models_by_account":null and the three agent fields as null
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Make the contract changes.** Keep every existing positional construction compiling (defaults only).
- [ ] **Step 4: Run the Core suite to verify everything passes, then** `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` **to confirm the daemon and CLI still compile.**
- [ ] **Step 5: Commit** — `Carry an account on the launch, status and resume contracts (#1806)`

### Task 4: Snapshot builder

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/AccountSnapshotBuilder.cs`
- Modify: `src/Capacitor.Cli.Core/Accounts/AccountWiring.cs` (add `public static RecordingState State(HarnessId vendor, string directory, UserHome home)`; the existing overload delegates to it)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountSnapshotBuilderTests.cs`

**Interfaces:**
- Produces: `public static class AccountSnapshotBuilder { public static AccountsSnapshotDto Build(AccountRegistry registry, string hostId, IReadOnlyDictionary<HarnessId, string> defaults, UserHome home); }` — one `AccountViewDto` per registry entry with `DirectoryKey.Of` and `AccountWiring.State`; one `DefaultDirectoryDto` per entry of `defaults`. No synthesized row: the reader derives the Default badge and the unregistered-default row by key.
- Produces: `public static class VendorTokens { public static string Of(HarnessId vendor); public static HarnessId? Parse(string token); }` if no such mapping exists in Core yet (check `HarnessId` extensions first and reuse what is there).

- [ ] **Step 1: Write the failing tests** (registry with a wired Claude dir, an unwired Codex dir; defaults pointing at the Claude dir and at an unregistered Codex dir)

```csharp
Builds_one_view_per_account_with_key_label_and_recording_state()
Defaults_carry_directory_and_key_only()                        // no id field; the Claude default's key equals the Claude view's key
Unregistered_default_matches_no_view_by_key()
Keys_equal_across_casing_off_linux()                           // registry dir "Work", default dir "work" on macOS/Windows -> equal keys
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Build the accounts snapshot from the registry and daemon defaults (#1806)`

### Task 5: `kcap accounts` commits after wiring and reports machine-readably

**Files:**
- Modify: `src/Capacitor.Cli.Core/Accounts/VendorAccount.cs` (append `[property: JsonPropertyName("wired_at")] DateTimeOffset? WiredAt = null, [property: JsonPropertyName("wiring_failure")] string? WiringFailure = null`), `src/Capacitor.Cli/Commands/AccountsCommand.cs`, `src/Capacitor.Cli.Core/Resources/help-accounts.txt`, `README.md` (`### Accounts`)
- Create: `src/Capacitor.Cli.Core/Accounts/AccountActionResultDto.cs`, `src/Capacitor.Cli.Core/Accounts/WiringStepDto.cs`, `src/Capacitor.Cli.Core/Accounts/AccountActionResultJsonContext.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/AccountsCommandTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Accounts/AccountStoreTests.cs`

**Interfaces:**
- `AccountActionResultDto(string Action, string? AccountId, bool Succeeded, WiringStepDto[] Steps, long? Revision, string? Error)`; `WiringStepDto(string Name, bool Succeeded, string Detail)`; snake_case context.
- `AccountsCommand`: a `--json` flag (stripped like `--skip-codex-network-access`) makes every verb print exactly one `AccountActionResultDto` line on stdout and nothing else there; errors go to stderr as today. Exit 0 only when `Succeeded`.
- After `WireAndReport` (add, rewire) and after `Unwire` (remove) the command calls `accounts.Mutate` to stamp `WiredAt = now` and `WiringFailure = null` or the failure summary on the entry (remove: drop the entry in that same mutation, which is the one it does today). A rename is already a mutation. The result's `Revision` is the revision that mutation produced (`Mutate` returns it: change `Mutate<T>` callers to read `Load().Revision` after, or extend `Mutate` to return the new revision alongside `T`; pick the latter: `public (T Result, long Revision) MutateWithRevision<T>(...)`).

- [ ] **Step 1: Write the failing tests**

```csharp
// AccountStoreTests
MutateWithRevision_returns_the_revision_it_wrote()
// AccountsCommandTests (stub PluginEnvironment as the existing tests do)
Rewire_that_changes_nothing_still_increments_the_revision()
Add_whose_wiring_fails_registers_and_reports_failure_with_the_committed_revision()   // exit 1, Succeeded false, Revision == registry revision
Json_flag_prints_one_result_line_with_steps_and_revision()
Remove_reports_the_revision_of_the_removal()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.** Update `help-accounts.txt` (`--json`) and the README `### Accounts` section in the same step.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Commit a registry revision after every accounts verb and add --json (#1806)`

### Task 6: The daemon publishes accounts locally

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AccountsPublisher.cs`, `src/Capacitor.Cli.Daemon/Services/AccountsIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (`public AccountStore? Accounts { get; set; }`, `public IReadOnlyDictionary<HarnessId, string> DefaultDirectories { get; set; } = FrozenDictionary<HarnessId, string>.Empty;`), `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (set both from `AccountStore.Beside(store)` and `harnesses.Of<ClaudeHarness>().Paths.Home` / `harnesses.Of<CodexHarness>().Paths.Home`; register the publisher and the IPC handler), `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (route `AccountsSubscribe`), `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs` (`"accounts/1"`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountsPublisherTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountsIpcTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlCapabilitiesTests.cs` (or wherever the capability list is pinned today)

**Interfaces:**
- `internal sealed class AccountsPublisher : BackgroundService` with ctor `(DaemonConfig config, UserHome home, TimeProvider time, ILogger<AccountsPublisher> logger)`: watches `config.Accounts.Directory` for `accounts.json` with a `FileSystemWatcher` plus a 2-second trailing debounce (reuse the pattern `VendorCliWatcher` uses for catalog paths, or poll every 2 s when the watcher cannot be created); exposes `AccountsSnapshotDto? Current`, `event Action<AccountsSnapshotDto>? Changed`, and `Task RebuildAsync(CancellationToken)`. A `TryLoad` that returns null keeps `Current` unchanged. The host id comes from `config.Accounts.HostId()` once at start.
- `internal sealed class AccountsIpc(AccountsPublisher publisher)` with `Task HandleSubscribeAsync(Stream stream, CancellationToken ct)` shaped like `DaemonStatusIpc.HandleSubscribeAsync`: writes `Current` as an `AccountsSnapshot` frame at once, then one frame per `Changed`, until the client disconnects.

- [ ] **Step 1: Write the failing tests** (a `TempDir`-backed `AccountStore`, a fake clock)

```csharp
// AccountsPublisherTests
Start_builds_the_snapshot_with_host_id_revision_and_defaults()
A_registry_rewrite_republishes_within_the_debounce()          // mutate via AccountStore, expect Changed with the new revision within 5 s
A_burst_of_rewrites_yields_one_snapshot()                     // ten mutations inside 500 ms -> exactly one Changed after the debounce
A_malformed_registry_keeps_the_last_snapshot()                // write garbage to accounts.json -> Current unchanged, no Changed
// AccountsIpcTests
Subscribe_writes_the_current_snapshot_then_each_change()
// capability list test: Current contains "accounts/1"
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.** Register in `DaemonRunner` next to `VendorCliWatcher`.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Publish the host's accounts over the local socket (#1806)`

### Task 7: Launches resolve an account or are refused

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` (`RuntimeStartContext`: append `ResolvedAccount? Account = null`), `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs` (`LauncherContext`: append `ResolvedAccount? Account = null`), `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntimeFactory.cs` (copy `ctx.Account` into the launcher context), `src/Capacitor.Cli.Daemon/Services/AgentInstance.cs` (or wherever `AgentInstance` is declared: `ResolvedAccount? Account`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`HandleLaunchAgentCore`: resolve before the worktree is created; refuse with `LaunchFailed` carrying the refusal text when `Refused`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (Spawn: read the frame's account id and resolve the same way)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorAccountTests.cs` (model on `AgentOrchestratorVendorTests` fixtures)

**Interfaces:**
- `AgentOrchestrator.ResolveAccount(string vendorToken, string? accountId)`: `AccountResolution.Resolve(_config.Accounts?.TryLoad(), vendor, accountId, _config.DefaultDirectories[vendor])`; for a vendor outside Claude and Codex returns `Account = null` and no refusal. Resume launches (`resume is not null`) use `ResolveForResume` from the ledger's fields instead (Task 11).
- Refusal: the same failure path as `CodexHooksNotInstalledException` today (LaunchFailed, no worktree left behind), with `Reason = outcome.Refusal`.

- [ ] **Step 1: Write the failing tests**

```csharp
Null_account_id_launches_under_the_daemon_default_directory()         // launcher context Account.Directory == DefaultDirectories[Claude]
Registered_account_id_launches_under_its_directory_with_its_label()
Unknown_account_id_is_refused_with_account_unavailable_and_spawns_nothing()
Account_id_of_another_vendor_is_refused()
Registered_account_whose_directory_is_gone_is_refused()
Spawn_frame_account_id_resolves_like_a_server_launch()
Malformed_registry_refuses_an_explicit_id_and_defaults_a_null_one()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Resolve or refuse the account before a launch starts (#1806)`

### Task 8: Claude launcher takes its layout per launch

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Claude/ClaudeLauncher.cs` (replace the `_paths` field with `ClaudePaths PathsFor(ResolvedAccount? account)` = `account is null ? harnesses.Of<ClaudeHarness>().Paths : AccountLayouts.Claude(home, account.Directory)`; every site at lines 68, 74, 80, 94, 274 uses it; the teardown at 274 takes the agent's `Account`), and whichever `UserHome` the launcher can reach (take it from `HarnessRegistry` or `DaemonConfig`, whichever already carries it)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Claude/ClaudeLauncherAccountTests.cs`

- [ ] **Step 1: Write the failing tests** (two Claude directories under `TempDir`, `TestHarnesses.Under(Home)` pointing at one)

```csharp
Prepare_writes_trust_and_mcp_config_under_the_account_directory()      // .claude.json and the project dir under the other directory, nothing under the default
Prepare_with_no_account_uses_the_environment_layout()
Teardown_removes_the_project_symlink_from_the_account_directory()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Give the Claude launcher its layout per launch (#1806)`

### Task 9: Codex launcher and app-server runtime take their layout per launch

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Harness/Codex/CodexLauncher.cs` (`CodexPaths PathsFor(ResolvedAccount? account)`; sites at 128, 139, 455; keep a `Paths` property for the environment layout), `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs` (the `Prepare` call and the hooks validation after spawn use the launch's layout), `src/Capacitor.Cli.Daemon/Harness/Codex/CodexAppServerHostedAgentRuntime.cs` (hooks/list validation path if it reads the layout)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexLauncherAccountTests.cs`, extend `CodexHostedAgentRuntimeFactoryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
Prepare_checks_hooks_in_the_account_directory_not_the_default()        // hooks only in the account dir -> no CodexHooksNotInstalledException
Prepare_without_account_or_repository_hooks_in_the_account_directory_throws()
TrustWorktree_writes_the_account_config_toml()
Model_migrations_are_read_from_the_account_config_toml()
AppServer_factory_prepares_with_the_launch_account()                   // factory test: Prepare called with ctx.Account propagated
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Give the Codex launcher and app-server its layout per launch (#1806)`

### Task 10: The child environment removes or sets the vendor home

**Files:**
- Create: `src/Capacitor.Cli.Core/Accounts/VendorHomeVariable.cs`
- Modify: `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs` (`BuildChildEnv(Dictionary<string,string>? extraEnv, IReadOnlyCollection<string>? removeEnv, ushort cols, ushort rows)` and the ctor/options that reach it), `src/Capacitor.Cli.Daemon/Pty/Windows/ConPtyProcess.cs` (`BuildEnvironmentBlock(Dictionary<string,string>? extraEnv, IReadOnlyCollection<string>? removeEnv)`), the PTY options record both read from (add `IReadOnlyCollection<string>? RemoveEnv`), `src/Capacitor.Cli.Daemon/Services/PtyHostedAgentRuntimeFactory.cs` (after building `env`: apply `VendorHomeVariable.Plan`), `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs` (`ApplyChildEnv(childEnv, overlay, IReadOnlyCollection<string> remove)` and the spawn env for app-server)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Accounts/VendorHomeVariableTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Pty/UnixPtyProcessEnvTests.cs` (make `BuildChildEnv` internal), `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Codex/CodexHostedAgentRuntimeFactoryTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PtyHostedAgentRuntimeFactoryEnvTests.cs`

**Interfaces:**
- `public static class VendorHomeVariable { public static string? NameOf(HarnessId vendor); /* CLAUDE_CONFIG_DIR, CODEX_HOME, else null */ public static (string Variable, string? Value) Plan(HarnessId vendor, string directory, UserHome home); }` — `Value` null when `AccountDirectory.Same(directory, AccountLayouts.DefaultDirectory(vendor, home))`, else `directory`. Callers put a null-valued plan into the removal set and a valued one into the overlay.

- [ ] **Step 1: Write the failing tests**

```csharp
// VendorHomeVariableTests
Built_in_directory_plans_a_removal()
Custom_directory_plans_a_set()
Vendor_without_a_home_variable_plans_nothing()
// UnixPtyProcessEnvTests (run under EnvScope.Exclusive for CLAUDE_CONFIG_DIR, bare [NotInParallel])
Inherited_variable_is_removed_when_named_in_the_removal_set()
Overlay_sets_the_variable_after_removal()
// PtyHostedAgentRuntimeFactoryEnvTests: with CLAUDE_CONFIG_DIR inherited as ~/.claude-work
Launch_resolved_to_the_built_in_directory_removes_the_inherited_variable()
Launch_resolved_to_the_daemons_own_custom_default_sets_it_explicitly()
Launch_resolved_to_another_custom_account_sets_that_directory()
// CodexHostedAgentRuntimeFactoryTests: the same three through the spawn-factory seam's env
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.** The daemon process environment is never mutated; assert that in the factory tests by reading `Environment.GetEnvironmentVariable` after the launch.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Remove or set the vendor home in every spawned agent's environment (#1806)`

### Task 11: Resume keeps the account

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.Resume.cs` (`BuildLedgerEntry` and `BuildPreStartLedgerEntry` write `AccountDirectory`, `AccountId`, `AccountLabel` from the resolved account; `BuildResumeCommand` passes `AccountId: launch.AccountId`; the launch path for a resume calls `AccountResolution.ResolveForResume(registry, vendor, launch.AccountId, launch.AccountDirectory, default)`; `ResumeEntryAsync`: when the native failure reason starts with `account_unavailable`, skip `ContinueEntry` and interrupt with that reason; `NativeResumeAsync` returns the refusal text, not `launch_rejected`, when the rejection is an account refusal)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorResumeAccountTests.cs` (model on `AgentOrchestratorResumeOnBootTests`)

- [ ] **Step 1: Write the failing tests**

```csharp
Ledger_entry_records_the_resolved_account()
Resume_relaunches_under_the_ledger_directory_after_the_default_changed()    // daemon default now another dir; launcher context Account.Directory == ledger directory
Resume_of_a_removed_registered_account_fails_with_account_unavailable_and_skips_continue()  // entry Interrupted, LastError starts with account_unavailable, continue launcher never called
Resume_of_an_unregistered_default_uses_its_stored_directory()
Resume_of_an_unregistered_default_whose_directory_is_gone_fails_named()
Legacy_entry_without_account_fields_resumes_under_the_current_default()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Resume an agent under the account it was launched with (#1806)`

### Task 12: Status and registration carry the account

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (the `new AgentStatusDto(` site: the three fields from `agent.Account`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`AgentRegistered` construction and `LiveAgentInfo` construction: the same three)
- Test: extend `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorAccountTests.cs`; a registration-payload test beside the existing `AgentRegistered` tests

- [ ] **Step 1: Write the failing tests**

```csharp
Status_dto_names_the_resolved_account_directory_id_and_label()
Registration_and_live_agent_report_carry_the_resolved_account()
Unregistered_default_reports_the_directory_and_a_null_id()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Report each agent's account in status and registration (#1806)`

### Task 13: Codex model catalog per account

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs` (add `Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(string? configDirectory, CancellationToken ct)` defaulting to the existing probe, and `IReadOnlyList<string> CatalogFingerprintPathsFor(string? configDirectory)` defaulting to `CatalogFingerprintPaths`), `src/Capacitor.Cli.Daemon/Harness/Codex/CodexHostedAgentRuntimeFactory.cs` (probe with `CODEX_HOME` set per directory through the spawn factory's env, using a per-directory probe dir `CodexModelCatalogProbe.DirectoryFor(stateDir, DirectoryKey.Of(dir))`; fingerprint paths from `PathsFor(dir)`), `src/Capacitor.Cli.Daemon/Services/VendorModelCatalogs.cs` (`ProbeByAccountAsync(factories, IReadOnlyList<(string Vendor, string AccountId, string Directory)>, ct)` and `EqualByAccount`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`RefreshAdvertisedCapabilities`: build `_config.VendorModelsByAccount` from the current snapshot's Codex accounts plus the unregistered default; `catalogChanged` compares both maps; a probe result for an id no longer in the snapshot at completion is dropped), `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (`VendorModelsByAccount`), `src/Capacitor.Cli.Daemon/Services/VendorCliWatcher.cs` (`WatchSet` includes each Codex account's fingerprint paths; `AccountsPublisher.Changed` triggers a `WatchSet` rebuild and `RefreshAdvertisedCapabilities("accounts changed")`), `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`DaemonInfoDto.VendorModelsByAccount`), `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`BuildDaemonConnect`: `VendorModelsByAccount`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/VendorModelCatalogsAccountTests.cs`, extend `VendorCliWatcherTests.cs`, `CodexHostedAgentRuntimeFactoryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
Probe_runs_once_per_codex_account_with_its_home_in_the_environment()
Default_catalog_stays_in_vendor_models_and_accounts_land_in_the_map()
Adding_an_account_probes_it_and_extends_the_watch_set()
Removing_an_account_drops_its_catalog_and_a_late_probe_result_is_discarded()
Map_only_change_republishes_locally_and_to_the_server()                  // VendorModels equal, map differs -> pulse + re-register
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Probe and advertise the Codex catalog per account (#1806)`

### Task 14: The daemon publishes accounts to the server

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (`BuildDaemonConnect`: `SupportsAccountLaunches: true`, `Accounts: GetAccountsSnapshot?.Invoke()`; `public virtual Task AccountsChangedAsync(AccountsSnapshotDto snapshot)` as a one-way `SendAsync("AccountsChanged", snapshot)` with the `DaemonStatusReportAsync` catch), `src/Capacitor.Cli.Daemon/Services/AccountsPublisher.cs` (on `Changed`: a 30-second trailing throttle that sends the latest snapshot through the connection, immediately when nothing was sent in the last 30 s), `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (wire `GetAccountsSnapshot`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AccountsPublisherServerTests.cs` (fake `ServerConnection` recording sends, `FakeTimeProvider`)

- [ ] **Step 1: Write the failing tests**

```csharp
Connect_payload_advertises_account_launches_and_carries_the_snapshot()
First_change_sends_at_once_and_a_burst_sends_the_last_change_after_thirty_seconds()
A_server_without_the_handler_logs_at_debug_and_keeps_the_connection()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Publish the accounts snapshot to the server behind its capability (#1806)`

### Task 15: `kcap agent start --account`

**Files:**
- Modify: `src/Capacitor.Cli.Core/AgentStartArgs.cs` (`public string? Account { get; }`; parse `--account <v>` and `--account=<v>`; an empty value is an `Error`), `src/Capacitor.Cli/Commands/AgentCommand.cs` (`RunAsync`: when `Account` is set, load the registry through `AccountStore.Beside(store)`, resolve with `AccountResolution.ResolveArgument` (expand `~` as `AccountsCommand.TryNormalize` does; move that expansion into Core if it is private there), refuse on `Refusal` with exit 1; send `Hello` and refuse with "this daemon does not support choosing an account; update kcap" when `Capabilities` lacks `accounts/1`; pass the id to `FrameCodec.Spawn`), `src/Capacitor.Cli.Core/Resources/help-agent.txt` (`--account <id|dir>` under "Options for start"), `README.md` (`### Local agents (kcap agent)` example line and `### Accounts`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/AgentStartArgsTests.cs` (or the existing args test file), `test/Capacitor.Cli.Tests.Unit/Commands/AgentCommandAccountTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// AgentStartArgsTests
Account_flag_with_a_value_parses()                 // both spellings
Account_flag_without_a_value_is_an_error()
// AgentCommandAccountTests (fake socket as AgentCommandRoutingTests does)
Account_prefix_resolves_and_the_spawn_frame_carries_the_id()
Ambiguous_prefix_is_refused_listing_both_candidates_and_nothing_is_sent()
Unregistered_directory_is_refused_with_the_add_remedy()
Daemon_without_accounts_capability_is_refused_before_spawn()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.** README and help text in the same step.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Choose the account on kcap agent start (#1806)`

### Task 16: Whole-PR verification

- [ ] **Step 1:** `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` — 0 warnings.
- [ ] **Step 2:** `dotnet test --solution Capacitor.slnx` — green (daemon timing flakes rerun alone per the project memory).
- [ ] **Step 3:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — no output.
- [ ] **Step 4:** Live check on this Mac: `kcap accounts --json` shows the revision; `kcap agent start claude --account <prefix> -d` spawns, and the agent's row in the daemon's status snapshot (the `StatusSubscribe` frame the app reads; `kcap daemon status --json` reports the service, not agents) carries the account's `account_directory`, `account_id` and `account_label`, with its `transcript_path` under that directory; the daemon log shows the agent's spawn and session link and no `account_unavailable` refusal (it names no account, so the account is read from the snapshot); the local snapshot frame lists both Claude directories with the daemon's default keyed to one of them.

### Task 17: Session discovery and the model report read the launch's layout

Runs between Task 9 and Task 10. The orchestrator still reads the environment layout in three
places an account launch must not use: the resolved-model report for Codex reads the environment
`config.toml`, and both transcript locators search the environment `projects/` (Claude) and
`sessions/` (Codex). A session started under a custom directory writes its transcript there, so
detection would never find it and the agent would have no session id and no transcript path.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`CodexResolvedModel` about line 3395 takes the launch's `ResolvedAccount?`; the `"claude"` / `"codex"` locator lambdas about lines 5724–5727 use the agent's account layout)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorAccountTests.cs` (extend)

**Interfaces:**
- Consumes: `AgentInstance.Account`, `AccountLayouts.Claude(home, dir)` / `AccountLayouts.Codex(home, dir)`, `DaemonConfig.Home`.
- Produces: `ClaudePaths ClaudePathsFor(ResolvedAccount? account)` and `CodexPaths CodexPathsFor(ResolvedAccount? account)` on the orchestrator, the environment layout for a null account (never touching `Home`), else the account's layout; every later orchestrator read of a vendor layout goes through them.

- [ ] **Step 1: Write the failing tests**

```csharp
Codex_session_detection_searches_the_account_sessions_directory()    // a rollout for the agent's worktree under the account's sessions dir is found; the same file under the environment dir is not
Claude_session_detection_searches_the_account_projects_directory()   // same for a Claude transcript under the account's projects dir
Codex_resolved_model_report_reads_the_account_config_toml()          // the account's config.toml names a model; the report carries it, not the environment's
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** — `Find an account launch's transcript under its own directory (#1806)`
