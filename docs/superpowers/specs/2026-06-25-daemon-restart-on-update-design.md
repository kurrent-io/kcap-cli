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
- Zero coupling required from the update flow on the success path: detection is
  daemon-side. **Caveat — detection assumes same-path, in-place binary
  replacement** (the npm global-install path: `npm install -g …@latest`
  overwrites the file at the stable `node_modules/@kurrent/kcap-<platform>/…`
  path that `Environment.ProcessPath` points to). Versioned-directory or
  symlink-swap installs (e.g. Homebrew, which writes a new Cellar dir and
  repoints a symlink) leave the polled path — and the unit's baked `ExecStart`
  — pointing at the *old* binary, so they are **not** auto-detected, and `kcap
  daemon restart` does not fix them either (it re-runs the old path). The path for
  those is stop/start with the updated CLI, or a service reinstall. See
  *Install-shape assumption*.

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
- **Preflight abort, not a success-path notice.** This is the critical ordering
  point: `kcap.js`'s `runUpdate()` runs `npm install` *first* and only prints
  notices afterward — but on Windows the install itself **fails** on the locked
  `kcap-daemon.exe`, so any post-install notice is unreachable. Therefore on
  Windows the launcher must, **before** `npm install`, probe for a running daemon
  (a machine-readable probe on the still-old, not-yet-replaced binary — e.g.
  `kcap daemon status`/a dedicated `--running` check) and, if one is found,
  **abort with instructions** rather than attempt the doomed install:
  > A kcap daemon is running and locks the binary, so the update can't replace
  > it. Stop it first (`kcap daemon service stop` or `kcap daemon stop`), then
  > re-run `kcap update`.
- `kcap daemon restart` still *functions* on Windows (it's just IPC + lifecycle),
  but it only adopts a newer version if the binary was actually replaced — i.e.
  the daemon was stopped during the update. It does not work around the lock.
- The Windows Scheduled Task supervisor is therefore not part of the supervised
  restart path. *Possible future enhancement:* have the npm launcher orchestrate
  stop → update → start on Windows automatically.
- **First-upgrade limitation (residual):** the preflight lives in `kcap.js`, so it
  only protects upgrades *from* a version that already ships it. The very first
  upgrade from a pre-feature version runs the old launcher, which still attempts
  `npm install` and hits the locked-exe failure (the new postinstall runs only
  *after* files are written — too late to help). This is inherent to any
  launcher-side change and is non-destructive (the install fails, the old daemon
  keeps running); the user retries after stopping the daemon. Documented, not
  fixed.

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
   daemon's **running** version (the on-disk target is unknown without execing
   the new binary — see *Observability*), writes the `<name>.restart-pending`
   marker, and logs it.
   Transient stat failures (binary momentarily missing/being swapped mid
   `npm install`) are tolerated — the tick is skipped, not treated as a change.
2. **If a restart is pending and `!IsBusy`** — invoke the selected strategy.
3. **If pending and busy** — do nothing. The marker persists; `status` shows it.

Idle latency of up to one tick (~15s) after work finishes is acceptable; no
event-driven wakeup is required.

### Startup decision tree (strategy chosen once, logged)

| Context | Detection | Restart action |
|---|---|---|
| **Supervised** (launchd/systemd) | Our **name-specific** marker `KCAP_DAEMON_SUPERVISED=<service-id>` whose value equals the daemon's own sanitized `--name` *(authoritative)*; **else** a false-positive-safe pre-marker probe (systemd: `kcap-daemon-` in `/proc/self/cgroup` **and** `SYSTEMD_EXEC_PID == ProcessId`; launchd: `XPC_SERVICE_NAME` equals our computed label `io.kurrent.kcap.daemon.<sanitized-name>`) | Request shutdown and **exit with a dedicated non-zero code** (`ExitCodes.RestartRequested`); the unit's failure-restart policy relaunches the new binary |
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

**Detection robustness.** Two rejected approaches and why the chosen probe is
false-positive-safe:

- *"A unit exists for my id"* is unsound — an *installed-but-stopped* service plus
  a separately hand-started detached daemon under the same name would misclassify
  the detached daemon as supervised and exit it with no relaunch.
- *`INVOCATION_ID` alone* is also unsound — per systemd.exec(5) it is set for the
  whole unit runtime cycle and is **inherited by child processes**, so a daemon
  hand-started from *another* systemd-managed process would inherit it and be
  misclassified. The variable that proves *direct* launch is `SYSTEMD_EXEC_PID`,
  which equals the PID systemd `exec`'d.

So the chosen signals are: the **name-specific** marker
`KCAP_DAEMON_SUPERVISED=<service-id>` (authoritative; set on all new installs),
else — for pre-marker installs — a combination that can't be inherited: on
systemd, our unit's cgroup (`kcap-daemon-…` in `/proc/self/cgroup`) **and**
`SYSTEMD_EXEC_PID == ProcessId`; on launchd, `XPC_SERVICE_NAME` *exactly equal to
our computed label*. The dangerous direction is a **false positive** (think
supervised when actually detached → exit, no relaunch, daemon gone), so detection
is deliberately conservative and **uncertainty defaults to detached / self-respawn**.

**Env-inheritance vector.** Env vars are inherited, and the daemon's PTY children
(hosted agents) inherit the daemon's environment — `UnixPtyProcess.Spawn` unsets a
few vars (`CLAUDECODE`, `ANTHROPIC_API_KEY`, `KCAP_AGENT_ID`, …) but not the
supervision vars. So a `kcap daemon start` run from inside a supervised daemon's
agent could inherit a supervision marker. Two facts make this safe once the marker
is name-specific:

1. **Same-name nesting is impossible** — a second daemon under the same name
   can't run; it fails to acquire the per-name flock and exits code 2. So an
   inherited signal only matters for a *different*-name daemon.
2. **All three signals are name-bound** — the launchd label and the
   `SYSTEMD_EXEC_PID==ProcessId` check are name/PID-specific by construction
   (a different-name daemon computes a different label; a child has a different
   PID). Making the marker name-specific (`=<service-id>`, matched against the
   daemon's own sanitized name) brings it in line: a different-name daemon that
   inherits `KCAP_DAEMON_SUPERVISED=laptop` but runs as `--name ci` sees a
   mismatch and is correctly classified detached.

As defense-in-depth and hygiene, the daemon also **scrubs**
`KCAP_DAEMON_SUPERVISED` and `XPC_SERVICE_NAME` (and `INVOCATION_ID` /
`SYSTEMD_EXEC_PID`) from the env of every PTY child it spawns, so supervision
state never leaks into hosted agents or anything they launch.

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

## Install-shape assumption

Both detection (polling `Environment.ProcessPath`) and supervised relaunch (the
unit's baked `ExecStart=<resolved DaemonBinaryPath>`, e.g. `SystemdUnit.cs:28`,
set from the path `DaemonCommands.ServiceInstall` resolved at install time) assume
the **same path is overwritten in place** when a new version lands.

- **Covered:** the npm global install — `npm install -g @kurrent/kcap@latest`
  replaces files at the stable `node_modules/@kurrent/kcap-<platform>/…` path that
  both `Environment.ProcessPath` and the baked `ExecStart` point to. This is the
  project's primary distribution and the target of this feature.
- **Not auto-covered:** versioned-directory / symlink-swap installs (Homebrew
  writes `…/Cellar/kcap/<version>/…` and repoints a symlink). The new binary lives
  at a *new* path; the polled path and the baked `ExecStart` still resolve to the
  old version. Self-detection won't fire and a supervised relaunch would re-exec
  the old binary.

  **`kcap daemon restart` is *not* a workaround here.** The detached strategy
  respawns from the old daemon's `Environment.ProcessPath`, and the supervised
  path relaunches from the unit's baked `ExecStart` — both the *old* resolved
  path. The correct manual path for a path-changing install is:
  - **Detached** — `kcap daemon stop`, then `kcap daemon start -d` using the
    **updated** CLI (it resolves the new sibling `kcap-daemon` path).
  - **Supervised** — re-run `kcap daemon service install` (re-bakes `ExecStart`
    with the newly resolved path), or stop/start.

*Possible future strategies (out of scope):* (a) install to a stable
launcher/symlink path and have both the unit's `ExecStart` and the self-respawn
target use it, so a symlink repoint is observable and relaunch follows the new
version; (b) have `kcap daemon restart` carry the **caller-resolved** new daemon
path (the updated CLI resolves its sibling `kcap-daemon`) so the *detached*
respawn execs the new binary — this still wouldn't fix supervised relaunch, which
needs the unit re-baked. Until then, auto-restart is scoped to same-path in-place
replacement.

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

Self-detection does the functional work; on **macOS / Linux** the update flow
merely informs. The launcher *does* know the new version here (it just installed
`@latest` and ran the `--check` probe), so unlike the daemon's status line it can
name it. After a successful `npm install` + refresh, if any daemon is running,
print one line:

> kcap daemon '<name>' is running and will restart automatically when idle to
> pick up v0.4.12 (`kcap daemon status` to check, `kcap daemon restart --force`
> to apply now).

This is a post-install heads-up with no functional coupling to the daemon.

**Windows is different** — there the daemon check is a *preflight abort before*
`npm install` (the install would otherwise fail on the locked binary), not a
post-install notice. See *Platform scope*.

## Service install change

Inject `KCAP_DAEMON_SUPERVISED=<service-id>` into the unit environment at
`kcap daemon service install` (via the `ServiceSpec.Environment` dictionary, set
where `ServiceInstall` builds the env). The value is the sanitized service id, and
the daemon honors it only when it equals its own sanitized `--name` — so an
inherited marker from a *different*-name daemon doesn't classify this one (see
*Env-inheritance vector*). Services installed before this feature lack the marker
but are still detected correctly at runtime via the false-positive-safe pre-marker
probe (systemd cgroup + `SYSTEMD_EXEC_PID == ProcessId`; launchd exact label match)
— no reinstall required, no reliance on the unsound "a unit exists" probe or on
the inheritable `INVOCATION_ID` alone.

## Components & files

**New (daemon):**

- `src/Capacitor.Cli.Daemon/Services/RestartCoordinator.cs` — background loop,
  decision/queue/idle-gate logic, marker writer.
- `src/Capacitor.Cli.Daemon/Services/IRestartStrategy.cs` +
  `SupervisedExitStrategy` + `DetachedRespawnStrategy`.
- Supervision detection — a small daemon-local helper:
  `KCAP_DAEMON_SUPERVISED == own sanitized name` (authoritative, name-specific),
  else systemd (`/proc/self/cgroup` contains `kcap-daemon-` **and**
  `SYSTEMD_EXEC_PID == ProcessId`) or launchd (`XPC_SERVICE_NAME` equals the
  computed label `io.kurrent.kcap.daemon.<sanitized-name>`). No dependency on the
  CLI's `IServiceManager`; no "unit exists" probe; `INVOCATION_ID` is *not* used
  alone (inheritable).

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
- `src/Capacitor.Cli.Daemon/Pty/Unix/UnixPtyProcess.cs` and the Windows ConPty
  spawn — add `KCAP_DAEMON_SUPERVISED` / `XPC_SERVICE_NAME` / `INVOCATION_ID` /
  `SYSTEMD_EXEC_PID` to the env vars scrubbed from spawned PTY children (alongside
  the existing `CLAUDECODE`/`KCAP_AGENT_ID`/… unsets), so supervision state never
  leaks into hosted agents.
- `src/Capacitor.Cli/Commands/DaemonCommands.cs` — new `restart` subcommand;
  `status` reads/prints the pending marker; usage text.
- `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`ServiceInstall`) — inject
  `KCAP_DAEMON_SUPERVISED=<service-id>` (name-specific).
- `npm/kcap/bin/kcap.js` — **Windows preflight** in `runUpdate()` *before*
  `npm install`: probe for a running daemon and abort with stop-first
  instructions (the install would otherwise fail on the locked exe). On
  macOS/Linux, a post-install one-line "daemon will restart when idle" notice.
- A machine-readable "is any daemon running" probe the launcher can call on the
  not-yet-replaced binary (a `daemon status`-style flag / exit code), so the
  Windows preflight doesn't parse human output.
- `src/Capacitor.Cli.Core/Resources/help-*.txt` — daemon help text.
- `README.md` — daemon quick-start + per-command `daemon` section (CLAUDE.md
  mandate; same PR).

## Testing

**Unit (TUnit):**

- `RestartCoordinator` gate logic with an injected `IsBusy` and a fake update
  signal: pending+idle → strategy fires; pending+busy → no fire; busy→idle
  transition fires.
- Startup strategy selection via an injected detection seam: supervised via
  `KCAP_DAEMON_SUPERVISED == own name`; supervised via systemd pre-marker probe
  (cgroup match **and** `SYSTEMD_EXEC_PID == ProcessId`); supervised via launchd
  exact label match; detached (none, `--log-file` present); foreground (none, no
  `--log-file`). **Regressions for the review findings:** (a) unit installed but
  daemon hand-started, none of the signals set → **detached**; (b) `INVOCATION_ID`
  present but `SYSTEMD_EXEC_PID != ProcessId` (inherited) → **detached**; (c)
  `XPC_SERVICE_NAME` set to a *different* job's label → **detached**; (d) marker
  value `laptop` inherited but daemon runs as `--name ci` → **detached** (marker
  is name-specific).
- The PTY-spawn env scrub removes `KCAP_DAEMON_SUPERVISED` / `XPC_SERVICE_NAME` /
  `INVOCATION_ID` / `SYSTEMD_EXEC_PID` from a spawned child's environment.
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
- The daemon-running probe the Windows preflight relies on returns the right
  exit code with a daemon up vs. down (the preflight ordering itself — abort
  before `npm install` — is launcher logic verified by inspection / a Node-side
  test of `runUpdate`).

**AOT:** `dotnet publish -c Release` then grep for `IL3050`/`IL2026`. The
process-spawn and stat paths are reflection-free and should stay clean.

## Risks & mitigations

- **Supervised daemon exits 0 and stays stopped** (the units only relaunch on
  failure) → supervised strategy exits with the dedicated non-zero
  `ExitCodes.RestartRequested`, which the existing policies honor; no unit changes.
- **Mis-detected supervision kills a detached daemon** → all signals are
  name-bound (name-specific marker, cgroup + `SYSTEMD_EXEC_PID == ProcessId`,
  exact launchd label) — never `INVOCATION_ID` / "a unit exists" / a bare `=1`
  marker alone; same-name nesting is blocked by the flock; uncertainty defaults to
  detached/self-respawn.
- **Inherited supervision env from a daemon-spawned agent** → marker is
  name-specific *and* supervision vars are scrubbed from every PTY child, so a
  `kcap daemon start` run from inside an agent can't inherit a matching signal.
- **`daemon restart` re-runs the old binary on path-changing installs** → both
  respawn (`Environment.ProcessPath`) and supervised relaunch (baked `ExecStart`)
  key off the old path; documented path is stop/start with the updated CLI or a
  service reinstall (not `restart`).
- **Windows install fails on the locked binary instead of warning** → the daemon
  check is a *preflight abort before* `npm install`, not a post-install notice
  (which would be unreachable once the install errors).
- **Non-npm install shapes (brew/symlink) silently keep the old binary** →
  detection + relaunch are scoped to same-path in-place replacement; the claim is
  narrowed and the documented path for those installs is stop/start with the
  updated CLI (detached) or a service reinstall (supervised) — not `restart`.
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
