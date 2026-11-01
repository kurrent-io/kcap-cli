# Resume an ended session in place (slice 4 of resume/hand-off) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From an ended session's composer, the session's owner clicks **Resume** on the machine that recorded it: the daemon natively resumes the same vendor session (`claude --resume`, `codex resume`, ACP `session/load`) in the session's own directory, bound to the original kcap session, and the open view turns live. A Resume that cannot work is not offered; one that fails falls back to Hand off with the typed text kept.

**Architecture:** The app asks the server (`GetLaunchFeatures` → `resume-in-place/1`) and the local daemon (hello capability `resume-in-place/1`, then a local-control `ResumeProbe`) whether Resume can work, and sends `RemoteLaunchRequest.Resume { source_session_id, directory }`. The server admits it only for the session's owner launching on their own daemon on the recording machine, with a hard `Ended` stream state, no live agent and a recorded store; it takes the `Resume` kind of the shared `SourceSessionReservations` lease **bound to the daemon's connection** before the command leaves, and sends `LaunchAgentCommand.ResumeInPlace` carrying the lease token and the recorded store. The daemon renews the lease from arrival, passes the consent gate, then runs the restart-resume protocol on a synthetic ledger entry for the source (`LaunchAdmittedAsync` with a `ResumeLaunch`, then `SurvivesReadinessAsync(judgeTranscript: true)`) under a new work location, `WorkLocation.InPlace`. Success releases the lease; any failure retires the runtime, reports `LaunchFailed` with a `resume_…` reason and releases the lease. The daemon's restart resume takes the same lease through a new hub method before it re-registers.

**Tech Stack:** .NET 10 NativeAOT, Avalonia + ReactiveUI, ASP.NET Core SignalR, TUnit, WireMock.Net, real `git`.

**Spec:** `docs/superpowers/specs/2026-10-29-resume-ended-sessions-design.md` (What the input bar offers; Layout option C; Resume; Takeover's server-held reservation; Constraints; Resume acceptance cases).

Two PRs. kcap-server (Tasks 1–3), reference line `Part of kurrent-io/kcap-cli#2132 — AI-4757`; kcap-cli (Tasks 4–10), reference line `Part of #2132 — AI-4757` until the leftovers below are closed. Each works against the other's older version: an older server lists no `resume-in-place/1` and the app hides Resume; an older daemon advertises nothing and the server refuses the launch.

## Decisions this plan makes

- **One capability name on both hops, `resume-in-place/1`**: the server's `GetLaunchFeatures` entry, the local-control hello capability, and (as a bool) `DaemonConnect.SupportsResumeInPlace`. The existing local `resume/1` is the ledger-resume API and is not reused.
- **The server takes the reservation at admission, held by the daemon's connection**, not the app's. The daemon is what renews it (`RenewHandoffTakeover`, every 20 s from command arrival, consent wait included) and ends it (`EndHandoffTakeover`); a daemon disconnect or a missed renewal frees it. The existing renew/end hub methods already operate on any token by holder connection, so they are reused rather than duplicated. The server also releases it when the launch's `LaunchAgent` send throws and when `LaunchFailed` arrives for that agent id, so a daemon that fails without ending the lease frees it at once.
- **The recorded store comes from the server, not the app.** The server reads `recording_account_id` / `recording_config_dir` / `recording_machine_id` and puts the store in the command; the app's request carries only the source id and directory. A null `config_dir` is refused (`resume_not_resumable`), as is a recording machine other than the daemon's reported `MachineId`.
- **A source with `SessionContinuedAs` recorded (already handed off) may still be resumed.** Its work items moved to the successor; resuming the original chat loses nothing and the spec does not forbid it.
- **The native id is derived from the listed session id by one Core helper, `NativeResumeId.For(vendor, sessionId)`**: Claude and Codex need a GUID and get its dashed form; the ACP vendors whose daemon descriptor supports restart resume (Cursor, Copilot, OpenCode) take the id as listed, a GUID in its dashed lowercase form, and a `senc1…` token yields null. Every other vendor yields null. Kiro, Gemini and Grok have no daemon `session/load` resume yet, so they never offer Resume.
- **ACP Resume rides on the recording the session already has.** A daemon-launched ACP session whose vendor hook did not fire under ACP has no recorded store and so is never offered Resume — safe by construction. Having the daemon report the store for its ACP launches needs a server ingestion change and stays a loose end.
- **`WorkLocation.InPlace = 2`** (appended to the byte enum): permission, posture and preset rules treat it as `OwnedWorktree` (writable, presets allowed, Codex `workspace-write`); deletion, attachment placement and transcript-discovery rules treat it as `BorrowedCwd` (never deleted, no worktree-placed attachments, no cwd-only Claude discovery). The resume ledger lists InPlace paths in a **protected** set the startup sweep never deletes, beside the owned set it may delete.
- **A resume reuses `LaunchAdmittedAsync`'s resume branch** with a synthetic `ResumeLedgerEntry` (agent id from the server, `SessionId` the source, `VendorSession` the native id, `Launch.Work = InPlace`, `Launch.AccountId`/`AccountDirectory` the recorded store, `Origin = "server"` so the allow-list applies). Its consent is the server launch's own gate decision, not `GrantResume`.
- **The store check**: `AccountResolution.ResolveForResume` must accept and its physical directory must equal the recorded `config_dir`; for the ACP vendors (no account registry) the vendor's current env-resolved root must equal it. Anything else refuses `resume_store_changed`.
- **Codex transcripts are found by id, not by spawn time**: an agent that already holds its Codex session id locates its rollout by the id in the filename anywhere under the store's `sessions/` tree, which also fixes a restart resume of an old Codex PTY session.
- **An empty first message** resumes silently where the factory resumes idle without a prompt (`ResumesIdleWithoutPrompt`); elsewhere the prompt is left empty and the runtime sends no turn.
- **Resume's settings** are the launcher defaults the hand-off strip seeds from (`HandoffChoicesViewModel`: default model, no effort, `HomeViewModel.DefaultPermissionMode`, default Codex posture), under the recorded account.
- **No placeholder for a resume.** The view stays on the source with `Resuming…`; the daemon row for the same session id replaces the server row through the existing rebind. A failure — before or after that row appears — is matched by agent id in `HandoffDrafts` (a resume hold is never confirmed by its row appearing) and hands back the draft with the notice, marking the source so its composer hides Resume and opens the strip.
- **The daemon-restart resume takes the lease too** (`BeginSourceResume`, a new daemon hub method) before it re-registers an agent with a known session. `source_reserved` fails that native resume and skips the kcap-continue fallback, since a hand-off is taking the session over; an older server, or any other refusal, proceeds as today.
- **"Alive locally"** is the daemon's own `HostsSession`; a terminal session still running would not be `Ended` on the server.

## Global Constraints

- New wire fields are trailing, optional and name-bound; absent means unsupported. The `ResumeInPlace` command field is omitted when null (`WhenWritingNull`) so other launches keep their bytes.
- A resume is never run as an ordinary launch: the server refuses one to a daemon without `SupportsResumeInPlace`; the daemon refuses a `ResumeInPlace` block it cannot carry, and one combined with `Borrowed`, `Review`, `Handoff`, `ScratchWorkspace`, `ResumeSessionId` or a non-default kind.
- Refusal codes (server, `code: sentence`): `resume_unsupported_daemon`, `resume_not_owner`, `resume_source_unavailable`, `resume_not_resumable`, `resume_other_machine`, `resume_source_active`, `resume_source_reserved`. Daemon `LaunchFailed` reasons: `resume_invalid`, `resume_unsupported_vendor`, `resume_source_active`, `resume_directory_missing`, `resume_directory_not_allowed`, `resume_store_changed`, `resume_failed: <restart-resume reason>`.
- Local-control probe refusals (same codes as the daemon's launch checks) hide Resume; they are never shown as a disabled button.
- No worktree, branch or project link is deleted for an InPlace agent, on success, failure, stop or recovery. No `git worktree prune`.
- The prompt is delivered like any hosted launch prompt, never on the command line.
- Copy (exact): button `Resume`; progress `Resuming…`; fallback notice `Couldn't resume this session here ({reason}). Hand off starts a new session with its context.`; Hand off button `Hand off` while the strip is closed, `Start hand-off` while open, with a `Cancel` ghost button.
- Desktop UI: `kcapChip` / `kcapGhost` / `kcapPrimary` classes as the hand-off composer uses them; no status colours on Resume.
- Comments follow CLAUDE.md's Comments rules. No README or `help-*.txt` change (no CLI surface changes).

## Review Focus

1. A resume admitted and paused at consent, then a hand-off takeover of the same source: the takeover is refused `source_reserved`; denying consent releases the lease and a retried takeover is admitted. Pinned in Tasks 2 and 6.
2. A resume whose ACP transcript bind is refused or whose Codex source claim is refused after a live row appears: a `LaunchFailed` (the vendor's own startup-failure report may precede it; the app acts on the first), no runtime left, the directory untouched, the lease released, and the app's draft returns with Resume hidden. Pinned in Tasks 6 and 9.
3. A newer app with an older server, a newer server with an older daemon: no Resume shown, and a hand-crafted resume request is refused, never launched as an ordinary agent. Pinned in Tasks 2, 6 and 8.
4. A retained daemon worktree resumed in place, then cleaned and the daemon restarted: the startup sweep keeps it while the agent is active or recoverable. Pinned in Task 7.
5. A Codex session a week old: resumed with an empty and a non-empty first message, its rollout found by id. Pinned in Task 6.

---

## kcap-server (PR 1)

### Task 1: Wire fields, daemon capability and feature list

**Files:**
- Create: `src/Capacitor.Api.Public.Abstractions/Agents/ResumeLaunchRequest.cs`, `src/Capacitor.Api.Public.Abstractions/Agents/Commands/ResumeInPlaceLaunch.cs`
- Modify: `RemoteLaunchRequest.cs` (trailing `Resume`), `Commands/LaunchAgentCommand.cs` (trailing `ResumeInPlace`, `WhenWritingNull`), `Daemon/DaemonConnectArgs.cs` (trailing `SupportsResumeInPlace`), `DaemonRegistry.cs` (entry field, `SupportsResumeInPlace(connId)`), `CapacitorHub.cs` (`DaemonConnect` passes it; `GetLaunchFeatures` lists `resume-in-place/1` when the admission is registered)
- Test: `CapacitorHubLaunchFeaturesTests`, `CapacitorHubDaemonConnectHeadlessTests` (pattern)

**Interfaces:**
- `public sealed record ResumeLaunchRequest(string SourceSessionId, string Directory)`.
- `public sealed record ResumeInPlaceLaunch(string SourceSessionId, string Directory, string ReservationToken, string? AccountId, string ConfigDir)`.

- [ ] Failing tests: `GetLaunchFeatures_lists_resume_in_place`, `DaemonConnect_records_SupportsResumeInPlace` (absent → false). Implement; run; commit `Carry an in-place resume on the launch wire`.

### Task 2: Resume admission under the source reservation

**Files:**
- Create: `src/Capacitor.Server.Services/Sessions/Handoff/SourceResumeGate.cs`, `src/Capacitor.Api.Public/Sessions/ResumeLaunchAdmission.cs`, `ResumeLaunchRefusals.cs`
- Modify: `CapacitorHub.cs` (`RequestLaunchAgentCore` gets `resume`; refuse `Resume` + `Handoff` together; admission after the capability policy and before `TryReserveAsync`; release on a send failure; `LaunchFailed` releases), the module registration
- Test: `test/Capacitor.Server.Services.Tests/Sessions/SourceResumeGateTests.cs`, `test/Capacitor.Api.Public.Tests/Sessions/CapacitorHubResumeLaunchTests.cs`

**Interfaces:**
- `SourceResumeGate.BeginAsync(string sourceSessionId, string connectionId, string requesterUserId, bool requireEnded, CancellationToken ct) : Task<SourceResumeOutcome>` — inside `EnterAsync`: access `Full`, session owner is the requester (`resume_not_owner` otherwise), `TryAcquire(…, Resume)` (null → `source_reserved`), then when `requireEnded`: `ReadEndStateAsync` ended and no live agent (`source_active`); any refusal after the acquire releases it. Returns the token and the summary's recording.
- `ResumeLaunchAdmission.AdmitAsync(ResumeLaunchRequest, requesterUserId, requesterIsOwner, daemonSupports, daemonMachineId, connId, ct) : Task<(ResumeInPlaceLaunch? Launch, string? Refusal)>`; `Track(agentId, token, connId)` / `Release(agentId)`.

- [ ] Failing gate tests: `An_ended_source_is_reserved`, `An_active_source_is_refused`, `A_live_agent_on_the_source_is_refused`, `A_takeover_lease_refuses_a_resume`, `A_resume_lease_refuses_a_takeover` (`HandoffTakeoverGate.BeginAsync` → `source_reserved`), `A_non_owner_is_refused`, `A_refusal_after_acquire_releases_the_lease`, `A_daemon_disconnect_frees_the_lease`.
- [ ] Failing hub tests: `A_resume_to_a_daemon_without_the_capability_is_refused_and_nothing_is_sent`, `A_resume_without_a_recorded_store_is_refused`, `A_resume_on_another_machine_is_refused`, `An_admitted_resume_carries_the_token_and_the_recorded_store`, `A_resume_and_a_handoff_together_are_refused`, `LaunchFailed_releases_the_resume_lease`, `A_failed_send_releases_the_resume_lease`.
- [ ] Implement; run `Capacitor.Server.Services.Tests` and `Capacitor.Api.Public.Tests` classes touched; commit `Admit an in-place resume only under the source's lease`.

### Task 3: A restart resume's lease

**Files:**
- Modify: `CapacitorHub.cs` (`BeginSourceResume(SourceResumeBeginRequest)` daemon-only, `requireEnded: false`)
- Create: `src/Capacitor.Api.Public.Abstractions/Agents/Daemon/SourceResumeBeginRequest.cs`
- Test: `CapacitorHubResumeLaunchTests`

- [ ] Failing tests: `BeginSourceResume_reserves_an_active_session`, `BeginSourceResume_is_refused_while_a_takeover_holds_it`, `BeginSourceResume_from_a_non_daemon_is_refused`. Implement; run; commit `Let a daemon hold a session's lease while it resumes it after a restart`.
- [ ] Open the server PR per kcap-server's template. Ask before merging.

## kcap-cli (PR 2)

### Task 4: Core wire and the native resume id

**Files:**
- Create: `src/Capacitor.Cli.Core/Resume/ResumeInPlaceLaunch.cs`, `src/Capacitor.Cli.Core/Resume/NativeResumeId.cs`, `src/Capacitor.Cli.Core/LocalIpc/ResumeInPlaceIpc.cs` (`ResumeInPlaceWire.Capability`, probe DTOs, JSON context)
- Modify: `Models.cs` (`LaunchAgentCommand.ResumeInPlace`, `DaemonConnect.SupportsResumeInPlace`), `LocalFrame.cs` (`WorkLocation.InPlace = 2`; factories), `FrameType.cs` (`ResumeProbe = 36`, `ResumeProbeReply = 91`), `FrameCodec.cs`, `LocalControlOps.cs` (`ProbeResumeAsync`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Resume/NativeResumeIdTests.cs`, `…/Resume/ResumeInPlaceWireTests.cs`

**Interfaces:**
- `ResumeProbeRequestDto(string SessionId, string Vendor, string Directory, string? AccountId, string? ConfigDir)`, `ResumeProbeReplyDto(string? Refusal)`.
- `NativeResumeId.For(string vendor, string? sessionId) : string?`.

- [ ] Failing tests per the Decisions rules (Claude/Codex GUID dashed, non-GUID null, ACP opaque kept, ACP GUID dashed, `senc1…` null, Pi/Muse/Antigravity/Kiro null); wire round-trip; `ProbeResumeAsync` null on transport failure. Implement; commit `Carry an in-place resume to the daemon`.

### Task 5: Eligibility check and local-control probe

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.ResumeInPlace.cs`
- Modify: `LocalControlServer.cs` (route), `LocalControlCapabilities.cs` (`resume-in-place/1` beside it), `ServerConnection.BuildDaemonConnect` (`SupportsResumeInPlace: true`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorResumeInPlaceProbeTests.cs`, `LocalControlCapabilitiesTests`, `ServerConnectionAdvertisementTests`

**Interfaces:** `internal string? InPlaceResumeRefusal(string vendor, string sessionId, string directory, string? accountId, string? configDir)` — the one check both the probe and the launch run.

- [ ] Failing tests: `A_claude_session_in_its_store_is_resumable`, `A_missing_directory_is_refused`, `A_directory_outside_the_allowed_repositories_is_refused`, `A_removed_account_is_refused`, `A_retargeted_store_is_refused` (symlink moved), `An_unknown_store_is_refused`, `An_acp_session_recorded_with_no_account_is_resumable`, `A_senc_id_is_refused`, `A_session_hosted_here_is_refused`; capability list pinned with `resume-in-place/1`; connect advertises it. Implement; commit `Tell the app whether an ended session can be resumed here`.

### Task 6: The in-place resume launch

**Files:**
- Modify: `AgentOrchestrator.ResumeInPlace.cs`, `AgentOrchestrator.cs` (branch in `HandleAdmittedLaunchAsync`; InPlace in the work-location switches), `AgentOrchestrator.Resume.cs` (`ResumedWorktree` for InPlace), `WorktreeInfo` (`InPlace(path)`), `CodexSessionRolloutLocator.cs` (`TryLocateSession` by id), the launchers/factories' work-location checks, `ServerConnection.cs` (lease renew/end reuse)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorResumeInPlaceLaunchTests.cs` (harness: the restart-resume tests')

- [ ] Failing tests: `A_claude_resume_runs_in_the_directory_on_the_original_session` (runtime ctx `ResumeSessionId` dashed, cwd = directory, agent `SessionId` = source, `Work == InPlace`), `A_week_old_codex_resume_finds_its_rollout_by_id` (empty and non-empty prompt), `An_empty_prompt_resumes_idle`, `The_lease_is_renewed_until_readiness_and_ended_after`, `A_denied_consent_reports_failure_and_ends_the_lease`, `A_refused_acp_bind_retires_the_runtime_and_reports_once`, `A_refused_source_claim_retires_the_runtime_and_reports_once`, `A_runtime_that_exits_early_reports_resume_failed`, `Nothing_in_the_directory_is_deleted_on_failure`, `A_resume_block_with_a_borrow_or_handoff_is_refused`, `A_codex_in_place_resume_is_workspace_write`.
- [ ] Implement; run the daemon suite's Resume and Handoff classes; commit `Resume an ended session in its own directory`.

### Task 7: InPlace survives a restart and the sweep

**Files:** `AgentOrchestrator.Resume.cs` (protected paths), `DaemonRunner.cs`, `WorktreeManager.CleanupOrphanedAsync` (protected set never deleted)

- [ ] Failing tests: `An_in_place_entry_resumes_in_place_after_a_restart`, `The_sweep_keeps_a_retained_worktree_an_in_place_agent_uses` (clean tree, ledgered InPlace), `An_in_place_agent_stop_deletes_nothing`. Implement; commit `Keep a directory an in-place agent uses through a restart`.

### Task 8: The restart resume takes the lease

**Files:** `ServerConnection.cs` (`BeginSourceResumeAsync`), `AgentOrchestrator.Resume.cs` (`NativeResumeAsync`, `ResumeEntryAsync`)

- [ ] Failing tests: `A_restart_resume_holds_the_lease_until_readiness`, `A_reserved_source_interrupts_without_continuing`, `An_old_server_resumes_as_before`. Implement; commit `Hold a session's lease while resuming it after a restart`.

### Task 9: App wire and the Resume composer

**Files:**
- Modify: `src/Capacitor.App/Services/ILaunchClient.cs` (`LaunchRequest.Resume` before `AccountId`; payload `resume`), `HandoffDrafts.cs` (resume holds; `ResumeFailed(source)`), `HandoffComposerViewModel.cs` (Resume eligibility and launch; strip open/closed; Hand off / Start hand-off / Cancel), `HandoffComposerView.axaml(.cs)` (Resume button, Cancel, Esc), `App.axaml.cs` (`LocalMachine` into the factory)
- Test: `HandoffComposerViewModelTests`, `HandoffDraftsTests`, `LaunchRequestTests`, `RemoteSessionViewSmokeTests`

- [ ] Failing view-model tests: `Resume_is_offered_for_my_session_recorded_here` (strip closed, button `Hand off`), and not offered for each of: another machine, no recording, null store, someone else's, stale-not-ended, old server, old daemon, daemon refusal; `Hand_off_opens_the_strip_and_hides_resume`, `Cancel_closes_the_strip`, `Resume_sends_the_source_and_directory_with_the_session_vendor`, `A_resume_failure_keeps_the_text_shows_the_notice_and_opens_the_strip`, `A_failure_after_the_view_rebound_hands_the_draft_to_the_next_composer` (`ResumeFailed` hides Resume there), `An_immediate_refusal_falls_back_too`; smoke: `Clicking_resume_launches`, `Esc_closes_the_strip`.
- [ ] Implement; run the App classes with the session env unset; commit `Resume an ended session from its composer`.

### Task 10: Verify and open PR 2

- [ ] `dotnet build Capacitor.slnx`; Core, Daemon, App suites touched; AOT check prints nothing.
- [ ] Open the PR per the template, `Part of #2132 — AI-4757`. Ask before merging.
- [ ] Leftovers as loose ends: daemon-reported store for ACP launches; the live end-to-end Resume run (terminal Claude and Codex, default and non-default stores) once both PRs deploy.
