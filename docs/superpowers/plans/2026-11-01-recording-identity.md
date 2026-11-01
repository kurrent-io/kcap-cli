# Record each session's recording identity (slice 3 of resume/hand-off) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every recorded session carries the machine it was recorded on, the daemon that hosted it (if any), the vendor store it ran under (a physical config directory, or unknown) and the registered account that store belongs to, from the SessionStart hook through the server's ingestion to the session listing, and the desktop app has both the session's identity and its own machine id. Slice 4 (in-place Resume) reads them; this slice adds no button.

**Architecture:** The CLI stamps a `recording` object onto every SessionStart body beside the harness inventory. The store comes from vendor-supplied evidence: Claude's transcript path and Codex's rollout path name their store structurally (`<store>/projects/<p>/<id>.jsonl`, `<store>/sessions/Y/M/D/rollout-*.jsonl`), so no environment is consulted for them, and a path of any other shape is unknown. The ACP vendors (Cursor, Copilot, Kiro, Gemini, OpenCode, Grok) have no accounts; their store is the root their own environment variable resolves to, accepted only when the hook's transcript path, if it sent one, lies inside it. The server packs the object into `CapacitorSessionBlock`, projects four nullable `sessions` columns, and lists them owner-only as `recording`. The app maps it onto `AgentRow` and exposes its own machine id through `LocalMachine`.

**Tech Stack:** .NET 10 NativeAOT, Avalonia, TUnit; kcap-server ASP.NET Core, Eventuous/KurrentDB, Postgres read models.

**Spec:** `docs/superpowers/specs/2026-10-29-resume-ended-sessions-design.md` (What the input bar offers; Resume, "Offered only when"; Constraints: Server data the listing must add, Who produces the recording identity).

Two PRs, reference line `Part of #2132 — AI-4757` (kcap-server: `Part of kurrent-io/kcap-cli#2132 — AI-4757`). Each works against the other's older version: an older server ignores `recording` on the hook, and an older listing leaves the app's `Recording` null, which means "not this machine".

## Decisions this plan makes

- **The machine id is `MachineId` (`machine.json`)**, the one the daemon sends on connect and the app already reads; not `MachineIdProvider` (memory tagging) and not the accounts host id.
- **One wire shape for every vendor:** `recording: { machine_id, daemon_name, account_id, config_dir }`, all optional strings. `config_dir` null is unknown; `account_id` is the kcap registry id (`VendorAccount.Id`, what launches call `AccountId`), not the vendor's own account id.
- **Evidence only for Claude and Codex.** The structural parse needs no environment, so a scrubbed `CLAUDE_CONFIG_DIR` cannot mislead it. The default directory is never assumed: a missing or unrecognised path is unknown.
- **Mistral Vibe, Pi, Muse and Antigravity record no store** (machine and daemon only): none is resumable by session id, and Vibe is not an ACP vendor here.
- **Daemon launches report through the hook they host.** A hosted agent's SessionStart hook runs in the environment the daemon resolved (its account's directory, its `KCAP_DAEMON_NAME` marker), and the transcript evidence names the same store the daemon chose; the daemon's own resolution already reaches the server on `AgentRegistered`. No second, server-side merge that could disagree with the evidence.
- **The listing withholds `recording` from everyone but the owner**, like the agents surface's account fields: which of the owner's directories a session spent is the owner's to see, and only the owner can resume.
- **A later SessionStart of the same session that carries `recording` replaces all four columns together**; one that carries none (an older CLI) keeps them. Mixing fields from two starts could pair one store with another store's account.
- **No projection version bump.** The columns fill from new start events only: an old session's start carries nothing to replay, so a rebuild would backfill nothing, and the session stays unknown.

## Global Constraints

- A hook never throws or fails because of the stamp: every probe is best-effort and omits what it cannot read.
- New wire fields are trailing and optional on every hop; absent means unknown.
- `config_dir` is a physical path (`AccountDirectory.Normalize`), so a retargeted symlink is detectable later.
- Comments follow CLAUDE.md's Comments rules. No README or `help-*.txt` change (no CLI surface changes).

## Review Focus

1. A Claude transcript under a non-default config directory while the hook sees `CLAUDE_CONFIG_DIR` scrubbed: the store is the transcript's real store. Pinned in Task 1.
2. A transcript path of an unexpected shape, or none: `config_dir` null, never the default. Pinned in Task 1.
3. A listing read by a full-access viewer who is not the owner: `recording` null. Pinned in Task 4.
4. A resumed session whose second start has no `recording`: the first start's identity stays. Pinned in Task 3.

---

### Task 1: Recording identity in Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Recording/RecordingIdentity.cs`, `src/Capacitor.Cli.Core/Recording/RecordingStore.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Recording/RecordingStoreTests.cs`

**Interfaces:**
- `public sealed record RecordingIdentity(string? MachineId, string? DaemonName, string? AccountId, string? ConfigDir)` (snake_case JSON through `CapacitorJsonContext`).
- `public static class RecordingStore { public static string? Of(HarnessId vendor, string? transcriptPath, HarnessRegistry harnesses); public static string? AccountFor(HarnessId vendor, string? store, AccountRegistry? registry); }`

- [ ] **Step 1: Failing tests** — `Claude_store_is_read_off_the_transcript_path` (non-default dir, env registry pointing elsewhere), `Codex_store_is_read_off_the_rollout_path`, `An_unrecognised_claude_path_is_unknown`, `No_transcript_is_unknown_for_claude`, `A_symlinked_store_is_recorded_physically`, `Acp_vendor_store_is_its_root`, `Acp_vendor_transcript_outside_its_root_is_unknown`, `Vendors_without_resume_record_no_store`, `Account_is_the_registered_entry_for_the_store` (and null when unregistered).
- [ ] **Step 2: Run** `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/... -- --treenode-filter "/*/*/RecordingStoreTests/*"`; expected: compile failure.
- [ ] **Step 3: Implement**; run; expected: PASS.

### Task 2: Stamp it on every SessionStart

**Files:**
- Modify: `src/Capacitor.Cli/SessionStartInventory.cs` (new `StampRecording(JsonObject body, HarnessId vendor, ConfigRoot, HarnessRegistry, AccountStore?)`, reading `transcript_path` from the body and `KCAP_DAEMON_NAME` from the environment)
- Modify: each `*HookCommand` that calls `SessionStartInventory.Stamp`, plus `GrokHookCommand.StartRecording`, which does not
- Test: `test/Capacitor.Cli.Tests.Unit/SessionStartRecordingStampTests.cs`

- [ ] **Step 1: Failing tests** — body gets `recording.machine_id` equal to `MachineId.Get()`; `daemon_name` from `KCAP_DAEMON_NAME` (bare `[NotInParallel]`, `EnvScope.Exclusive`), absent without it; `config_dir` and `account_id` for a Claude body; a throwing probe leaves the body otherwise intact.
- [ ] **Step 2: Implement and wire**; run the CLI unit suite.
- [ ] **Step 3: Commit** — `Stamp each session's recording identity on SessionStart`

### Task 3: kcap-server ingestion

**Files:**
- Create: `src/Capacitor.Api.Public.Abstractions/Hooks/RecordingIdentityPayload.cs`
- Create: `src/Capacitor.Server.Core/SessionRecordingIdentity.cs` (the block's nested record; `SessionRecording` is taken by a UI test type)
- Modify: `SessionStartHook` and each vendor's session-start payload (a `recording` property); `CapacitorSessionBlock` (`Recording`); `SessionStartedAccessors`; `SessionWriter.RecordSessionStartedAsync`/`BuildSessionStarted`; `SessionHookHandlers.HandleSessionStart`; each vendor handler that packs a `CapacitorSessionBlock`
- Modify: `CapacitorSchema.cs` (`recording_machine_id`, `recording_daemon_name`, `recording_account_id`, `recording_config_dir` via `TryAddColumn`), `CapacitorSchema.expected.sql`, `SessionProjector.HandleSessionStarted` (both upsert variants), no `ProjectionGroups.Core` bump
- Test: projector tests beside the existing `HandleSessionStarted` ones; hook handler tests for Claude and one ACP vendor

- [ ] **Step 1: Failing tests** — a SessionStarted with the block projects all four columns; a later start without it keeps them; a later start with it replaces all four; the Claude handler and the Gemini handler carry the payload into the event.
- [ ] **Step 2: Implement**; run the affected classes.
- [ ] **Step 3: Commit** — `Record where each session was recorded`

### Task 4: kcap-server listing

**Files:**
- Modify: `RepoSessionsSql.cs` (CTE `is_owner` and the four columns; outer select owner-gated), `RepoSessionRow`, `RepoSessionReads.MapRepoSessionRow`, `RepoSession` (body member `Recording`), new `RepoSessionRecording` DTO, `RepoSessionsHandler.ToDto`, OpenAPI snapshots, `docs/API_REFERENCE.md`, `docs/gotchas/VISIBILITY.md`
- Test: `SessionListingEndpointTests` / `RepoSessionsQueryTests`

- [ ] **Step 1: Failing tests** — the owner sees `recording`; a full-access grantee sees null; a session with no columns lists `recording` null.
- [ ] **Step 2: Implement**; run; regenerate snapshots.
- [ ] **Step 3: Commit** — `List each session's recording identity to its owner`

### Task 5: Desktop app

**Files:**
- Modify: `src/Capacitor.Remote.Models/RepoSessionDto.cs` (+ `RepoSessionRecordingDto`, registered in `RemoteModelsJsonContext`), `src/Capacitor.App/Services/AgentRow.cs` (`Recording`, set by `FromServer`)
- Create: `src/Capacitor.App/Services/LocalMachine.cs` (the app's own `MachineId`, read lazily and re-read until present) and `AgentRow.RecordedOn(string? machineId)`
- Modify: `RemoteSessionViewModel` (`RecordedOnThisMachine`, from a `LocalMachine` the composition root passes in)
- Test: `RepoSessionDtoJsonTests`, `AgentRowTests`, `LocalMachineTests`

- [ ] **Step 1: Failing tests** — the DTO reads `recording`; absent reads null; `FromServer` carries it; `RecordedOn` is false for a null recording, a null machine id on either side, or a different id.
- [ ] **Step 2: Implement**; run the App suite with the session's env unset.
- [ ] **Step 3: Commit** — `Carry a session's recording identity to the desktop app`

### Task 6: Verify and open PRs

- [ ] Build the solution; run the Core, CLI and App unit suites; AOT publish check prints nothing.
- [ ] kcap-server: build, run the touched test projects.
- [ ] Push both branches; open the PRs per the template; address review threads. Do not merge.
