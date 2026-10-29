# Vendor accounts at launch, PR 3 (desktop app) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop app lets a person pick a Claude or Codex account when a hosted session starts, remembers the pick per repository, manages this machine's accounts from a Settings tab, and names the account a session runs under beneath the chat composer.

**Architecture:** The app reads two account sources and never mixes them up: the local daemon's socket (`AccountsSubscribe` → `AccountsSnapshotDto`) for this machine, and the server (`GetDaemonAccounts`, nudged by `DaemonAccountsChanged`) for every machine's accounts plus, per listed daemon, whether it honours an explicit account. A pure `AccountChooser` holds the selection state machine of spec Section 6.3 and is driven by `HomeViewModel`; the launch request carries `account_id`, null for the default. Account actions run `kcap accounts … --json` through the existing `DaemonMutationLane`, with a fresh request id per action and success confirmed by a snapshot at or past the revision the CLI committed. The composer strip reads the resolved account off `AgentStatusDto` locally and off the agent row remotely.

**Tech Stack:** .NET 10, Avalonia + ReactiveUI, TUnit (`test/Capacitor.App.Tests.Unit`, `test/Capacitor.Cli.Core.Tests.Unit`, `test/Capacitor.Remote.Models.Tests.Unit`), the kcap-server contracts in `src/Capacitor.Remote.Models`.

**Spec:** `docs/superpowers/specs/2026-10-21-vendor-accounts-launch-choice-design.md` Sections 3, 4.1, 4.4, 5, 6, 7, 8 (without bars), 10 item 3 and 11 (the "App" bullets), over `docs/superpowers/specs/2026-10-07-vendor-accounts-and-limits-design.md` Sections 3.2, 3.4, 4.3, 6.3 and 6.4. Read both before any task. The daemon and CLI half is on this branch's base (PR #1870); the server half is kcap-server PR #2641, whose wire shapes are quoted where a task reads them.

## Global Constraints

- The daemon's local socket speaks `FrameType.AccountsSubscribe` (32) and `FrameType.AccountsSnapshot` (87); the capability token is `"accounts/1"`; the snapshot is `AccountsSnapshotDto(string HostId, long Revision, AccountViewDto[] Accounts, DefaultDirectoryDto[] Defaults)` with `AccountViewDto(Id, Vendor, Directory, DirectoryKey, Label, RecordingState)` and `DefaultDirectoryDto(Vendor, Directory, DirectoryKey)` in `Capacitor.Cli.Core.Accounts`, serialized with `AccountsSnapshotJsonContext` (snake_case). Recording-state tokens are the constants in `RecordingStates` (`recording`, `installed`, `broken`, `not_wired`). A snapshot has no row for a default: readers match `DefaultDirectoryDto.DirectoryKey` against `AccountViewDto.DirectoryKey` by ordinal equality and never normalize or case-fold a path themselves.
- Server wire (snake_case, kcap-server PR #2641): the listed daemon gains `supports_account_launches` (bool) and `vendor_models_by_account` (map of registry id to `[{value,label}]`); `GetDaemonAccounts()` returns rows `{daemon_name, host_id, revision, accounts:[{id,vendor,directory,directory_key,label,recording_state}], defaults:[{vendor,directory,directory_key}]}`, owner-scoped; `DaemonAccountsChanged` is a payload-free push; `RequestLaunchAgentV2` takes `account_id` (null or absent = default; refused with `account_launch_unsupported` for a daemon without the flag); the agent row gains `account_directory`, `account_id`, `account_label`. Hub arities never change: new capability is a new method or a new member on the record.
- Null account id means the default. An explicit id is honoured or refused; no screen and no hop substitutes the default for it. A restore from memory may fall back to the default silently; a choice the person made may not.
- Support knowledge per target is Unknown / Unsupported / Supported, read only from the server's daemon listing (local target included, through its own row in that listing), never from the local socket. Unknown is never read as Unsupported.
- `kcap accounts` verbs: `add <claude|codex> <dir>`, `remove <idPrefix|dir>`, `rename <idPrefix|dir> <label>`, `rewire [<idPrefix|dir>]`, each with `--json` printing one line `AccountActionResultDto(Action, AccountId, Succeeded, Steps:[WiringStepDto(Name, Succeeded, Detail)], Revision, Error)` (`AccountActionResultJsonContext` in `Capacitor.Cli.Core.Accounts`), exit 0 when `Succeeded`. In the app a failed step is a failure even when `Succeeded` is true.
- Desktop-app rules (CLAUDE.md): `KcapSuccess*`/`KcapWarning*` only on status; the Default badge and labels are identity and take neutral styling; controls opt into `kcapPanel`, `kcapGhost`, `kcapChip`, `kcapField`; Lucide icons through `AppIcon`; filter `SourceCache.Connect()` before `ObserveOn(RxSchedulers.MainThreadScheduler)`; no uncached I/O on the UI thread.
- Code rules: one type per file; a new parameter goes before the last one unless positional callers force it last; `FrozenDictionary<K,V>.Empty` for an empty read-only map; `UserHome`, never `Environment.GetFolderPath`; comments scarce and non-historical; tests use Helpers' `TempDir`, `EnvScope`, `[NotInParallel("AvaloniaSession")]` around `AvaloniaSession.WithImmediateRxScheduler`. Commit subjects: one imperative clause `(#1806)`, at most 80 characters, with a `Co-Authored-By` trailer.
- README: the desktop-app section (around the **Notifications** tab paragraph) gains the **Accounts** tab in the same PR (Task 12).

## Review Focus

- A machine with no `accounts.json` publishes revision 0 with no accounts: the launcher must resolve to the default and enable Start, with no account section. Pinned in Task 9.
- The selected vendor is neither Claude nor Codex: no section, no gating, and the request carries null. Pinned in Tasks 8 and 9.
- The server's listing has answered but the local daemon is not in it yet: support is Unknown, not Unsupported, and Start waits rather than launching under a silent default. Pinned in Task 9.
- The CLI prints a warning line to stdout ahead of the JSON result: the lane reads the last line that parses, not the first line. Pinned in Task 11.
- The Settings tab's actions while the daemon is connected without `accounts/1`, or not connected: the actions are not offered, so nothing can run unconfirmed. Pinned in Task 12.

Run one test class with: `dotnet run --project test/<Project>/<Project>.csproj -- --treenode-filter "/*/*/<ClassName>/*"`. The App suite is `test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`.

---

### Task 1: The server wire shapes the app reads

**Files:**
- Modify: `src/Capacitor.Remote.Models/DaemonInfo.cs` (append two members), `src/Capacitor.Remote.Models/AgentInstanceDto.cs` (append three), `src/Capacitor.Remote.Models/RemoteWire.cs` (`HubMethods.GetDaemonAccounts`, `HubBroadcasts.DaemonAccountsChanged`), `src/Capacitor.Remote.Models/RemoteModelsJsonContext.cs`
- Create: `src/Capacitor.Remote.Models/DaemonAccountsDto.cs`, `src/Capacitor.Remote.Models/ListedAccountDto.cs`, `src/Capacitor.Remote.Models/ListedDefaultDirectoryDto.cs`
- Test: `test/Capacitor.Remote.Models.Tests.Unit/DaemonAccountsWireTests.cs` (model: whichever existing test there deserializes `DaemonInfo`; find it with `/usr/bin/grep -rl supported_vendors test/Capacitor.Remote.Models.Tests.Unit`)

**Interfaces:**
- Produces on `DaemonInfo`: `[JsonPropertyName("supports_account_launches")] public bool SupportsAccountLaunches { get; init; }` and `[JsonPropertyName("vendor_models_by_account")] public Dictionary<string, VendorModelOptionDto[]>? VendorModelsByAccount { get; init; }`.
- Produces on `AgentInstanceDto`: `account_directory` → `string? AccountDirectory`, `account_id` → `string? AccountId`, `account_label` → `string? AccountLabel`.
- Produces: `public sealed record DaemonAccountsDto { [JsonPropertyName("daemon_name")] public required string DaemonName; [JsonPropertyName("host_id")] public required string HostId; [JsonPropertyName("revision")] public long Revision; [JsonPropertyName("accounts")] public ListedAccountDto[]? Accounts; [JsonPropertyName("defaults")] public ListedDefaultDirectoryDto[]? Defaults; }` (all `{ get; init; }`), `ListedAccountDto` (`id`, `vendor`, `directory`, `directory_key`, `label`, `recording_state`, all `required string`), `ListedDefaultDirectoryDto` (`vendor`, `directory`, `directory_key`).
- Produces: `HubMethods.GetDaemonAccounts = "GetDaemonAccounts"`, `HubBroadcasts.DaemonAccountsChanged = "DaemonAccountsChanged"`; `RemoteModelsJsonContext` gains `[JsonSerializable(typeof(DaemonAccountsDto))]` and `[JsonSerializable(typeof(List<DaemonAccountsDto>))]`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] DaemonInfo_binds_account_support_and_per_account_models()
  // the server's own golden row: {"name":"box","platform":"linux","repo_paths":["/repo"],"connected":true,"owner_user_id":"github:1","version":"1.2.3","supported_vendors":["claude","codex"],"machine_id":"machine-1","vendor_models":{"claude":[{"value":"opus","label":"Opus"}]},"supports_account_launches":true,"vendor_models_by_account":{"c1":[{"value":"gpt-5","label":"GPT-5"}]}}
  // → SupportsAccountLaunches true; VendorModelsByAccount["c1"].Single().Value == "gpt-5"
[Test] DaemonInfo_from_an_older_server_binds_false_and_null()   // the same row without the two keys
[Test] AgentInstance_binds_the_account_fields()
  // {"agent_id":"a","status":"running","account_directory":"/u/.claude-work","account_id":"a1","account_label":"work"} → the three members; the same without them → all null
[Test] DaemonAccounts_binds_the_servers_row()
  // {"daemon_name":"box","host_id":"h1","revision":7,"accounts":[{"id":"a1","vendor":"claude","directory":"/u/.claude-work","directory_key":"/u/.claude-work","label":"work","recording_state":"recording"}],"defaults":[{"vendor":"claude","directory":"/u/.claude","directory_key":"/u/.claude"}]}
  // deserialized through RemoteModelsJsonContext.Default.DaemonAccountsDto → every member as sent
```

- [ ] **Step 2: Run to verify they fail** — compile errors on the new members and types.
- [ ] **Step 3: Add the members, records, constants and context entries.**
- [ ] **Step 4: Run `DaemonAccountsWireTests` and the whole `Capacitor.Remote.Models.Tests.Unit` suite to verify they pass.**
- [ ] **Step 5: Commit** — `Read the server's account fields in the desktop wire models (#1806)`

### Task 2: The local accounts subscription

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/AccountsStreamEvent.cs`, `src/Capacitor.Cli.Core/LocalIpc/AccountsSubscription.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AccountsSubscriptionTests.cs` (rig copied from `ConsentSubscriptionTests.cs`: a listening Unix socket in a `TempDir`, a `DaemonStore` pointed at it, frames written with `FrameCodec`)

**Interfaces:**
- Consumes: `FrameType.AccountsSubscribe`, `FrameType.AccountsSnapshot`, `LocalFrame.AccountsSubscribe()`, `FrameCodec.WriteAsync/ReadAsync`, `AccountsSnapshotJsonContext.Default.AccountsSnapshotDto`.
- Produces: `public abstract record AccountsStreamEvent { public sealed record Subscribed : AccountsStreamEvent; public sealed record Snapshot(AccountsSnapshotDto Value) : AccountsStreamEvent; }`
- Produces: `public static class AccountsSubscription { public static IAsyncEnumerable<AccountsStreamEvent> RunAsync(DaemonStore store, string daemonName, [EnumeratorCancellation] CancellationToken ct = default); }` — shape of `PermissionSubscription.RunAsync`: connect, write the subscribe frame, yield `Subscribed`, then yield a `Snapshot` per `AccountsSnapshot` frame. A frame of any other type, a `JsonException`, an `IOException`/`SocketException`/`InvalidDataException` or end of stream ends the enumeration; caller cancellation propagates. A snapshot that deserializes to null, or whose `HostId` is blank or `Revision` negative, is skipped (`continue`), since ending there would make every resubscribe replay it.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] A_subscribe_yields_subscribed_then_each_snapshot_frame()
  // the server side writes two AccountsSnapshot frames (revision 1 then 2) → events: Subscribed, Snapshot(rev 1), Snapshot(rev 2)
[Test] A_snapshot_without_identity_is_skipped_not_fatal()   // frame with "host_id":"" then one with "h1" → only the second is yielded
[Test] A_foreign_frame_ends_the_attempt()                    // a StatusSnapshot frame after Subscribed → the enumeration completes
[Test] A_closed_socket_ends_the_attempt_without_throwing()
[Test] Caller_cancellation_throws_OperationCanceledException()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `AccountsSubscriptionTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Subscribe to the daemon's accounts snapshot from the app side (#1806)`

### Task 3: The app's local accounts service

**Files:**
- Create: `src/Capacitor.App/Services/IAccountsService.cs`, `src/Capacitor.App/Services/AccountsService.cs`
- Test: `test/Capacitor.App.Tests.Unit/AccountsServiceTests.cs` (model: `PermissionServiceTests.cs` — a `FakeDaemonClientService`, a scripted `Func<CancellationToken, IAsyncEnumerable<AccountsStreamEvent>>` over a `Channel`, `FakeTimeProvider`)

**Interfaces:**
- Consumes: `IDaemonClientService.Status` (`AttachStatus(State, Reason, Capabilities, …)`), `AccountsStreamEvent` (Task 2).
- Produces:
  ```csharp
  public interface IAccountsService {
      /// Replay-1. The connected daemon's latest snapshot: null until it advertises accounts/1 and the first one
      /// arrives, and null again when the connection or the capability goes.
      IObservable<AccountsSnapshotDto?> Snapshot { get; }
      /// Replay-1. Whether the connected daemon advertises accounts/1; false while not connected.
      IObservable<bool> Supported { get; }
      /// True once a snapshot at or past revision is held, false when timeout passes first or the snapshot
      /// goes null (the subscription was lost) before one arrives.
      Task<bool> ReachedAsync(long revision, TimeSpan timeout, CancellationToken ct);
  }
  public sealed class AccountsService(
      IDaemonClientService service, Func<CancellationToken, IAsyncEnumerable<AccountsStreamEvent>> subscribe,
      TimeProvider time, CancellationToken shutdownToken) : IAccountsService, IDisposable
  ```
  `const string Capability = "accounts/1"`, retry delay 1 s between attempts, loop start/stop on `Status` exactly as `PermissionService.OnStatus`/`StartLoop`/`StopLoop`/`RunLoopAsync`. `Subscribed` does not clear the held snapshot (the daemon resends it); `StopLoop` publishes null.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] No_subscription_without_the_capability()                       // Connected with ["status/1"] → subscribe never invoked, Supported false, Snapshot null
[Test] A_snapshot_is_published_once_the_daemon_sends_it()            // Connected with ["accounts/1"] → Supported true; the stream yields rev 3 → Snapshot.Revision 3
[Test] Losing_the_connection_publishes_null_and_false()
[Test] An_attempt_that_ends_is_retried_after_a_second_and_keeps_the_snapshot_meanwhile()
[Test] ReachedAsync_answers_true_for_a_later_revision_false_on_timeout_and_false_when_the_snapshot_drops()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `AccountsServiceTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Hold the local daemon's accounts snapshot in the app (#1806)`

### Task 4: The server lane lists accounts and says when a listing answered

**Files:**
- Modify: `src/Capacitor.App/Services/IServerLane.cs`, `src/Capacitor.App/Services/ServerConnectionService.cs` (the `hub.On` block near `HubBroadcasts.DaemonsChanged` and `GetConnectedDaemonsAsync`), `src/Capacitor.App/Services/RemoteAgentsService.cs`
- Create: `src/Capacitor.App/Services/DaemonListing.cs`
- Modify: `test/Capacitor.App.Tests.Unit/FakeServerLane.cs` (`DaemonAccountsChangedSubject`, `DaemonAccountsHandler`), `test/Capacitor.App.Tests.Unit/FakeRemoteAgents.cs` (`ListingSubject`, `DaemonAccountsSubject`, a no-op `RefreshDaemonsAsync`)
- Test: `test/Capacitor.App.Tests.Unit/RemoteAgentsServiceTests.cs`

**Interfaces:**
- Produces on `IServerLane`: `IObservable<System.Reactive.Unit> DaemonAccountsChanged { get; }` and `Task<IReadOnlyList<DaemonAccountsDto>?> GetDaemonAccountsAsync(CancellationToken ct)` (null with no live hub; `InvokeAsync<List<DaemonAccountsDto>>(HubMethods.GetDaemonAccounts, ct)`, errors to null as the daemons call does).
- Produces: `public sealed record DaemonListing(IReadOnlyList<DaemonInfo>? Daemons, int Generation, bool FetchFailed);` — `Daemons` null means the current connection generation has not answered.
- Produces on `IRemoteAgentsService`: `IObservable<DaemonListing> Listing { get; }` (replay-1, seeded `new(null, 0, false)`), `IObservable<IReadOnlyList<DaemonAccountsDto>> DaemonAccounts { get; }` (replay-1, seeded `[]`), `Task RefreshDaemonsAsync()` (fetches now; the Retry action).
- Behaviour in `RemoteAgentsService`: every Connected status that bumps `_generation` publishes `Listing(null, generation, false)`; a daemons fetch that returns a list publishes it on both `Daemons` and `Listing(list, generation, false)`; a fetch that fails (null or throw) publishes `Listing(null, generation, true)`, leaves `Daemons` as it was, and while the lane is still Connected schedules another fetch after 3 s, then 6, 12, 24 and 30 s thereafter, the sequence reset by a successful fetch or a new Connected status. `DaemonAccounts` refreshes on Connected and on `lane.DaemonAccountsChanged.Throttle(wait)` with the same single-flight, generation-guarded shape as the daemons refresh; a failed fetch keeps the last list; a subject change clears it to `[]` with the other caches.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] A_connect_publishes_an_unanswered_listing_then_the_list()          // Listing: (null,g,false) then (rows,g,false)
[Test] A_failed_fetch_publishes_FetchFailed_and_retries_with_backoff()    // DaemonsHandler fails twice then answers; FakeTimeProvider advances 3 s, 6 s → three calls, final Listing answered
[Test] A_reconnect_returns_the_listing_to_unanswered_until_it_answers()   // Retrying then Connected → (null, g+1, false) before the new list
[Test] RefreshDaemonsAsync_fetches_at_once()
[Test] A_result_from_a_superseded_generation_is_dropped()                 // handler held across a reconnect, old result never publishes
[Test] Daemon_accounts_are_seeded_on_connect_refreshed_on_the_push_and_cleared_on_a_subject_change()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement**, including `FakeServerLane` and `FakeRemoteAgents`.
- [ ] **Step 4: Run `RemoteAgentsServiceTests` and `ServerConnectionServiceTests` (if present) to verify they pass.**
- [ ] **Step 5: Commit** — `Read each daemon's accounts and listing state from the server lane (#1806)`

### Task 5: The launch carries the account id

**Files:**
- Modify: `src/Capacitor.App/Services/ILaunchClient.cs` (`LaunchRequest`, `LaunchAgentRequestV2Payload`, `LaunchPayload.For`), `src/Capacitor.App/ViewModels/LaunchDraft.cs`
- Test: `test/Capacitor.App.Tests.Unit/LaunchRequestTests.cs`

**Interfaces:**
- Produces: `LaunchRequest(…, CodexLaunchPosture? CodexPosture = null, string? AccountId = null)`; `LaunchAgentRequestV2Payload` member `[JsonPropertyName("account_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AccountId { get; init; }` (omitted when null, as `permission_mode` is); `LaunchPayload.For` sets it from `r.AccountId` with blank read as null; `LaunchDraft(…, int TrayGeneration, string? AccountId = null)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// Wire_keys_are_exactly_the_twelve_hub_fields_when_no_mode_is_chosen stays as it is: a null account adds no key.
[Test] A_chosen_account_adds_the_account_id_key()   // AccountId "a1" → the twelve keys plus "account_id", value "a1"
[Test] A_blank_account_id_is_sent_as_no_key()        // AccountId "  " → the twelve keys
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `LaunchRequestTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Carry a chosen account id on the launch request (#1806)`

### Task 6: Memory per repository, vendor and host

**Files:**
- Modify: `src/Capacitor.App/Services/AppStateStore.cs` (append `IReadOnlyDictionary<string, string>? AccountByRepo = null` after `WebSignUpOpenedAt`)
- Create: `src/Capacitor.App/Services/AccountMemory.cs`
- Test: `test/Capacitor.App.Tests.Unit/AccountMemoryTests.cs`, `test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs` (one round-trip)

**Interfaces:**
- Produces: `public static class AccountMemory` with
  - `public static string KeyFor(string hostId, string vendor, string repoPath)` → `$"{hostId}/{vendor}:{repo}"` where `repo` is `""` for `HomeViewModel.ScratchRepoPath` and `PlatformPaths.Normalize(repoPath)` otherwise (host ids are GUID `N` strings and vendor tokens carry neither `/` nor `:`, so the key splits on the first `/` and the first `:` after it);
  - `public static string? Lookup(IReadOnlyDictionary<string, string>? byRepo, string hostId, string vendor, string repoPath)` — host and vendor matched ordinally, the repo part with `PlatformPaths.Comparer`;
  - `public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string>? existing, string hostId, string vendor, string repoPath, string? accountId)` — replaces every entry matching the three under the same rules; a null `accountId` removes and adds nothing.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] Lookup_matches_the_repo_part_under_the_platform_comparer_and_the_rest_ordinally()
[Test] With_replaces_a_matching_entry_and_null_removes_it()
[Test] The_scratch_key_round_trips()                      // repoPath "" survives With → Lookup
[Test] Entries_for_another_host_or_vendor_are_untouched()
// AppStateStoreTests: Account_memory_round_trips_through_the_file()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run both classes to verify they pass.**
- [ ] **Step 5: Commit** — `Remember the chosen account per repository, vendor and host (#1806)`

### Task 7: Account rows from a snapshot

**Files:**
- Create: `src/Capacitor.App/Services/AccountRow.cs`, `src/Capacitor.App/Services/AccountRows.cs`, `src/Capacitor.App/Services/RecordingStateLabels.cs`, `src/Capacitor.App/Services/DaemonAccountsMapping.cs`
- Test: `test/Capacitor.App.Tests.Unit/AccountRowsTests.cs`

**Interfaces:**
- Produces: `public sealed record AccountRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, bool IsDefault);` — `Id` null is the unregistered default of Section 3.
- Produces: `public static class AccountRows { public static IReadOnlyList<AccountRow> For(AccountsSnapshotDto snapshot, string vendor); }` — the snapshot's accounts of that vendor (`OrdinalIgnoreCase`), ordered by `Label` then `Directory` ordinally, `IsDefault` when the vendor's default `DirectoryKey` equals the row's; when the vendor has a default that matched no row, one more row `new(null, vendor, default.Directory, Path.GetFileName(default.Directory.TrimEnd(separators)), RecordingStates.NotWired, true)` appended last. No default for the vendor → no default row and no badge.
- Produces: `public static class RecordingStateLabels { public static string Label(string vendor, string token); public static bool IsAttention(string token); public static string? Hint(string vendor, string token, string directory); }` — `recording` → "Recording"; `installed` → Codex "Needs trust" (hint `$"Trust the kcap hooks the next time you start Codex with CODEX_HOME={directory}"`), Claude "Wired"; `broken` → "Broken"; `not_wired` → "Not wired"; any other token → the token. Attention is true for `installed` on Codex, `broken` and `not_wired`.
- Produces: `public static class DaemonAccountsMapping { extension(DaemonAccountsDto row) { public AccountsSnapshotDto ToSnapshot(); } }` — null arrays read as empty; row members copied as sent.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] Rows_are_the_vendors_accounts_with_the_default_badge_on_the_matching_key()
[Test] An_unregistered_default_is_one_more_row_labelled_by_directory_name_and_not_wired()
[Test] A_vendor_without_a_default_has_no_badge_and_no_extra_row()
[Test] Only_one_row_results_from_an_empty_registry_with_a_default()           // the section threshold case
[Test] [Arguments("codex","installed","Needs trust",true)] [Arguments("claude","installed","Wired",false)] [Arguments("claude","broken","Broken",true)] [Arguments("claude","not_wired","Not wired",true)] [Arguments("claude","recording","Recording",false)]
       Labels_follow_the_token_and_vendor(string vendor, string token, string label, bool attention)
[Test] A_server_row_maps_to_the_snapshot_shape_with_null_arrays_as_empty()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `AccountRowsTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Derive a vendor's account rows and badges from a snapshot (#1806)`

### Task 8: The selection state machine

**Files:**
- Create: `src/Capacitor.App/ViewModels/AccountSupport.cs` (enum), `src/Capacitor.App/ViewModels/AccountSelectionState.cs` (enum), `src/Capacitor.App/ViewModels/AccountChoice.cs`, `src/Capacitor.App/ViewModels/AccountChooser.cs`
- Test: `test/Capacitor.App.Tests.Unit/AccountChooserTests.cs`

**Interfaces:**
- Consumes: `AccountRows.For` (Task 7), `AccountMemory` key shape (Task 6) through the two delegates below.
- Produces: `public enum AccountSupport { Unknown, Unsupported, Supported }`, `public enum AccountSelectionState { Pending, Default, ExplicitValid, ExplicitInvalid }`, `public sealed record AccountChoice(AccountRow Row, bool Selected, bool Removed);`
- Produces:
  ```csharp
  public sealed class AccountChooser {
      public AccountChooser(
          Func<string, string, string, string?> remembered,          // (hostId, vendor, repoPath) → account id
          Action<string, string, string, string?> remember);         // null forgets
      public void EnterTarget(string repoPath, string vendor);       // a new repository, vendor or machine; clears the pick flag and re-resolves
      public void SetSupport(AccountSupport support, bool fetchFailed);
      public void SetSnapshot(AccountsSnapshotDto? snapshot);         // null = no complete snapshot at hand
      public void SetMemoryLoaded(bool loaded);                        // false keeps every target Pending: a restore may not run before memory is read
      public void Pick(string? accountId);                             // null = the Default row; writes memory
      public void UseDefault();                                        // same as Pick(null)
      public AccountSelectionState State { get; }
      public bool IsResolved => State is AccountSelectionState.Default or AccountSelectionState.ExplicitValid;
      public string? SelectedAccountId { get; }                        // the explicit id while ExplicitValid or ExplicitInvalid
      public string? SelectedLabel { get; }                            // valid: the row's label; invalid: "{label} — unavailable"; else null
      public bool ShowsSection { get; }
      public IReadOnlyList<AccountChoice> Rows { get; }
      public string? PendingText { get; }                              // "Loading accounts…" while Pending, else null
      public bool OffersRetry { get; }                                 // Pending and the last listing fetch failed
      public string? InvalidReason { get; }                            // ExplicitInvalid: "{label} is not available on this machine"
      public event Action? Changed;                                    // after every state recomputation
  }
  ```
  Rules, from Sections 6.1, 6.3 and 6.4:
  - A vendor other than `claude` or `codex` is always `Default`, shows no section and ignores picks.
  - Not picked on this target yet: memory not loaded, or support Unknown, or Supported without a snapshot → `Pending`; Unsupported → `Default`, memory untouched; Supported with a snapshot → restore: `remembered(snapshot.HostId, vendor, repoPath)` null → `Default`; an id the snapshot lists → `ExplicitValid`; an id it does not list → `remember(…, null)` then `Default`.
  - Picked on this target: a null pick → `Default` and `remember(hostId, vendor, repo, null)` when a snapshot is at hand; an explicit pick → `remember(…, id)` when a snapshot is at hand, and the state is `ExplicitValid` while Supported and the snapshot lists the id, `ExplicitInvalid` otherwise, through every later support or snapshot change; a Default pick stays `Default` through them too.
  - A memory entry whose id is absent from a snapshot of its own host is forgotten whenever that is observed, the live pick's state notwithstanding.
  - `Rows`: `AccountRows.For(snapshot, vendor)` as `AccountChoice`, `Selected` on the default row while `Default`, on the row whose `Id` equals `SelectedAccountId` while explicit; while `ExplicitInvalid` one more `AccountChoice(new AccountRow(SelectedAccountId, vendor, "", label, "", false), Selected: true, Removed: true)` where `label` is the last label seen for that id, else the id. Empty without a snapshot.
  - `ShowsSection`: `Pending`, or Supported with a snapshot and (`Rows.Count >= 2` or `ExplicitInvalid`). Unsupported → false.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] A_vendor_without_accounts_is_default_with_no_section()                       // "pi"
[Test] Unknown_support_is_pending_with_the_loading_text()
[Test] Unsupported_is_default_and_keeps_the_memory_entry()
[Test] Supported_without_a_snapshot_is_pending_until_the_snapshot_arrives_then_restores()
[Test] Memory_not_yet_loaded_keeps_the_target_pending()
[Test] A_remembered_id_that_the_snapshot_lists_is_explicit_valid_and_selected()
[Test] A_remembered_id_absent_from_its_hosts_snapshot_is_forgotten_and_the_default_restored()
[Test] A_snapshot_from_another_host_restores_nothing_and_forgets_nothing()         // remembered under h1, snapshot h2 → Default, remember not called
[Test] A_pick_writes_memory_and_the_default_pick_removes_it()
[Test] A_late_snapshot_does_not_override_a_pick_made_since()
[Test] Support_withdrawn_or_unknown_again_turns_a_pick_explicit_invalid_not_default()
[Test] The_picked_account_removed_from_the_snapshot_is_invalid_until_it_returns()
[Test] Use_default_resolves_an_invalid_pick()
[Test] The_section_shows_at_two_rows_not_one_and_stays_while_invalid()
[Test] Entering_another_target_re_restores_that_targets_entry()                    // repo or vendor change
[Test] Retry_is_offered_only_while_pending_after_a_failed_fetch()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `AccountChooserTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Resolve the launcher's account selection from support, snapshot and memory (#1806)`

### Task 9: The launcher view-model chooses, gates and sends the account

**Files:**
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs` (the `MachineOption` record declared at its top gains a trailing `IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>? VendorModelsByAccount = null`, filled wherever a `MachineOption` is built from a `DaemonInfo` through `VendorModelMaps.FromRegistry(d.VendorModelsByAccount)`; `VendorModelMaps.FromStatus` maps the local dto's per-account dictionary unchanged)
- Modify: `test/Capacitor.App.Tests.Unit/FakeDaemonClientService.cs` (`Snap(…, Dictionary<string, VendorModelOption[]>? vendorModelsByAccount = null)` before `credential`)
- Test: `test/Capacitor.App.Tests.Unit/HomeViewModelAccountTests.cs` (new class; rig copied from `HomeViewModelTests.Build` and the remote tests' `daemons`/`viewerId`/`laneStatus` arguments), `test/Capacitor.App.Tests.Unit/HomeAttachmentsTests.cs` (two tests on the upload seam)

**Interfaces:**
- Consumes: `AccountChooser` (Task 8), `DaemonListing`, `IRemoteAgentsService.DaemonAccounts`, `IAccountsService.Snapshot` (Tasks 3, 4), `AccountMemory` (Task 6), `DaemonAccountsMapping.ToSnapshot` (Task 7), `LaunchDraft.AccountId`/`LaunchRequest.AccountId` (Task 5).
- Produces: `HomeViewModel` constructor parameters before `launchFailed`: `IObservable<DaemonListing>? listing = null, IObservable<IReadOnlyList<DaemonAccountsDto>>? daemonAccounts = null, IObservable<AccountsSnapshotDto?>? localAccounts = null, Func<Task>? retryListing = null`.
- Produces: `internal static AccountSupport SupportFor(DaemonListing listing, string machine, string? viewerId)` — `listing.Daemons` null → Unknown; `FindMachine(listing.Daemons, machine, viewerId)` null → Unknown; else Supported/Unsupported by `SupportsAccountLaunches`. The local target asks for `_daemon.DaemonName` under the viewer id, the same name-and-owner match a remote uses.
- Produces (bound by the view): `bool AccountSection`, `IReadOnlyList<AccountChoice> AccountRows`, `string? AccountPendingText`, `bool AccountRetryVisible`, `string? SelectedAccountLabel`, `bool StartErrorOffersDefault`, `ReactiveCommand<Unit, Unit> RetryAccountsCommand`, `ReactiveCommand<Unit, Unit> UseDefaultAccountCommand`, `Task PickAccountAsync(string? accountId)`.
- Pipeline: `_machineSelectionChanges`, `_selectedRepoPathChanges`, `WhenAnyValue(x => x.SelectedVendor)`, `listing` (seeded `new(null, 0, false)`), `daemonAccounts` (seeded `[]`), `localAccounts` (seeded null) combined, observed on `RxSchedulers.MainThreadScheduler`, then: `EnterTarget` when repo, vendor or machine changed; `SetSupport(SupportFor(listing, machine, _lastViewerId), listing.FetchFailed)`; `SetSnapshot(sel.Remote ? daemonAccounts.FirstOrDefault(d => d.DaemonName == sel.Name)?.ToSnapshot() : local)`. Memory: `_state.LoadAsync()` once at construction into an in-memory mirror, then `SetMemoryLoaded(true)`; `remember` updates the mirror and `_state.UpdateAsync(s => s with { AccountByRepo = AccountMemory.With(…) })`; a remote target writes memory too (the key carries the host id, so this is not the local-only `HarnessByRepo` rule).
- Start: `canLaunch` gains `accountResolved` (chooser `IsResolved`, republished on `Changed`); `TipFor` gains a third input so that an otherwise-ready launcher whose account is pending shows "Loading accounts…". `StartAsync`: before the draft, `State == ExplicitInvalid` → `StartError = $"{InvalidReason}. Use the default account or pick another."`, `StartErrorOffersDefault = true`, return. The draft captures `AccountId = State == ExplicitValid ? SelectedAccountId : null`. After the upload (and, with no files, right before the request) `AccountErrorFor(draft)`: a non-null `draft.AccountId` whose target is no longer Supported or whose latest snapshot no longer lists it → the same refusal, draft kept, nothing sent. `UseDefaultAccountCommand` → `chooser.UseDefault()`, clears `StartError` and `StartErrorOffersDefault`. Any `StartError` set elsewhere resets `StartErrorOffersDefault` to false.
- Catalog: `EffectiveModelCatalog` takes one more input — the target's per-account map (`s.Daemon.VendorModelsByAccount` through `VendorModelMaps.FromStatus` locally, `FindMachine(...)?.VendorModelsByAccount` remotely) and the chooser's `SelectedAccountId` while `ExplicitValid` — and overrides the selected vendor's entry with that account's list when the map has one. `ShouldClearSelection` then resets a model the account's list withdraws, unchanged.

- [ ] **Step 1: Write the failing tests** (every test inside `AvaloniaSession.WithImmediateRxScheduler`, `[NotInParallel("AvaloniaSession")]`)

```csharp
// Section and rows
[Test] The_section_shows_at_two_rows_and_not_at_one_counting_an_unregistered_default()
[Test] The_section_is_absent_without_a_complete_snapshot_and_for_a_daemon_listed_without_the_flag()
[Test] An_empty_registry_on_a_supported_target_resolves_to_the_default_and_enables_Start()     // revision 0, no accounts
[Test] A_vendor_without_accounts_never_gates_Start_and_sends_null()                             // "pi"
// Chip and request
[Test] The_chip_label_carries_the_account_only_when_it_is_not_the_default()                     // SelectedAccountLabel null → "work"
[Test] The_request_carries_null_for_the_default_and_the_id_for_an_explicit_pick()
// Restore and memory
[Test] Restore_runs_on_a_repository_vendor_or_machine_change_once_the_snapshot_is_complete()
[Test] A_late_snapshot_restores_unless_the_person_has_picked_since()
[Test] An_entry_absent_from_its_hosts_snapshot_is_dropped_and_one_survives_a_missing_snapshot_a_disconnect_and_a_switch_to_another_host()
[Test] The_default_pick_removes_the_entry_and_a_vendor_switch_restores_the_other_vendors_entry()
[Test] Two_daemons_on_one_host_share_one_memory()                                               // same host id, different daemon names
// Support knowledge
[Test] A_remembered_local_account_behind_an_old_server_restores_nothing_and_sends_null_only_once_the_listing_answered()
[Test] A_ready_local_daemon_with_a_delayed_or_failed_first_listing_captures_no_draft_until_the_listing_and_snapshot_resolve()
      // then the retry (listing re-answers) restores the remembered account and Start is available without a reconnect
[Test] A_listing_that_answered_without_the_local_daemon_leaves_support_unknown_and_Start_waiting()
[Test] A_reconnect_returns_support_to_unknown_and_Start_waits()
[Test] A_result_from_a_superseded_connection_is_ignored()                                        // Listing generation guard observed through the VM
[Test] A_choice_made_on_a_supported_target_survives_support_withdrawal_as_explicit_invalid_and_Start_refuses_named()
[Test] Start_before_the_first_complete_snapshot_of_a_supported_target_is_disabled_and_captures_no_draft()
[Test] Removing_the_only_non_default_account_keeps_the_section_and_Use_default_lets_the_next_Start_succeed()
// Catalog
[Test] Model_rows_follow_the_selected_accounts_catalog_then_the_machines_then_the_curated_list()
// HomeAttachmentsTests
[Test] An_account_removed_during_the_upload_fails_named_with_no_request_and_the_draft_kept()
[Test] A_snapshot_arriving_during_an_in_flight_upload_changes_nothing_about_the_launch()
[Test] A_target_that_loses_the_flag_during_the_upload_fails_named_and_sends_nothing()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `HomeViewModelAccountTests`, `HomeViewModelTests` and `HomeAttachmentsTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Choose, gate and send the account from the launcher (#1806)`

### Task 10: The Agent panel's account rows, the chip and Use default

**Files:**
- Modify: `src/Capacitor.App/Views/LauncherPaneView.axaml.cs` (`OnAgentChipClick`, `AgentChipTextConverter`), `src/Capacitor.App/Views/LauncherPaneView.axaml` (the `AgentChipLabel` MultiBinding gains `<Binding Path="SelectedAccountLabel" />`; `StartErrorBanner` gains a `Button x:Name="UseDefaultAccountButton" Classes="kcapChip" Content="Use default" Command="{Binding UseDefaultAccountCommand}" IsVisible="{Binding StartErrorOffersDefault}"` in a third column)
- Test: `test/Capacitor.App.Tests.Unit/LauncherPaneViewSmokeTests.cs`, `test/Capacitor.App.Tests.Unit/AgentChipTextConverterTests.cs` (new, or beside the converter's existing tests if any)

**Interfaces:**
- Consumes: `HomeViewModel.AccountSection`, `AccountRows`, `AccountPendingText`, `AccountRetryVisible`, `RetryAccountsCommand`, `PickAccountAsync`, `SelectedAccountLabel`, `StartErrorOffersDefault`, `UseDefaultAccountCommand` (Task 9).
- Produces in `OnAgentChipClick`'s `RebuildRows`, with no search term only: when `vm.AccountSection`, before the model rows: a `TextBlock` "Account" (class `sectionMeta`, from `SectionStyles.axaml`), then either one muted line `vm.AccountPendingText` with a `kcapChip` "Retry" button (`RetryAccountsCommand`) when `vm.AccountRetryVisible`, or one `AccountRow(AccountChoice)` button per row: title = `Row.Label`, plus a neutral "Default" pill (the `RepositoryRow` pill shape, `KcapSurfaceRaisedBrush` background, `KcapBorderBrush` border, `KcapMutedBrush` text) when `Row.IsDefault`, plus " — removed" in muted text when `Removed`; a second line with `Row.Directory` in faint 10.5 text and, when the token is not `recording`, `RecordingStateLabels.Label` in `KcapWarningBrush` when `IsAttention` else muted; `SelectionCheck(Selected)` in the trailing column; `AutomationProperties.Name = ChoiceName(label, selected)`; click → `await vm.PickAccountAsync(Row.Id); RebuildRows();` (the panel stays open: picking an account re-lists the models for it). Then `ChoiceSeparator()`. A search term hides the section (the field does not match accounts).
- Produces: `AgentChipTextConverter.Convert` reads a fifth value, `string? account`, and appends ` · {account}` when it is non-null.

- [ ] **Step 1: Write the failing tests**

```csharp
// AgentChipTextConverterTests
[Test] The_label_is_vendor_and_model_and_gains_the_account_when_one_is_chosen()   // ["Claude · Opus", "Claude · Opus · work"]
// LauncherPaneViewSmokeTests (rig: Build(), OpenMenu(window, view, "AgentChip"))
[Test] The_agent_panel_lists_the_account_rows_with_the_default_pill_and_focus_reaches_them()
      // a VM fed a snapshot with two Claude accounts and a listing with the flag; Tab from the search box lands on the first account row (AutomationProperties.Name ends with ", selected" for the default)
[Test] A_search_term_hides_the_account_section()
[Test] Use_default_shows_in_the_error_banner_only_for_an_account_refusal()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `LauncherPaneViewSmokeTests` and `AgentChipTextConverterTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Offer account rows in the Agent panel and name the account on the chip (#1806)`

### Task 11: Account verbs through the mutation lane

**Files:**
- Modify: `src/Capacitor.App/Services/Mutation/MutationModel.cs` (`MutationVerb` gains `AccountAdd, AccountRemove, AccountRename, AccountRewire` and the extension `IsAccountVerb`; `MutationRequest` gains `AccountArgs? Account = null, string? RequestId = null` after `RetireServiceId`; `MutationOutcome` gains `public sealed record Unconfirmed(string Detail) : MutationOutcome;`), `src/Capacitor.App/Services/Mutation/MutationRequestFactory.cs`, `src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs`, `src/Capacitor.App/Services/KcapCli.cs` (`IKcapCli` and `KcapCli`), every `IKcapCli` fake (`FakeKcapCli` in `DaemonLifecycleControllerTests.cs` and any other implementer the build names), and the lifecycle outcome presenter in `App.axaml.cs` only if the compiler demands an arm for the new case
- Create: `src/Capacitor.App/Services/Mutation/AccountArgs.cs`
- Test: `test/Capacitor.App.Tests.Unit/MutationRequestFactoryTests.cs`, `test/Capacitor.App.Tests.Unit/DaemonMutationLaneTests.cs`, `test/Capacitor.App.Tests.Unit/KcapCliTests.cs`

**Interfaces:**
- Produces: `public sealed record AccountArgs(string? Vendor, string? Directory, string? AccountId, string? Label);`
- Produces: `MutationRequestFactory.ForAccount(MutationVerb verb, AccountArgs args, string daemonName, string? profileName) → MutationRequest` — `Profile = profileName ?? ""`, `CanonicalServer = ""`, `RequestId = Guid.NewGuid().ToString("N")`; a verb that is not an account verb throws `ArgumentOutOfRangeException`. No server is needed: the verbs touch nothing on the server.
- Produces: `IKcapCli.AccountsAsync(MutationVerb verb, AccountArgs args, CancellationToken ct) → Task<ProcessResult>` — argv `accounts add {Vendor} {Directory} --json`, `accounts remove {AccountId} --json`, `accounts rename {AccountId} {Label} --json`, `accounts rewire {AccountId} --json` (`rewire` with a null id omits the argument); `MutationTimeout`; environment `Env()`, not `MutationEnv()`, as `PluginInstallAsync` does, so a null canonical server never throws.
- Produces in the lane: `internal Func<long, CancellationToken, Task<bool>> AccountsRevisionReached { get; set; } = (_, _) => Task.FromResult(false);` (the app wires `IAccountsService.ReachedAsync` with a 10 s budget); `Dispatch` routes account verbs to `AccountsAsync`; `ClassifyOutcomeAsync` routes them to `ClassifyAccountVerbAsync(request, result, ct)`: the last stdout line that deserializes as `AccountActionResultDto` is the result; none → `Unconfirmed("no_result")`; exit code not 0, `Succeeded` false, or any step with `Succeeded` false → `Failed(result.ExitCode, dto?.Error ?? $"{step.Name}: {step.Detail}", RecoverySurface.Attention)`; `Revision` null → `Succeeded`; otherwise `AccountsRevisionReached(revision, ct)` true → `Succeeded`, false → `Unconfirmed("snapshot_not_seen")`. `Deliver` never enqueues an account verb's outcome on the `OutcomeChannel`: the Accounts tab is its surface. Coalescing is untouched: a fresh `RequestId` makes two account requests unequal.

- [ ] **Step 1: Write the failing tests**

```csharp
// MutationRequestFactoryTests
[Test] ForAccount_builds_without_a_server_and_mints_a_request_id_per_call()
[Test] ForAccount_refuses_a_non_account_verb()
// KcapCliTests (FakeProcessRunner records argv)
[Test] [Arguments(...)] Account_verbs_spell_their_argv_with_json(MutationVerb verb, AccountArgs args, string[] expected)
[Test] Account_verbs_run_without_a_canonical_server()
// DaemonMutationLaneTests
[Test] Two_account_requests_never_coalesce()                                          // both reach the executor
[Test] A_failed_step_is_a_failure_even_when_the_cli_says_succeeded()
[Test] Stdout_noise_before_the_result_line_is_ignored()
[Test] A_missing_or_malformed_result_is_unconfirmed()
[Test] A_revision_the_snapshot_never_reaches_is_unconfirmed_and_one_it_reaches_succeeds()
[Test] A_null_revision_with_every_step_succeeded_succeeds()                            // rewire with nothing registered
[Test] An_account_verbs_failure_never_reaches_the_outcome_channel()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the three classes and `DaemonLifecycleControllerTests` (the fake changed) to verify they pass.**
- [ ] **Step 5: Commit** — `Run kcap accounts verbs through the mutation lane (#1806)`

### Task 12: The Accounts tab

**Files:**
- Create: `src/Capacitor.App/ViewModels/AccountsSettingsViewModel.cs`, `src/Capacitor.App/ViewModels/AccountSettingsRow.cs`, `src/Capacitor.App/ViewModels/AccountVendorGroup.cs`, `src/Capacitor.App/Views/AccountsSettingsView.axaml`, `src/Capacitor.App/Views/AccountsSettingsView.axaml.cs`
- Modify: `src/Capacitor.App/Views/SettingsWindow.axaml` (a `TabItem x:Name="AccountsTab"` after `ProfilesTab`, same `IsVisible` shape over `Accounts`), `src/Capacitor.App/ViewModels/SettingsViewModel.cs` (`public AccountsSettingsViewModel? Accounts { get; init; }` beside `Appearance`), `src/Capacitor.App/App.axaml.cs` (`OpenSettings` builds the view-model; `_accounts` comes from Task 14), `README.md` (the desktop section: one paragraph on the **Accounts** tab after the Notifications paragraph)
- Test: `test/Capacitor.App.Tests.Unit/AccountsSettingsViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/SettingsWindowSmokeTests.cs` (every `SelectedIndex = 2 // …Notifications` becomes 3 with the new hidden tab counted, and one new test)

**Interfaces:**
- Consumes: `IAccountsService` (Task 3), `MutationRequestFactory.ForAccount`, `MutationOutcome` (Task 11), `AccountRows`, `RecordingStateLabels` (Task 7), `AccountDiscovery.Find(UserHome, AccountRegistry, Func<string, string?>)` and `AccountCandidate(HarnessId Vendor, string Directory, string Reason)` from Core.
- Produces: `public sealed record AccountSettingsRow(string? Id, string Vendor, string Directory, string Label, string RecordingState, bool IsDefault)` with `StateLabel`, `StateIsAttention`, `StateHint` (through `RecordingStateLabels`), `IsUnregisteredDefault => Id is null`; `public sealed record AccountVendorGroup(string Vendor, string VendorLabel, IReadOnlyList<AccountSettingsRow> Rows);` — one group each for Claude and Codex, in that order, from `AccountRows.For`.
- Produces:
  ```csharp
  public sealed class AccountsSettingsViewModel : ReactiveObject, IDisposable {
      public AccountsSettingsViewModel(
          IAccountsService accounts, Func<MutationRequest, CancellationToken, Task<MutationOutcome>> runMutation,
          string daemonName, string? profileName, Func<IReadOnlyList<AccountCandidate>> discover,
          Func<CancellationToken, Task<string?>> pickDirectory, CancellationToken appLifetime = default);
      public IReadOnlyList<AccountVendorGroup> Groups { get; }
      public bool CanManage { get; }                 // Supported and a snapshot is held
      public string? ReadOnlyNotice { get; }         // "Update kcap on this machine to manage accounts" when connected without accounts/1; "Accounts are listed once the daemon is connected" with no snapshot; else null
      public string? Message { get; }                // the last action's outcome line
      public bool IsBusy { get; }
      public IReadOnlyList<AccountCandidate> Candidates { get; }   // refreshed by RefreshCandidates(), which the Add flyout calls on open
      public void RefreshCandidates();
      public ReactiveCommand<AccountCandidate, Unit> AddCandidateCommand { get; }
      public ReactiveCommand<string, Unit> AddFromPickerCommand { get; }          // parameter: the vendor token; pickDirectory null → no action
      public ReactiveCommand<AccountSettingsRow, Unit> AddDefaultCommand { get; } // the unregistered default row
      public ReactiveCommand<AccountSettingsRow, Unit> RewireCommand { get; }
      public ReactiveCommand<AccountSettingsRow, Unit> RemoveCommand { get; }
      public ReactiveCommand<AccountSettingsRow, Unit> BeginRenameCommand { get; }
      public ReactiveCommand<Unit, Unit> CommitRenameCommand { get; }
      public ReactiveCommand<Unit, Unit> CancelRenameCommand { get; }
      public AccountSettingsRow? Renaming { get; }
      public string RenameDraft { get; set; }
      internal static string OutcomeMessage(MutationVerb verb, string label, MutationOutcome outcome);
  }
  ```
  Every command is gated on `CanManage && !IsBusy`; each builds its request with `MutationRequestFactory.ForAccount` and awaits `runMutation` under `IsBusy`, then sets `Message` from `OutcomeMessage`: Succeeded → `$"{Verb} {label}: done"` in plain words ("Added work", "Removed work", "Renamed work", "Re-wired work"); `Failed` → the reason; `Unconfirmed` → `$"… could not be confirmed; the list shows the current state"`; `Refused` → the reason. `CommitRenameCommand` needs a non-blank draft different from the label.
- View: `ScrollViewer` → `StackPanel` Spacing 16: title `kcapTitle` "Accounts", subtitle `kcapSubtitle` "Claude and Codex config directories on this machine. Each records its sessions and can be picked when a session starts."; `ReadOnlyNotice` as `kcapHint`; per group a `kcapLabel` with the vendor label and an `ItemsControl` of row cards (the `profileRow` card: `KcapSurfaceBrush`, `KcapBorderBrush`, radius 12, padding 20): label `kcapLabel`, the Default pill (as in Task 10), directory `kcapHint`, state `kcapHint` in `KcapWarningBrush` when attention with `ToolTip.Tip = StateHint`; trailing: for a registered row a `kcapGhost` icon button (`AppIcon` `Ellipsis`) whose `Flyout` (`kcapPanel`, the `SessionRailView` ghost-row pattern) lists Rename, Re-wire, Remove; for the unregistered default row a `kcapChip` "Add". While `Renaming` is the row, its label is replaced by a `kcapField` `TextBox` bound to `RenameDraft` with `kcapChip` Save and Cancel. Below the groups a `kcapChip` "Add account" whose flyout lists each candidate (vendor glyph via `VendorGlyphConverter`, directory, muted reason) and the rows "Choose a Claude directory…" and "Choose a Codex directory…". `Message` as `kcapHint` at the bottom. Nothing in the tab takes a status colour except the state line.
- `OpenSettings` builds `new AccountsSettingsViewModel(_accounts, lane.RunAsync, service.DaemonName, settings.ProfileName, discover: () => AccountDiscovery.Find(_userHome, AccountStore.Beside(_daemonStore).TryLoad() ?? new AccountRegistry(), Environment.GetEnvironmentVariable), pickDirectory: ct => the settings window's `StorageProvider.OpenFolderPickerAsync` (the `AddRepositoryAsync` pattern in `LauncherPaneView.axaml.cs`), appLifetime: _shutdown.Token)` and passes it as `Accounts`.

- [ ] **Step 1: Write the failing tests**

```csharp
// AccountsSettingsViewModelTests
[Test] Groups_list_each_vendors_rows_with_the_default_badge_and_the_unregistered_default_as_not_wired_with_add()
[Test] Without_the_capability_the_tab_is_read_only_and_says_to_update_kcap()
[Test] Without_a_snapshot_the_tab_says_the_daemon_must_connect()
[Test] [Arguments(...)] Each_action_builds_its_request_with_the_verb_the_arguments_and_a_fresh_id(…)   // add candidate, add default, add from picker, remove, rename, rewire
[Test] Actions_run_one_at_a_time_and_report_the_outcome()                 // IsBusy while awaiting; Message after Succeeded / Failed / Unconfirmed
[Test] Rename_needs_a_changed_non_blank_label()
[Test] RefreshCandidates_reads_discovery_each_time()
// SettingsWindowSmokeTests
[Test] Accounts_tab_lists_rows_with_the_default_pill_state_and_menu()   // two rows, the Default pill on one, "Needs trust" in warning on a Codex installed row, the Add chip on an unregistered default
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement**, including the README paragraph and the smoke-test index updates.
- [ ] **Step 4: Run `AccountsSettingsViewModelTests`, `SettingsWindowSmokeTests`, `SettingsViewModelTests` to verify they pass.**
- [ ] **Step 5: Commit** — `Manage this machine's accounts from the Settings window (#1806)`

### Task 13: The strip under the composer

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatSessionInfo.cs` (append `string? AccountDirectory = null, string? AccountId = null, string? AccountLabel = null`; `FromLocal` passes the dto's, `FromRemote` the row's), `src/Capacitor.App/Services/AgentRow.cs` (append the same three; `FromLocal` and `FromRemote` pass them), `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (`string? AccountLabel`, `string? AccountDirectory`, `string AccountVendor`, `bool ShowsAccountStrip`, set in `OnSession`), `src/Capacitor.App/Views/ChatTabView.axaml` (the band grid gains a fifth row)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`, `test/Capacitor.App.Tests.Unit/AgentRowTests.cs` (if present; else one test beside the row's existing coverage)

**Interfaces:**
- Produces: `ShowsAccountStrip` is true when the vendor is `claude` or `codex` and `AccountDirectory` is non-null. `AccountLabel` is the info's label, else the directory's name. For an unregistered default the daemon sends a null id and the directory name as the label, so the strip reads label and directory alike.
- Produces in XAML: `Grid RowDefinitions="Auto,Auto,Auto,Auto,Auto"`; `Border x:Name="AccountStrip" Grid.Row="4" Margin="0,6,0,0" IsVisible="{Binding ShowsAccountStrip}"` holding a horizontal `StackPanel` Spacing 8: the vendor glyph (`ContentControl Content="{Binding AccountVendor, Converter={x:Static views:VendorGlyphConverter.Instance}, ConverterParameter=12}"`), `AccountLabel` in `KcapTextBrush` 12, `AccountDirectory` in `KcapMutedBrush` 11.5 with `TextTrimming="CharacterEllipsis"`. No bars, no reading text: those arrive with Part B.

- [ ] **Step 1: Write the failing tests**

```csharp
// ChatTabViewModelTests
[Test] The_strip_shows_for_a_claude_session_with_an_account_and_not_from_an_older_daemon_or_another_vendor()
[Test] An_unregistered_default_shows_its_directory_as_the_label()
[Test] A_remote_row_carries_the_account_onto_the_strip()
// ChatTabViewSmokeTests
[Test] The_account_strip_sits_under_the_composer_card_inside_the_band()
```

- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run the three classes to verify they pass.**
- [ ] **Step 5: Commit** — `Name the session's account under the composer (#1806)`

### Task 14: Wiring and whole-PR verification

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (build `_accounts = new AccountsService(service, ct => AccountsSubscription.RunAsync(_daemonStore, service.DaemonName, ct), _time, _shutdown.Token)` beside the `PermissionService`; set `lane.AccountsRevisionReached = (revision, ct) => _accounts.ReachedAsync(revision, TimeSpan.FromSeconds(10), ct)` once the daemon graph exists; pass `listing: remoteAgents?.Listing, daemonAccounts: remoteAgents?.DaemonAccounts, localAccounts: _accounts.Snapshot, retryListing: remoteAgents is null ? null : remoteAgents.RefreshDaemonsAsync` to `HomeViewModel`; dispose `_accounts` with the other services)
- Test: `test/Capacitor.App.Tests.Unit/AppMutationLaneWiringTests.cs` (one test that the lane's seam is set when the graph builds, if the rig allows; otherwise skip and say so in the commit body)

- [ ] **Step 1:** `dotnet build Capacitor.slnx` — 0 warnings in the projects touched.
- [ ] **Step 2:** Run as executables: `Capacitor.App.Tests.Unit`, `Capacitor.Cli.Core.Tests.Unit`, `Capacitor.Remote.Models.Tests.Unit` — green (the known local failures in `WizardCliIdentityTests`/`AppStartupCarveOutTests` predate this branch; name them in the ledger if they fail).
- [ ] **Step 3:** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — empty (Core changed).
- [ ] **Step 4:** `git diff --numstat origin/capacitor/agent-c32568ac39a64e -- test` — the suite grew only by the pins above.
- [ ] **Step 5: Commit** — `Wire the accounts service into the desktop app (#1806)`
