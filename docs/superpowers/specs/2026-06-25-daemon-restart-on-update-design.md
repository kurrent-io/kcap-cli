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
  (launchd/systemd) and **detached** (`kcap daemon start -d`).
- **macOS + Linux only.** Windows is out of scope for auto-restart (see *Platform
  scope*): a running `kcap-daemon.exe` is locked, so `npm install` can't replace
  it in place and the on-disk poll never sees a change.
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

## Platform scope

Auto-restart-on-update is **macOS + Linux only**. On Windows a running executable
image is locked for the process's lifetime, so `npm install -g` cannot replace
`kcap-daemon.exe` while the daemon runs — the install would fail on the locked
file, and the on-disk poll would never observe a new binary. (The existing
`kcap.js` comment notes this exact lock semantics; it only solved it for the *CLI*
binary, which isn't running during an update — the daemon binary is.)

Windows behavior:

- **No self-detection / no auto-restart.** The `RestartCoordinator` poll is a
  no-op trigger on Windows (the file can't change under a running daemon).
- **Documented manual flow:** stop the daemon (or `kcap daemon service stop`),
  run `kcap update`, then start it again — only then is the binary replaceable.
  The update-flow notice prints this Windows-specific instruction when a daemon
  is detected running.
- `kcap daemon restart` still *functions* on Windows (it's just IPC + lifecycle),
  but it only adopts a newer version if the binary was actually replaced — i.e.
  the daemon was stopped during the update. It does not work around the lock.
- The Windows Scheduled Task supervisor is therefore not part of the supervised
  restart path. *Possible future enhancement:* have the npm launcher orchestrate
  stop → update → start on Windows.

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
| **Supervised** (launchd/systemd) | Supervisor-injected env that proves *this process* was launched by the supervisor: `INVOCATION_ID` (systemd) or `XPC_SERVICE_NAME` matching our label (launchd); **or** our explicit `KCAP_DAEMON_SUPERVISED=1` marker | Request shutdown and **exit with a dedicated non-zero code** (`ExitCodes.RestartRequested`); the unit's failure-restart policy relaunches the new binary |
| **Detached** (`start -d`; `--log-file` present, not supervised) | else-branch | **Self-respawn**: spawn a fresh detached `kcap-daemon` (same argv, `--await-lock`), then `StopApplication()` and exit **0** |
| **Foreground** (interactive; no `--log-file`, not supervised) | no log-file & not supervised | Don't auto-act — set pending + log "restart pending; exit and restart to apply" |

**Why exit non-zero for supervised (not exit 0).** The installed units relaunch
**only on failure** — launchd `KeepAlive`/`SuccessfulExit=false`, systemd
`Restart=on-failure`, Windows `RestartOnFailure`. A clean exit 0 would leave the
daemon **stopped**. So the supervised path exits with a dedicated non-zero code,
which those existing policies already honor — **no unit changes required** on
launchd/systemd. This is consistent with the daemon's existing non-zero exits
(config error → 1, name-in-use → 2/3), which already trigger supervisor relaunch.
A user-initiated `kcap daemon service stop` is an *administrative* unit stop, so
the failure-restart policy does not relaunch then — correct. Expect the
supervisor's normal relaunch latency (systemd `RestartSec=5`).

Mechanically this mirrors the existing `nameInUse` flag in `DaemonRunner.RunAsync`:
the coordinator sets a `restartRequested` flag and calls `StopApplication()`;
`RunAsync` returns `restartRequested ? ExitCodes.RestartRequested : (nameInUse ? 3 : 0)`.

**Detection robustness — why not "a unit exists for my id".** That fallback is
unsound: a user can have an *installed-but-stopped* service and separately run a
detached daemon under the same name; "unit exists" would misclassify the detached
daemon as supervised and exit it with no relaunch. Instead we key on
supervisor-injected env vars that are present only on the process the supervisor
**actually launched** (`INVOCATION_ID`/`XPC_SERVICE_NAME`) — a hand-started
detached daemon has neither, even if a unit is installed. These cover pre-upgrade
installs (no `KCAP_DAEMON_SUPERVISED` marker) without the false positive, and
need no cross-layer "does a unit exist" probe. If genuinely uncertain, default to
**detached / self-respawn**: a spurious extra exit-and-relaunch under a supervisor
is self-correcting (the loser hits the flock and exits with code 2), whereas a
wrong "just exit" would silently kill a real detached daemon — the worse failure.

### Busy definition

`IsBusy = orchestrator.ActiveCount > 0 || evalRunner.HasActiveRun`

- `ActiveCount` already exists: agents with status `Starting`/`Running` (counts
  both server-launched and local/private agents).
- Evals run **inline** (`EvalService.RunQuestionAsync`) and are *not* reflected
  in `ActiveCount`, so a restart gated only on agents could cut off an eval
  mid-run. Track active eval runs in `EvalRunner` as a **set keyed by
  `EvalRunId`** (`HasActiveRun = !activeRuns.IsEmpty`), not a single bool: the
  daemon's eval handlers (PrepareEval / RunQuestion / FinalizeEval / CancelEval)
  are server-driven and can overlap, and a lone bool would let one run's finalize
  clear the flag while another run is still active. The run id is added on
  PrepareEval (and defensively on RunQuestion) and removed on FinalizeEval /
  CancelEval, covering the whole eval lifecycle.

## Restart execution

### Supervised (`SupervisedExitStrategy`)

Set the `restartRequested` flag and call `lifetime.StopApplication()`. The
existing cleanup path runs (disposes `DaemonLock` → releases the flock + deletes
the PID file, disposes orchestrator/connection), and `RunAsync` returns
`ExitCodes.RestartRequested` (a dedicated **non-zero** code). The unit's
failure-restart policy then relaunches the now-updated binary — launchd
`KeepAlive`/`SuccessfulExit=false`, systemd `Restart=on-failure` (after
`RestartSec=5`). **No unit changes are required**; a clean exit 0 would *not*
relaunch under these policies, which is why the dedicated code matters. The
freshly-started daemon clears any stale `<name>.restart-pending` marker on startup
(its running version now matches the on-disk binary).

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
  `FrameType.Restart = 7` frame; the daemon replies with `FrameType.RestartAck =
  69` on success (or the existing `Error` frame on failure).
- **Codec format (explicit):** `FrameCodec.Encode`/`Decode` are per-type switches
  that *throw* on any unlisted type, so both new types must be added. The mode
  travels in `LocalFrame.Text` (`"when-idle"` / `"now"` / `"force"`) and the ack
  status likewise — so `Restart` and `RestartAck` join the existing UTF-8-`Text`
  arms (alongside `Error`/`Attach`/`AgentList`). Add `LocalFrame.Restart(mode)`
  and `LocalFrame.RestartAck(status)` factory helpers mirroring `Error(...)`.
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
  when a restart is queued: the daemon's **running** version, reason
  (`self-detected` / `requested`), and queued-at timestamp. The successor daemon
  deletes it on startup.
- **Version display is honest about what we know.** Size+mtime detection proves
  the on-disk binary *changed* but does not reveal its version (we don't exec it).
  We know our own running version (`ResolveDaemonVersion()`), so `kcap daemon
  status` prints, e.g.: `restart pending: running v0.4.11, newer binary detected
  on disk (queued 12:03, waiting for idle)` — **no fabricated target version**.
  Adding `kcap-daemon --version` (the optional follow-up) is what would let us
  show `v0.4.11 → v0.4.12`. Status reads the marker the same way it already reads
  PID files — no socket round-trip.
- `kcap daemon doctor` enumerates per-name files via
  `DaemonLockPaths.EnumerateNames()`, which today globs only `*.lock`/`*.pid`.
  **Extend it to also union `*.restart-pending`** so a marker-only leftover (e.g.
  a crash between queueing and restart) is enumerated, reported (it classifies as
  STALE — no live lock), and removed under `--clean`.

## Update-flow UX (informational only)

Self-detection does the functional work; the update flow merely informs. The
launcher *does* know the new version here (it just installed `@latest` and ran
the `--check` probe), so unlike the daemon's status line it can name it. After
`npm install` + refresh, if any daemon is running:

- **macOS / Linux** — print:
  > kcap daemon '<name>' is running and will restart automatically when idle to
  > pick up v0.4.12 (`kcap daemon status` to check, `kcap daemon restart --force`
  > to apply now).
- **Windows** — print the manual flow instead (the running daemon's exe is
  locked, so the update can't replace it and auto-restart won't apply):
  > kcap daemon '<name>' is running on the old binary. Stop it (`kcap daemon
  > service stop` or `kcap daemon stop`), re-run `kcap update`, then start it
  > again to pick up v0.4.12.

There is no functional coupling to the daemon on the success path — purely a
heads-up.

## Service install change

Inject `KCAP_DAEMON_SUPERVISED=1` into the unit environment at
`kcap daemon service install` (via the `ServiceSpec.Environment` dictionary, set
where `ServiceInstall` builds the env). This is the explicit, belt-and-suspenders
signal. Services installed before this feature lack it but are still detected
correctly at runtime via the supervisor-injected env (`INVOCATION_ID` /
`XPC_SERVICE_NAME`) — no reinstall required, and no reliance on the unsound
"a unit exists for my id" probe.

## Components & files

**New (daemon):**

- `src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs` — background loop,
  decision/queue/idle-gate logic, marker writer.
- `src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs` +
  `SupervisedExitStrategy` + `DetachedRespawnStrategy`.
- Supervision detection — a small daemon-local helper that reads
  `KCAP_DAEMON_SUPERVISED` / `INVOCATION_ID` / `XPC_SERVICE_NAME` from the
  environment. Pure env reads; no dependency on the CLI's `IServiceManager` and
  no cross-layer "unit exists" probe.

**Modified:**

- `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs` — add `Restart = 7`,
  `RestartAck = 69`.
- `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` — `Restart(mode)` /
  `RestartAck(status)` factory helpers.
- `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` — add `Restart`/`RestartAck` to
  the UTF-8-`Text` `Encode`/`Decode` arms (else the codec throws on them).
- `src/Capacitor.Cli.Core/DaemonLockPaths.cs` — `EnumerateNames()` also unions
  `*.restart-pending`.
- A shared `ExitCodes.RestartRequested` constant (non-zero, distinct from 1/2/3)
  in `Capacitor.Cli.Core` or daemon-local.
- `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` — route `Restart`
  frames to `RestartCoordinator`.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — expose `IsBusy` /
  reuse `ActiveCount`.
- `src/Capacitor.Cli.Daemon/Services/EvalRunner.cs` — active-run **set keyed by
  `EvalRunId`**; expose `HasActiveRun`.
- `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — register `RestartCoordinator` (+
  hosted service), capture original argv for respawn, honor `--await-lock`, and
  return `ExitCodes.RestartRequested` when `restartRequested` is set (mirrors the
  existing `nameInUse` exit-code branch).
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
- Startup strategy selection from injected env: supervised via
  `KCAP_DAEMON_SUPERVISED`, supervised via `INVOCATION_ID`, supervised via
  `XPC_SERVICE_NAME`, detached (none set, `--log-file` present), foreground (none
  set, no `--log-file`). **Regression for the review finding:** unit installed
  but daemon hand-started (none of the env signals set) → classified **detached**,
  not supervised.
- Binary-change detection via an injected stat seam (size/mtime change → pending;
  transient failure → skip).
- `--force` overrides the gate; an active eval run (`EvalRunId` in the set) blocks
  a restart; **overlapping eval runs** — finalizing run A while run B is active
  keeps `HasActiveRun` true.
- Supervised strategy returns `ExitCodes.RestartRequested` (non-zero); detached
  strategy returns 0. Strategies sit behind `IRestartStrategy`, so coordinator
  tests never spawn real processes.

**Integration:**

- `--await-lock` acquires the flock after a holder releases it.
- `kcap daemon restart` round-trips over the control socket (queue + force).
- Marker file is written on queue and read/printed by `status`.

**AOT:** `dotnet publish -c Release` then grep for `IL3050`/`IL2026`. The
process-spawn and stat paths are reflection-free and should stay clean.

## Risks & mitigations

- **Supervised daemon exits 0 and stays stopped** (the units only relaunch on
  failure) → supervised strategy exits with the dedicated non-zero
  `ExitCodes.RestartRequested`, which the existing policies honor; no unit changes.
- **Mis-detected supervision kills a detached daemon** → detection keys on
  supervisor-injected env present only on the launched process (not "a unit
  exists"); uncertainty defaults to detached/self-respawn, which is
  self-correcting rather than fatal.
- **Respawn race on the flock** → `--await-lock` retry on the successor + the
  existing PID-match-guarded `DaemonLock.Dispose` handle the window.
- **Overlapping eval runs clear the busy flag early** → tracked as a set keyed by
  `EvalRunId`, not a single bool.
- **Perpetually-busy daemon never updates** → by design (never interrupt work);
  made visible via `status` and overridable via `restart --force`.
- **Windows can't replace a running daemon binary** → auto-restart is scoped out
  on Windows; the update notice prints the stop → update → start manual flow.
- **Same-version reinstall churn** → bounded to a single idle restart; optional
  `kcap-daemon --version` confirmation can eliminate it later.
