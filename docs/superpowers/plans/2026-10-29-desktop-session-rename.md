# Desktop Session Rename Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A pencil beside the session title in the desktop app's session headers renames the session in place, with the header and session rail updating together.

**Architecture:** Local sessions rename through a new capability-gated local-control frame; the daemon posts the rename to the server's owner-checked `/hooks/agent-title` (`rename: true`), applies the title under a revision that stale title ticks cannot overwrite, and republishes its status. Remote sessions rename over the app's own authenticated HTTP client; `RemoteAgentsService` keeps an identity-scoped title overlay until a later fetch carries the server's title. Remote rows read the `title` the server already returns from `GET /api/agent-instances`.

**Tech Stack:** .NET 10, NativeAOT (CLI/daemon), Avalonia + ReactiveUI + DynamicData (app), TUnit, WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-10-29-desktop-session-rename-design.md`

## Global Constraints

- Local-control IPC is append-only: new `FrameType` values `SessionRename = 25`, `SessionRenameAck = 82`; capability `"rename/1"` is added to `LocalControlCapabilities.Current` only beside its live routing case.
- New DTO members are trailing and nullable; status JSON always emits every member (no `DefaultIgnoreCondition`).
- JSON goes through source-generated contexts, snake_case on the wire. No reflection JSON (AOT).
- Titles are trimmed and capped at 120 characters.
- No server change: the server already returns `title` on `/api/agent-instances` and announces `AgentInstancesChanged` when a session's shown title changes.
- `applied == false` from the server is a failure, never a success.
- Comments follow `CLAUDE.md`: scarce, no history, no spec coordinates.
- After the last task: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.
- Use `~/.dotnet/dotnet` (the PATH `dotnet` is 8.0). Run one suite at a time.

## Review Focus

1. Rename while a title tick is mid-flight (server read or push paused) — the header never shows the old title again. Pinned in Task 3.
2. Rename to text equal to the prompt's first line, or to a title the daemon previously pushed — it sticks. Pinned in Task 3.
3. Sign out and back in as another user who sees an instance with the same session id — the old overlay title never shows. Pinned in Task 5.
4. A rename response that arrives after the identity changed — discarded, not recorded. Pinned in Task 5.
5. Enter pressed with whitespace-only or unchanged text — treated as cancel, no request sent. Pinned in Task 6.

---

### Task 1: Rename wire contract and client op

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/RenameIpc.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, `FrameCodec.cs`, `LocalFrame.cs`, `LocalControlOps.cs`, `StatusIpc.cs`; `src/Capacitor.App/Services/Onboarding/WizardLateBinding.cs` (forward the new op like `PutDaemonSettingsAsync`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/RenameWireContractsTests.cs`, `FrameCodecRenameTests.cs`, `LocalControlOpsTests.cs`, `StatusIpcJsonTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SessionRenameDto(string AgentId, string Title, string? ExpectedUserSubject);`
  - `public sealed record SessionRenameAckDto(bool Ok, string? Reason, string? Title);` — `Title` is the server's shown title when known.
  - `public static class SessionRenameReasons { Malformed="malformed", UnknownAgent="unknown_agent", PrivateAgent="private_agent", NoSession="no_session", IdentityMismatch="identity_mismatch", NotOwned="not_owned", NotFound="not_found", Refused="refused", Unauthenticated="unauthenticated", ServerError="server_error", Transport="transport" }` (`Transport` is client-side only).
  - `public static class RenameWire { public const string Capability = "rename/1"; public const int MaxTitleLength = 120; public static string? NormalizeTitle(string? title); }` — trims, returns null for empty/whitespace, truncates to 120.
  - `RenameIpcJsonContext` (snake_case) for both DTOs.
  - `LocalFrame.RenameJson(FrameType type, string json)`.
  - `ILocalControlOps.RenameSessionAsync(SessionRenameDto rename, CancellationToken ct) -> Task<SessionRenameAckDto>`; transport failure returns `Ok=false, Reason=Transport`.
  - `AgentStatusDto` trailing `bool? IsPrivate = null`; `DaemonInfoDto` trailing `string? UserSubject = null`.

- [ ] **Step 1: Write failing tests**
  - `Rename_dto_round_trips_snake_case`: `{"agent_id":"a","title":"T","expected_user_subject":"u"}` serializes and deserializes exactly.
  - `Rename_ack_emits_every_member`: `new SessionRenameAckDto(false, "refused", null)` serializes to `{"ok":false,"reason":"refused","title":null}`.
  - `NormalizeTitle_trims_caps_and_blanks`: `"  x  "`→`"x"`, `"   "`→null, 130 `'a'`→120 chars.
  - `Codec_round_trips_SessionRename_and_ack` following `FrameCodecSettingsTests`.
  - `RenameSessionAsync_returns_the_daemon_ack` and `RenameSessionAsync_maps_a_dropped_socket_to_transport` following the `PutDaemonSettingsAsync` tests in `LocalControlOpsTests`.
  - In `StatusIpcJsonTests`: `is_private` and `user_subject` are emitted as `null` by default and round-trip when set.
- [ ] **Step 2: Run** `~/.dotnet/dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/*Rename*/*"` — expect compile failure.
- [ ] **Step 3: Implement** the members above, mirroring `SettingsIpc.cs` / `PutDaemonSettingsAsync`.
- [ ] **Step 4: Run** the Core suite — expect PASS.
- [ ] **Step 5: Commit** `Add the session rename local-control frame`

### Task 2: Daemon server port posts renames

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/TitleResolveLoop.cs` (interface), `src/Capacitor.Cli.Daemon/Services/TitleServerPort.cs`
- Create: `src/Capacitor.Cli.Daemon/Services/TitleRenameOutcome.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TitleServerPortRenameTests.cs`; update fakes of `ITitleServerPort` in `TitleResolveLoopTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed record TitleRenameOutcome(bool Applied, string? Title, string? Reason);` — `Reason` uses `SessionRenameReasons`.
  - `ITitleServerPort.RenameAsync(string sessionId, string title, CancellationToken ct) -> Task<TitleRenameOutcome>`.
- Behaviour of `TitleServerPort.RenameAsync`: canonical id via `WorkContextIds.CanonicalSessionId` (null → `NotFound`); `http.ForHookAsync`; auth `Expired|NotAuthenticated|WrongServer` → `Unauthenticated`; `POST {base}/hooks/agent-title` body `{"session_id":canonical,"title":title,"rename":true}`; 200 with `{"applied":bool,"title":string?}` → `Applied`/`Title`, `applied:false` → `Reason=Refused`; 403 → `NotOwned`; 404 → `NotFound`; 400 or other → `ServerError`; exception → `ServerError`.

- [ ] **Step 1: Write failing tests** (WireMock, one per mapping): `Posts_rename_true_with_canonical_session_id` (asserts the request body), `Applied_reply_returns_shown_title`, `Applied_false_is_refused`, `Forbidden_is_not_owned`, `Not_found_is_not_found`, `Server_error_is_server_error`.
- [ ] **Step 2: Run** `~/.dotnet/dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/TitleServerPortRenameTests/*"` — expect failure.
- [ ] **Step 3: Implement** `RenameAsync`; parse the reply with a source-gen context (add `AgentTitleReplyDto(bool Applied, string? Title)` to the daemon's JSON context or `RenameIpcJsonContext`).
- [ ] **Step 4: Run** — expect PASS.
- [ ] **Step 5: Commit** `Post desktop renames through the agent-title hook`

### Task 3: Title loop honours an applied rename

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/TitleResolveLoop.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/TitleResolveLoopTests.cs`

**Interfaces:**
- Produces: `TitleResolveLoop.ApplyRename(string agentId, string title)` — thread-safe; callable while `TickAsync` runs.
- Rules:
  - Per-agent state gains `int Revision` and `string? Renamed`. `ApplyRename` (under a lock shared with the apply/push critical sections) sets `Renamed = title`, `ServerTitle = title`, `Applied = title`, increments `Revision`, and calls the `apply` delegate.
  - `ResolveOneAsync` captures `Revision` at start; before calling `apply` and before pushing it re-checks under the lock and skips both if the revision moved.
  - `ReadServerAsync`: a server title equal to `Renamed` is real (never discarded as a prompt echo or a push attempt). A different non-echo server title clears `Renamed` and becomes authority.
  - While `Renamed` is set, the loop never pushes a local (native/generated) title.

- [ ] **Step 1: Write failing tests**
  - `Rename_during_a_paused_server_read_is_not_reverted`: fake port whose `GetTitleAsync` blocks on a `TaskCompletionSource` returning `"Old"`; start `TickAsync`, call `ApplyRename(id, "New")`, release; assert applied titles end with `"New"` and no `"Old"` apply after `"New"`.
  - `Rename_during_a_paused_push_is_not_reverted`: same with `PushTitleAsync` paused; assert no apply of the pushed title after `"New"`.
  - `Rename_matching_the_prompt_first_line_sticks`: prompt `"Fix the bug"`, rename to `"Fix the bug"`, server then returns `"Fix the bug"`; after two ticks the applied title is still `"Fix the bug"` and generation never runs.
  - `Rename_matching_an_earlier_push_sticks`: loop pushed `"Native A"`, rename to `"Native A"`; server returns it; after a tick with native `"Native B"` the applied title stays `"Native A"` and nothing is pushed.
  - `A_newer_server_title_replaces_a_rename`: after rename `"New"`, server returns `"Newer"`; applied becomes `"Newer"`.
- [ ] **Step 2: Run** `... --treenode-filter "/*/*/TitleResolveLoopTests/*"` — expect the new tests to fail.
- [ ] **Step 3: Implement** the rules above.
- [ ] **Step 4: Run** the class — all pass, existing ones included.
- [ ] **Step 5: Commit** `Keep an applied rename from being reverted by a title tick`

### Task 4: Daemon rename handler, capability and status fields

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (handler + `IsPrivate` in `SnapshotAgentsForStatus`), `AgentOrchestrator.cs` (hold the `TitleResolveLoop` in a field so the handler can call `ApplyRename`; expose the daemon's `sub`), `LocalControlServer.cs` (route), `LocalControlCapabilities.cs` (`"rename/1"`), `DaemonStatusIpc.cs` (`UserSubject`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalRenameIpcTests.cs`, `LocalControlCapabilitiesTests.cs`, existing status snapshot tests

**Interfaces:**
- Consumes: Task 1 DTOs, Task 2 `RenameAsync`, Task 3 `ApplyRename`.
- Produces: `AgentOrchestrator.HandleLocalRenameAsync(string payload, Stream stream, CancellationToken ct) -> Task`, writing exactly one `SessionRenameAck` frame.
- Handler order: decode (bad JSON or `RenameWire.NormalizeTitle` null → `Malformed`); unknown agent → `UnknownAgent`; `IsPrivate` → `PrivateAgent`; `ExpectedUserSubject` null or ≠ daemon `sub` → `IdentityMismatch`; no session id (same resolution as `SnapshotAgentsForTitles`) → `NoSession`; `RenameAsync`; on `Applied` call `ApplyRename(agentId, outcome.Title ?? title)` then ack `Ok=true, Title=shown`; otherwise ack the outcome's reason and leave the title untouched.
- Daemon `sub`: `JwtClaims.TryGetString(accessToken, "sub")` from the daemon's current token, cached in a field that the status snapshot reads synchronously and that is refreshed whenever the daemon reads its token; null when signed out.

- [ ] **Step 1: Write failing tests**: one per refusal reason above; `Applied_rename_updates_the_status_title` (the next status snapshot carries the new `Title`); `Refused_rename_leaves_the_title`; `Capabilities_include_rename_1`; status snapshot emits `is_private` for a private agent and `user_subject` from the cached claim.
- [ ] **Step 2: Run** the daemon suite filtered to these classes — expect failure.
- [ ] **Step 3: Implement**; extend the routing `default:` error text with `SessionRename`.
- [ ] **Step 4: Run** the full daemon suite — PASS.
- [ ] **Step 5: Commit** `Rename a hosted agent's session over the local socket`

### Task 5: Remote titles, owner, overlay and remote rename

**Files:**
- Modify: `src/Capacitor.Remote.Models/AgentInstanceDto.cs` (`[JsonPropertyName("title")] public string? Title`), `src/Capacitor.App/Services/AgentRow.cs`, `src/Capacitor.App/Services/RemoteAgentsService.cs`, `src/Capacitor.App/App.axaml.cs` (wire the rename HTTP call)
- Test: `test/Capacitor.App.Tests.Unit/AgentRowTests.cs`, `RemoteAgentsServiceTests.cs`, `RemoteAgentsServiceHttpFetchTests.cs`, `test/Capacitor.Remote.Models.Tests.Unit/` (dto json)

**Interfaces:**
- Produces:
  - `AgentRow` trailing `string? OwnerUserId = null`; `FromRemote` sets it and sets `Title = dto.Title is { Length: > 0 } ? dto.Title : TitleFromPrompt(dto.Prompt)`.
  - `public sealed record SessionRenameResult(bool Applied, string? Title, string? Reason);` in `src/Capacitor.App/Services/SessionRenameResult.cs` (reasons reuse `SessionRenameReasons`).
  - `IRemoteAgentsService.RenameAsync(string agentId, string sessionId, string title, CancellationToken ct) -> Task<SessionRenameResult>`.
  - `RemoteAgentsService` constructor gains `Func<string, string, CancellationToken, Task<SessionRenameResult>>? postRename = null` (inserted before the last parameter); `RemoteAgentsService.HttpRename(ICapacitorHttpClient?, ProfileContext?)` builds it (`POST {server}/hooks/agent-title`, same status mapping as Task 2).
- Overlay rules: entries keyed by session id, each holding `(Title, Generation, FetchSeq)`. `RenameAsync` captures `_generation` and a monotonically increasing fetch sequence before the request; on `Applied` it records the entry only if `_generation` is unchanged, then republishes the cached instance with the overlay title. Every published row's title is overlay-first. A fetch whose start sequence is newer than the entry's clears the entry when that row's `Title` is non-null; an older fetch never clears it. The subject-change branch clears the overlay together with `_agents`.

- [ ] **Step 1: Write failing tests**
  - `FromRemote_prefers_the_server_title` / `FromRemote_falls_back_to_the_prompt_cut` / `FromRemote_carries_the_owner`.
  - `Applied_rename_shows_at_once`, `A_fetch_started_before_the_rename_keeps_the_overlay`, `A_later_fetch_with_a_title_wins`, `A_later_fetch_without_a_title_keeps_the_overlay` (older server).
  - `Identity_change_clears_the_overlay` and `A_rename_completing_after_an_identity_change_is_discarded` (Review Focus 3, 4).
  - `HttpRename_posts_rename_true` and the status mapping, in `RemoteAgentsServiceHttpFetchTests`.
- [ ] **Step 2: Run** `~/.dotnet/dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/*Remote*/*"` — expect failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS, plus `AgentRowTests` and `AgentDirectoryTests`.
- [ ] **Step 5: Commit** `Show the server's title on remote rows and rename them`

### Task 6: Title editor in both headers

**Files:**
- Create: `src/Capacitor.App/ViewModels/SessionTitleEditorViewModel.cs`, `src/Capacitor.App/Views/SessionTitleEditor.axaml` (+ `.axaml.cs`)
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs`, `src/Capacitor.App/Views/WorkspaceView.axaml`, `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs`, `src/Capacitor.App/Views/RemoteSessionView.axaml`, `src/Capacitor.App/App.axaml.cs` (pass dependencies), `docs/CHANGES.md` (a short entry on the rename semantics and accepted limitations)
- Test: `test/Capacitor.App.Tests.Unit/SessionTitleEditorViewModelTests.cs`, `WorkspaceViewModelTests.cs`, `RemoteSessionViewModelTests.cs`, `WorkspaceViewSmokeTests.cs`, `RemoteSessionViewSmokeTests.cs`

**Interfaces:**
- Consumes: Task 1 `RenameSessionAsync`, `RenameWire`; Task 5 `RenameAsync`, `AgentRow.OwnerUserId`.
- Produces `SessionTitleEditorViewModel(IObservable<string> title, IObservable<bool> canRename, Func<string, CancellationToken, Task<SessionRenameResult>> rename, IAppNotifier notifier)` with `Title`, `Draft`, `IsEditing`, `IsSaving`, `CanRename`, and commands `BeginEditCommand`, `SaveCommand`, `CancelCommand`.
- Behaviour: `BeginEdit` (only when `CanRename`) sets `Draft = Title`, `IsEditing = true`. `Save`: `RenameWire.NormalizeTitle(Draft)` null or equal to `Title` → cancel, no call; otherwise `IsSaving = true`, call `rename`; `Applied` → `IsEditing = false`; failure → `notifier.Notify(copy)` and stay editing with `Draft` intact. `Cancel` restores and exits. Save/Cancel disabled while saving. `CanRename` turning false while editing cancels.
- Failure copy (exact): `NotOwned` → "You can't rename a session you don't own"; `Refused` → "The server kept a newer title"; `Transport`/`Unauthenticated`/`ServerError` → "Couldn't rename the session. Check your connection and try again"; anything else → "Couldn't rename the session".
- View: read-only `TextBlock` + pencil button (`IsVisible` = `CanRename && !IsEditing`); edit mode `TextBox` (`MaxLength=120`, focused and text selected on entry) + ✓ and ✗ buttons. Enter → `SaveCommand`, Escape → `CancelCommand`. Accessible names: "Rename session", "Save name", "Cancel rename".
- Local eligibility (`WorkspaceViewModel`): attach `Connected` with `Capabilities` containing `RenameWire.Capability`, `LocalDaemonOnAppServer` true, snapshot `Daemon.UserSubject` non-null and equal to the app's subject, dto `IsPrivate == false`, dto `SessionId` non-null. Rename calls `RenameSessionAsync(new(agentId, title, appSubject))` and maps the ack to `SessionRenameResult`.
- Remote eligibility (`RemoteSessionViewModel`): row `OwnerUserId` equals the lane's current `Subject`, `SessionId` non-null, not read-only, session not ended. Rename calls `IRemoteAgentsService.RenameAsync`.

- [ ] **Step 1: Write failing tests**
  - Editor VM: `Enter_with_blank_text_cancels_without_a_call`, `Enter_with_unchanged_text_cancels_without_a_call` (Review Focus 5), `Applied_save_leaves_edit_mode`, `Failed_save_notifies_and_keeps_the_draft`, `Escape_restores_the_title`, `Buttons_are_disabled_while_saving`, `Losing_eligibility_cancels_the_edit`.
  - `WorkspaceViewModelTests`: pencil hidden for each of — older daemon (no capability), different user subject, daemon on another server, private agent, `IsPrivate` null, no session id.
  - `RemoteSessionViewModelTests`: pencil shown for an owned row, hidden for another owner and for an ended session.
  - Smoke tests: the pencil button exists in both headers and toggles the editor.
- [ ] **Step 2: Run** the app suite filtered to these classes — expect failure.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the whole app suite, then the AOT warning check from Global Constraints.
- [ ] **Step 5: Commit** `Rename a session from the desktop header`
