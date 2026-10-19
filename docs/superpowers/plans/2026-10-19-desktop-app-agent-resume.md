# Desktop app: resuming and interrupted agents, reload through the restart — Implementation Plan

**Goal:** The desktop app shows an agent the daemon is resuming or could not resume, says truthfully what a reload and an update do to running agents, judges a reload by the successor daemon rather than the old one, and rides a terminal tab through the restart.

**Spec:** `docs/superpowers/specs/2026-10-17-daemon-restart-agent-resume-design.md`, §5 and the desktop bullet of §8. Issue #1618.

**Out of scope:** server-side claims (kcap-server#2495), native ACP resume (#1617), the rename flow (a rename retires the old daemon with `end`, so its agents end and the idle gate stays).

## Constraints

- Status colours: `Resuming` is in-flight (`KcapPurple*`), `Interrupted` is attention (`KcapWarning*`). Lucide icons through `AppIcon`.
- The status payload is additive: every new member is trailing and nullable, an older app reads the new rows as opaque status words.
- The mutation lane's evidence predicate is shared; the reload adds a precondition to it (a different instance than the one the reload stopped), never a weaker check.
- Tests: projections in ViewModel unit tests, no XAML smoke tests for static values. Comments per CLAUDE.md.

## Tasks

### 1. The status payload carries ledger-only agents

`AgentStatusDto` gains a trailing `Interruption` (the ledger's reason). `DaemonStatusIpc.Snapshot` appends one row per ledger entry with no live agent, from `AgentOrchestrator.LedgerOnlyAgents`: status `Resuming` or `Interrupted`, the launch's repo, worktree, branch, title, model, requester and session id. Every change to the resume candidates or an entry's interruption pulses the status notifier, so the app sees it without waiting for unrelated activity.

Test first (daemon suite): a resuming candidate and an interrupted entry appear in the snapshot with their status and reason; a live agent is never listed twice.

### 2. `SessionStatusDots` gains `Resuming` and `Interrupted`

`AgentStatusKind` gains `Interrupted` (after `NeedsYou`) and `Resuming` (after `Starting`). `AgentRow` carries `Interruption`. `Present` maps status `Resuming` to the pulsing purple ring and `Interrupted` to a warning glyph with a "Reason" fact; rollups name "resuming" and "interrupted"; `NeedsAttention` counts an interrupted agent.

Test first (app suite): presentation kind, label, tip facts, colour flags and rollup dominance.

### 3. Reload and update copy

`ReloadDisclosure`, `UpdateCoordinator.ReadyDisclosure` (non-Windows) and `MainWindowViewModel.RestartPendingMessage` say that agents are stopped and resumed on the new daemon, and that an unattended restart waits for a turn boundary, not for idle.

Test first: the existing copy tests updated to the new text.

### 4. The reload lane waits for the successor

`DaemonMutationLane` observes the daemon before a reload and, after the CLI returns, requires evidence from a different instance: the same instance answering until the confirm window ends is `AttentionSkew("predecessor_still_running")`, never success. No pre-reload evidence leaves the check as it is.

Test first (`DaemonMutationLaneTests`): a reload whose evidence keeps the pre-reload instance id is not `Succeeded`; one whose evidence moves to a new instance is.

### 5. The terminal tab reattaches through the restart

`TerminalTabViewModel` takes a ledger lookup and a reconnect window (`KCAP_ATTACH_RECONNECT_SECONDS`, default 90, shared with `kcap agent attach` through Core). A lost connection, or a `no such agent` refusal, while the ledger holds a non-interrupted entry publishes `Reconnecting` and retries the attach by agent id until the window ends; the daemon's replay of the persisted ring and its restart marker is the first output of the new attempt. An entry that becomes interrupted publishes `Interrupted` with its reason; an entry that disappears ends the session; the window running out fails as a lost connection does today.

Test first: reconnect after a drop, interrupted with reason, no ledger entry fails as before, the window bound.

### 6. `daemon.auto_resume` in Settings

`SettingsProfileStore.SaveAutoResumeAsync`; `SettingsViewModel.AutoResume` loads the saved value (unset reads as on) and saves on change with a message that it applies at the next daemon start. A toggle in the Daemon tab beside capacity.

Test first: the property loads and saves, unset reads as on.

### 7. Docs and verification

README: the `daemon.auto_resume` row names the desktop app's Settings, and the restart paragraph mentions the app's terminal tab. App and CLI unit suites, plus the daemon suite for task 1.
