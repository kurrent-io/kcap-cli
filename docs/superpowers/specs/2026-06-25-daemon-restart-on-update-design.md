# Idle-gated daemon restart after CLI update

## Problem

`kcap update` (driven by the npm launcher `kcap.js`) runs
`npm install -g @kurrent/kcap@latest`, which overwrites the on-disk `kcap` and
`kcap-daemon` binaries, then runs `refresh.js`. **Nothing restarts a running
daemon.** A long-lived daemon — whether spawned with `kcap daemon start -d` or
supervised via `kcap daemon service install` — keeps executing the *old* code
until someone manually restarts it. Users silently run a stale daemon against a
freshly-updated CLI/server until they notice and restart by hand.

The fix must respect one hard constraint: **never interrupt running work.** A
restart must not land while a hosted agent is running or an eval is in progress.
Instead it is queued and applied the moment the daemon goes idle.

## Goals

- A running daemon picks up an on-disk binary update automatically, restarting
  itself **only when idle**.
- Works for both deployment modes the project supports: **service-managed**
  (launchd/systemd/Scheduled Task) and **detached** (`kcap daemon start -d`).
- A queued restart is **observable** (`kcap daemon status`) and **manually
  controllable** (`kcap daemon restart`).
- Zero coupling required from the update flow: detection is daemon-side, so brew
  and manual installs are covered too. The update flow only *informs* the user.

## Non-goals

- No forced convergence. A perpetually-busy daemon waits indefinitely (and stays
  fully available); we never drain or refuse new work to force an update through.
  `kcap daemon restart --force` is the manual escape hatch.
- No restart for **foreground** daemons (`kcap daemon start`, no `--log-file`) —
  the user is attached to the terminal; we only surface a "restart pending" hint.
- No server-side / remote-triggered restart. This is local lifecycle only.

## Design overview

A new daemon-side component, `RestartCoordinator`, owns the entire lifecycle. It
is split so the **decision logic is unit-testable** and the OS-specific **action
is a thin, swappable strategy**:

```
RestartCoordinator  (decision + queue + idle-gate; unit-tested)
 ├─ trigger:   on-disk binary change (poll) OR explicit request (control socket)
 ├─ busy:      Func<bool> IsBusy = orchestrator.ActiveCount > 0
 │                               || evalRunner.HasActiveRun
 ├─ strategy:  IRestartStrategy  = SupervisedExit | DetachedRespawn   (chosen once at startup)
 └─ state:     writes <name>.restart-pending marker for observability
```

### The loop

`RestartCoordinator` runs a `PeriodicTimer` (~15s). Each tick:

1. **If no restart pending** — cheap-stat the running binary
   (`Environment.ProcessPath`: size + last-write-time) and compare against the
   baseline captured at startup. A change marks a restart pending, records the
   target version, writes the `<name>.restart-pending` marker, and logs it.
   Transient stat failures (binary momentarily missing/being swapped mid
   `npm install`) are tolerated — the tick is skipped, not treated as a change.
2. **If a restart is pending and `!IsBusy`** — invoke the selected strategy.
3. **If pending and busy** — do nothing. The marker persists; `status` shows it.

Idle latency of up to one tick (~15s) after work finishes is acceptable; no
event-driven wakeup is required.

### Startup decision tree (strategy chosen once, logged)

| Context | Detection | Restart action |
|---|---|---|
| **Supervised** (launchd/systemd/Scheduled Task) | `KCAP_DAEMON_SUPERVISED=1` env injected at `service install` *(authoritative)*, **or** an installed service unit exists for our service-id *(deterministic fallback for pre-upgrade installs)* | `lifetime.StopApplication()` — clean exit; the supervisor relaunches the new binary |
| **Detached** (`start -d`; `--log-file` present, not supervised) | else-branch | **Self-respawn**: spawn a fresh detached `kcap-daemon` (same argv) that waits for the flock, then `StopApplication()` |
| **Foreground** (interactive; no `--log-file`, not supervised) | no log-file & not supervised | Don't auto-act — set pending + log "restart pending; exit and restart to apply" |

**Detection robustness.** We avoid fragile PPID/env heuristics (a detached
daemon also reparents to init/launchd, so PPID==1 is ambiguous). The env marker
is authoritative for new installs; the "does a service unit exist for my
service-id" check is deterministic for services installed before this feature
shipped. If genuinely uncertain, default to **detached / self-respawn**: a
spurious extra exit-and-relaunch under a supervisor is self-correcting (the loser
hits the flock and exits cleanly with code 2), whereas a wrong "just exit" would
silently kill a real detached daemon — the worse failure.

### Busy definition

`IsBusy = orchestrator.ActiveCount > 0 || evalRunner.HasActiveRun`

- `ActiveCount` already exists: agents with status `Starting`/`Running` (counts
  both server-launched and local/private agents).
- Evals run **inline** (`EvalService.RunQuestionAsync`) and are *not* reflected
  in `ActiveCount`, so a restart gated only on agents could cut off an eval
  mid-run. Add a `HasActiveRun` flag to `EvalRunner`, set when an eval run is
  prepared and cleared on finalize/cancel, covering the whole eval lifecycle.

## Restart execution

### Supervised (`SupervisedExitStrategy`)

Call `lifetime.StopApplication()`. The existing cleanup path runs (disposes
`DaemonLock` → releases the flock + deletes the PID file, disposes
orchestrator/connection), the process exits 0, and the supervisor relaunches the
now-updated binary (launchd `KeepAlive`, systemd `Restart=`, Scheduled Task
`RestartOnFailure`). The freshly-started daemon clears any stale
`<name>.restart-pending` marker on startup (its running version now matches the
on-disk binary).

### Detached (`DetachedRespawnStrategy`)

The handoff is the only delicate piece:

1. Build a child `ProcessStartInfo` from `Environment.ProcessPath` (now the *new*
   on-disk binary) using the **same argv the daemon received** (name, server-url,
   max-agents, `--log-file`, …), detached exactly like
   `DaemonCommands.StartDetached`: redirect all three std streams,
   `ProcessHelpers.PreventInheritedStdHandles()`, `CreateNoWindow`. Append a new
   `--await-lock` flag.
2. Start the child; **do not** wait for it. Its `DaemonLock.TryAcquire` initially
   fails (the old daemon still holds the flock); `--await-lock` makes startup
   retry for ~5s instead of exiting with code 2.
3. The old daemon calls `lifetime.StopApplication()`; cleanup disposes
   `DaemonLock`, releasing the flock.
4. The child's retry succeeds, it rewrites the PID file, connects, and runs. The
   existing `DaemonLock.Dispose` deletes the PID file only if it still matches the
   *old* PID, so the successor's PID entry is never orphaned.

**Verified correctness details:**

- **The flock is not inherited by the child.** .NET opens `FileStream` handles
  non-inheritable (`O_CLOEXEC` on Unix; `HANDLE_FLAG_INHERIT` cleared on Windows),
  so the child gets its own fd — the lock genuinely releases when the old daemon
  exits. (If inherited, the child would pin the lock forever.)
- **`--await-lock` is a new daemon-startup flag**: a bounded retry loop (~5s, then
  exit 2) wrapped around `DaemonLock.TryAcquire`. Harmless outside the handoff.

### Foreground

No auto-action. Set pending, write the marker, log a hint that the user should
exit and restart to apply the update.

## Trigger precision

The poll compares **size + last-write-time** only — hashing a tens-of-MB AOT
binary every tick is too expensive. Consequence: a same-version reinstall could
queue one redundant restart, which is harmless because it only ever fires when
**idle**.

Optional follow-up (not in initial scope): when a change is detected, exec the
on-disk binary `--version` *once* and compare to suppress same-version churn.
This requires adding a `--version` handler to `kcap-daemon`, which has none today
(`Program.cs` forwards all args straight to `DaemonRunner.RunAsync`).

## Manual command

```
kcap daemon restart [--name N] [--when-idle] [--force]
```

- Name resolution mirrors `stop`/`status`: explicit `--name`, else enumerate
  running daemons (prompt on multiple).
- Connects to the per-name control socket and sends a new
  `FrameType.Restart = 7` frame carrying the mode; the daemon replies with
  `FrameType.RestartAck = 69` on success (or the existing `Error` frame on
  failure).
- Semantics, consistent with the existing `stop` safety pattern:
  - **bare `restart`** → if idle, restart now; **if busy, refuse** with
    "N agents running / eval in progress — use `--when-idle` to queue or
    `--force` to restart anyway."
  - **`--when-idle`** → queue and return immediately (same path self-detection
    uses).
  - **`--force`** → restart now regardless; running agents are torn down (same as
    `daemon stop`).
- Routing: `LocalControlServer` gains a `RestartCoordinator` dependency and routes
  `Restart` frames to it. (`Spawn`/`Attach`/`List` continue to route to
  `AgentOrchestrator`.)

## Observability

- `RestartCoordinator` writes `~/.config/kcap/daemons/<name>.restart-pending`
  when a restart is queued: target version, reason (`self-detected` /
  `requested`), and queued-at timestamp. The successor daemon deletes it on
  startup.
- `kcap daemon status` reads the marker (the same way it already reads PID files —
  no socket round-trip) and prints, e.g.:
  `restart pending: v0.4.11 → v0.4.12 (queued 12:03, waiting for idle)`.
- `kcap daemon doctor` already enumerates per-name files; it surfaces stale
  `.restart-pending` markers and removes them under `--clean`.

## Update-flow UX (informational only)

Self-detection does the functional work; the update flow merely informs. After
`npm install` + refresh, if any daemon is running, print one line:

> kcap daemon '<name>' is running and will restart automatically when idle to
> pick up v0.4.12 (`kcap daemon status` to check, `kcap daemon restart --force`
> to apply now).

There is no functional coupling to the daemon — purely a heads-up.

## Service install change

Inject `KCAP_DAEMON_SUPERVISED=1` into the unit environment at
`kcap daemon service install` (via the `ServiceSpec.Environment` dictionary, set
where `ServiceInstall` builds the env). Services installed before this feature
are covered by the "service unit exists for my id" runtime fallback.

## Components & files

**New (daemon):**

- `src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs` — background loop,
  decision/queue/idle-gate logic, marker writer.
- `src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs` +
  `SupervisedExitStrategy` + `DetachedRespawnStrategy`.
- Supervised-context probe (env marker + unit-exists fallback). The unit-exists
  check needs visibility from the daemon assembly; expose a minimal
  `SupervisionProbe` helper in `Capacitor.Cli.Core` (or a daemon-local equivalent)
  so the daemon does not take a dependency on the CLI's `IServiceManager`.

**Modified:**

- `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs` — add `Restart = 7`,
  `RestartAck = 69`.
- `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` — route `Restart`
  frames to `RestartCoordinator`.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — expose `IsBusy` /
  reuse `ActiveCount`.
- `src/Capacitor.Cli.Daemon/Services/EvalRunner.cs` — add `HasActiveRun`.
- `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — register `RestartCoordinator` (+
  hosted service), capture original argv for respawn, honor `--await-lock`.
- `src/Capacitor.Cli.Daemon/DaemonLock.cs` — `--await-lock` bounded retry on
  acquire.
- `src/Capacitor.Cli/Commands/DaemonCommands.cs` — new `restart` subcommand;
  `status` reads/prints the pending marker; usage text.
- `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`ServiceInstall`) — inject
  `KCAP_DAEMON_SUPERVISED=1`.
- Update flow notice: `npm/kcap/bin/kcap.js` / `refresh.js` (or the native
  post-update path) — one-line "daemon will restart when idle" hint.
- `src/Capacitor.Cli.Core/Resources/help-*.txt` — daemon help text.
- `README.md` — daemon quick-start + per-command `daemon` section (CLAUDE.md
  mandate; same PR).

## Testing

**Unit (TUnit):**

- `RestartCoordinator` gate logic with an injected `IsBusy` and a fake update
  signal: pending+idle → strategy fires; pending+busy → no fire; busy→idle
  transition fires.
- Startup strategy selection for each context (supervised via marker, supervised
  via unit-exists fallback, detached, foreground, uncertain→detached).
- Binary-change detection via an injected stat seam (size/mtime change → pending;
  transient failure → skip).
- `--force` overrides the gate; `EvalRunner.HasActiveRun` blocks a restart.
- Strategies sit behind `IRestartStrategy`, so coordinator tests never spawn real
  processes.

**Integration:**

- `--await-lock` acquires the flock after a holder releases it.
- `kcap daemon restart` round-trips over the control socket (queue + force).
- Marker file is written on queue and read/printed by `status`.

**AOT:** `dotnet publish -c Release` then grep for `IL3050`/`IL2026`. The
process-spawn and stat paths are reflection-free and should stay clean.

## Risks & mitigations

- **Mis-detected supervision kills a detached daemon** → default-to-detached on
  uncertainty makes the failure self-correcting rather than fatal.
- **Respawn race on the flock** → `--await-lock` retry on the successor + the
  existing PID-match-guarded `DaemonLock.Dispose` handle the window.
- **Perpetually-busy daemon never updates** → by design (never interrupt work);
  made visible via `status` and overridable via `restart --force`.
- **Same-version reinstall churn** → bounded to a single idle restart; optional
  `--version` confirmation can eliminate it later.
