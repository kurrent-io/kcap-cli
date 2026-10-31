# Windows daemon as a Windows Service — design

**Status:** implemented on `worktree-windows-daemon-service`, one PR, verified in Windows Sandbox with UAC both off and on; a second Windows user still needs a VM

## Implementation notes

- **macOS and Linux are untouched.** Every new behaviour is Windows-only: the `--system` verbs, the
  service manager, `--replace` ending a standalone daemon, the app's supervision and service offers, and
  the status JSON's `supervisor`, `service_upgrade_available` and `windows_service_installable`, which are
  omitted when unset so other platforms' output is byte-identical. The one shared change is the daemon
  taking its lock before it opens its log file, which only changes what a refused duplicate does
- **Offers.** The app offers the service once, while the daemon is idle, and remembers a decline;
  Settings → Daemon → "Run as a Windows service" is the way back. Both go through the mutation lane's
  `InstallWindowsService` verb, and a declined prompt is the CLI's exit 1223
- **An unanswered prompt.** The app waits ten minutes for the UAC prompt, which can outlive it. Once the
  app has given up, approving the stale prompt installs nothing (seen under UAC in Sandbox), so the card
  says to run it again
- **A task that cannot be retired.** Retiring the Scheduled Task can fail after it has already ended the
  task's daemon — a task the user may not delete, such as one registered from an elevated token. The
  install then starts the task again and fails, rather than leaving the daemon with no supervisor and
  nothing running. When the service is installed and the daemon is left outside it, the app's takeover
  offer enrolls it (verified under UAC)
- **Testing UAC in Sandbox.** Turning `EnableLUA` on and restarting the sandbox from inside keeps its
  state. The sandbox window does not show the secure desktop, so `PromptOnSecureDesktop` is set to 0 (as
  SYSTEM, through `wsb exec`) to see and answer the prompt

## Goal

On Windows, the kcap daemon is supervised by the Service Control Manager (SCM) instead of a per-user
logon Scheduled Task. The desktop app stays a per-user Velopack install and registers the service once,
behind one UAC prompt

What this buys over the Scheduled Task:

- **One registration for the machine.** Once approved, the service exists whatever state any user's
  daemon is in, and starts that user's daemon at every sign-in. (The gap it closes, reproduced in
  Windows Sandbox: with a standalone daemon running, the app connects, installs nothing and warns
  nothing.)
- **Real supervision.** SCM restarts a crashed broker; the broker restarts a crashed user daemon. No
  `.cmd` restart loop, no `ping` sleep, no `conhost --headless` host
- **Structured status.** Status comes from the SCM API and the broker's own IPC, not from parsing
  `schtasks /Query` text, which is localized (`StatusFromQuery` matches the English word `Running`)
- **Clean stop.** Stopping is a request to the process that owns the daemon, not `schtasks /End` plus a
  pid-identity kill of a daemon that outlived its console host
- **Visible to IT.** It shows in `services.msc`, Event Log and every fleet tool that inventories services

What it does **not** buy: a daemon running before anyone signs in. The daemon runs coding agents as the
user, with the user's profile, credentials and git identity. Windows only hands a service a usable user
token for a signed-in session, and the only way to run as the user without one is to store their
password, which this design rejects (see Alternatives). The broker starts at boot; the user's daemon
starts at sign-in, exactly as it does today

## The industry pattern

A process that must act *as the interactive user* but be supervised *by the OS* is solved on Windows
the same way by remote-access agents, endpoint-management agents and desktop apps with a privileged
half: a **machine-wide service running as LocalSystem that launches a per-user process into each
signed-in session**. The service owns supervision and lifetime; the user process owns everything that
must be the user's

The mechanics are standard Win32:

1. The service registers for session notifications (`SERVICE_ACCEPT_SESSIONCHANGE`) and handles
   `SERVICE_CONTROL_SESSIONCHANGE` (`WTS_SESSION_LOGON`, `WTS_SESSION_LOGOFF`, unlock/reconnect)
2. For a session that should run the daemon it calls `WTSQueryUserToken(sessionId)` (LocalSystem holds
   the `SeTcbPrivilege` this needs), `DuplicateTokenEx` to a primary token, `CreateEnvironmentBlock` for
   the user's environment, and `CreateProcessAsUser` into that session
3. On start it enumerates existing sessions (`WTSEnumerateSessions`) so a broker restart re-adopts
   daemons for users already signed in

Installation is equally standard: a **per-machine MSI** with `ServiceInstall`/`ServiceControl` rows (or
the WiX equivalents) installs the binaries under `Program Files`, registers the service as
`LocalSystem`, `Automatic (Delayed Start)`, with SCM failure actions set to restart, and removes it on
uninstall. The binaries live where only administrators can write: a LocalSystem service that runs a
file a standard user can replace is a local privilege escalation

A per-user app installs the same service without becoming a per-machine install by using a
**privileged helper**: the app asks for elevation once (UAC), and the elevated step copies the service
binary into an administrator-only directory and registers it with SCM. Only the code that runs as
SYSTEM has to live there; anything the service starts *as the user* can stay in the user's install,
because running it with the user's own token grants nothing the user does not already have. This design
uses the helper, so Velopack stays the only installer

## Architecture

```
SCM ── kcap-service.exe (LocalSystem, Program Files\kcap\service, auto-start)
         │  session notifications, WTSQueryUserToken + CreateProcessAsUser
         ├── session 1: %LocalAppData%\kcap\current\kcap-daemon.exe  (as alice, from alice's Velopack install)
         └── session 2: %LocalAppData%\kcap\current\kcap-daemon.exe  (as bob, from bob's)
CLI / desktop app ── \\.\pipe\kcap-service ── broker (status, start, stop, enroll; caller-scoped)
```

### `kcap-service.exe` (new, NativeAOT)

A small, separate executable rather than a mode of `kcap-daemon.exe`: it runs as LocalSystem, so it must
stay small and auditable, and it must never load the code that hosts agents

- **SCM integration.** `ServiceBase` from `System.ServiceProcess` or a direct
  `StartServiceCtrlDispatcher`/`RegisterServiceCtrlHandlerEx` binding via `LibraryImport`. Whichever is
  chosen must publish AOT-clean (`IL3050`/`IL2026`); the spike proves it
- **Supervision.** One supervised daemon per enrolled user with a session. A non-zero exit relaunches
  after 5 s; exit 0 is a deliberate stop and is not relaunched — the same contract the Scheduled Task
  wrapper, systemd's `Restart=on-failure` and launchd's `SuccessfulExit=false` already keep
- **Process tree.** Each daemon goes into its own Job object owned by the broker, so a stop ends the
  daemon and every agent it hosts. The job is *not* kill-on-close: a broker restart (crash, update)
  must not take the user's agents with it; the restarted broker re-adopts by pid identity
- **Logon/logoff.** Logon of an enrolled user starts their daemon. Logoff ends it (the session's
  processes end with it anyway; the broker records the stop so it does not relaunch into a dead session)
- **Never runs user-controlled code as SYSTEM.** The service binary is the only code that runs as
  SYSTEM, and it lives in an administrator-only directory. The daemon it starts is the user's own,
  from their Velopack install, always with the user's token. Anything it reads or opens from a user's
  profile — enrollment, the daemon image — it opens while impersonating that user
  (`ImpersonateLoggedOnUser`), so a junction in the user's profile cannot make SYSTEM read or start a
  file the user could not
- **Logging.** Event Log for its own lifecycle; the daemon keeps its own log under the user's config
  directory as today

### Enrollment (which users get a daemon)

Opt-in per user, stored in the user's own hive — `HKCU\Software\Kurrent\kcap\Daemon` (name, profile,
max agents, daemon path) — so enrolling needs no elevation and one user cannot enroll another. The broker
reads it from `HKEY_USERS\<SID>` while impersonating. The daemon path is the user's Velopack
`current\kcap-daemon.exe`; Velopack swaps `current` on update, so a relaunch picks up the new daemon
with no elevation and no copy to keep in step. The captured environment the `.cmd` wrapper bakes in today
moves to the user's owner-only config directory and is passed to `CreateProcessAsUser` merged over the
user's `CreateEnvironmentBlock` result

### Control IPC

`\\.\pipe\kcap-service`, DACL: SYSTEM and Administrators full, Authenticated Users read/write. Every
request is scoped to the caller's SID, taken from `ImpersonateNamedPipeClient` — never from the request
body — so a user can only see and act on their own daemon. Verbs: `status`, `start`, `stop`,
`restart`, `enroll`, `unenroll`. The frame format follows the local control IPC rule already in force:
append-only frame types and capability-gated verbs

Granting `SERVICE_START`/`SERVICE_STOP` on the service itself is the other common choice; it is
rejected here because it is machine-wide — any user could stop every user's daemon

### CLI and app

- `ServicePlatform` gains `WindowsService`; `ServiceManagerFactory.ForCurrentOs` picks it when the
  broker service is installed and running, and keeps `WindowsScheduledTask` otherwise. The npm and
  script installs keep the Scheduled Task, as does a user who declines the UAC prompt
- A `WindowsServiceManager : IServiceManager` maps `Install` → `enroll` (+ `start`), `Uninstall` →
  `unenroll` (+ `stop`), `Start`/`Stop`/`Status`/`Query` → the pipe. `GenerateFiles`/`UnitDirectory`
  point at the user's enrollment and environment files
- The desktop app's lifecycle controller already drives everything through `kcap daemon service …`, so
  its matrix is unchanged; the `Installed`-but-not-running and `NotInstalled` rows now reach the broker.
  The unsupervised-daemon gap still needs its own fix in the app (connected + no supervisor ⇒ offer to
  hand the daemon to the service), because a standalone `kcap daemon start` remains possible
- **The daemon takes its lock before it opens its log file**, and a held lock is a clean refusal (exit 0
  when supervised). Today the log opens first with `FileShare.Read`, so on Windows a second same-name
  daemon dies on an unhandled `IOException` before it reaches the lock — under the service, a crash loop

### Installer and updates

- **The desktop app stays a per-user Velopack install**, and `kcap-service.exe` ships inside it. Velopack's
  MSI is not used: its install hook runs impersonated, so it has no admin rights under UAC, and its silent
  per-machine install lands in a user-writable `C:\<packId>` (see Spike results)
- **Install: one UAC prompt.** The app runs `kcap-service.exe install` elevated (`runas`). The elevated
  step copies `kcap-service.exe` from the Velopack install into `%ProgramFiles%\kcap\service\` — which
  inherits the administrator-only ACL — creates the service (`LocalSystem`, `Automatic (Delayed Start)`,
  failure actions restart), and starts it. It copies only the service binary, never anything that runs
  as the user. Enrolling the user afterwards is unelevated
- **Updates.** The daemon, CLI and app update silently with Velopack, as today; the service picks up the
  new daemon on its next launch. Only a new *service* binary needs elevation: the service reports its
  version and capabilities over the pipe, and when the app ships a newer one it asks for the same UAC
  prompt to stop the service, replace the binary and start it again (the daemon keeps running and is
  adopted, as the spike showed). The service is small and changes rarely, so this prompt is rare
- **Uninstall.** Velopack's uninstall hook runs as the user with no UI, so it cannot remove a service.
  It unenrolls the user over the pipe instead, which needs no elevation; the service stops that user's
  daemon, and when no enrolled user has a daemon binary left on the machine, it deletes its own
  registration and schedules its directory for removal. `kcap daemon service uninstall --system` (UAC)
  removes it on demand
- **Signing.** Not required by this design. Until the Windows build is Authenticode-signed the UAC
  prompt names an unknown publisher, which is worth fixing for trust but is not a security dependency:
  nothing installs code as SYSTEM without that prompt

### Migration from the Scheduled Task

Runs in the user's app once the service is installed (the service cannot enumerate another user's
tasks): uninstall the `kcap-daemon-<name>` task (`schtasks /End` + `/Delete`, wrapper removed), enroll the
same name and profile with the service, and verify the service's daemon serves before reporting success —
the same verify-then-commit shape `kcap daemon service install --verify` already has. On failure the task
is reinstalled, so the user is never left with no supervisor. The Velopack install itself is unchanged

## Invariants this keeps

- **The daemon never looks for a credential.** No password is stored or requested; the broker uses the
  token Windows already holds for the signed-in session
- **The daemons directory is a fixed location.** The daemon runs as the user, so it resolves the same
  path it does today
- **The daemon's state directory is owner-only.** Unchanged; the daemon still creates it as the user
- **Every server-driven launch passes the consent gate.** Untouched; the broker never handles server
  traffic

New invariant to add to `CLAUDE.md` when this lands: *the broker never runs user-controlled code as
SYSTEM* — the only SYSTEM code is the service binary in an administrator-only directory; everything it
starts runs with a user's token, and it opens user files only while impersonating that user

## Alternatives rejected

| Option | Why not |
|---|---|
| Service running *as the user* with a stored password (the CI-runner model: GitHub Actions, Azure Pipelines, Jenkins agents) | Stores the user's password with LSA; breaks on every password change; impossible for passwordless Microsoft/Entra accounts; contradicts "the daemon never looks for a credential". It is the only option that runs before sign-in, which is not worth those costs |
| Daemon itself runs as LocalSystem | Agents run arbitrary model-directed code; as SYSTEM that is full machine compromise. They also need the user's profile and credentials |
| Per-user service templates (`SERVICE_USER_OWN_PROCESS`, the `_LUID` instances) | Microsoft documents them as Windows components managed through templates; no supported third-party path |
| Keep the Scheduled Task, fix its rough edges | Cheapest, no admin. Fixes the localized status parse (Task Scheduler COM API) and the gap (app offers to install), but not install-time registration. Still the right path for npm/script installs |

## Spike results

The spike lives in `spike/WindowsService/` (`kcap-service.exe`, not in the solution). It was run in
Windows Sandbox against the 1.4.0-beta.4 daemon, installed with `sc.exe` under `Program Files`

| Question | Result |
|---|---|
| AOT | Direct SCM/WTS/userenv bindings via `LibraryImport` and `System.ServiceProcess.ServiceBase` (10.0) both publish with no IL3050/IL2026. `ServiceBase` handles session-change events, so production can use it; the WTS and `CreateProcessAsUser` bindings are needed either way |
| Launch into the session | The daemon runs as the user, in session 1, with the service (SYSTEM, session 0) as parent, the user's profile and registry environment (the vendor CLI on the user's `PATH` was found), connects to the server and registers under its usual name |
| ConPTY | A hosted Claude Code agent under the service-launched daemon renders in the desktop app's terminal and takes input |
| Enrollment and control | `HKCU` opt-in with no elevation; pipe verbs scoped to the caller's SID from pipe impersonation |
| Supervision | Killed daemon relaunched in 5.4 s; stop through the pipe exits 0 and is not relaunched; `kcap daemon stop` exits 0 and is not relaunched; stopping the job ends the daemon and its agent |
| Service restart, upgrade, crash | Stopping the service leaves the daemon running; replacing the binary and starting it adopts the daemon; killing the service, SCM restarts it in about 5 s and it adopts |

Found along the way:

- **A job object's name lives only while a handle to it is open**, not while processes are in it. The
  first adoption attempt found no job after a service restart and launched a duplicate. The service now
  hands the daemon a query-only handle to its own job (`DuplicateHandle`), which keeps the name alive,
  and adopts a running daemon by binary and session even when the job is gone
- **A second daemon with the same name crashes instead of refusing.** It dies on an unhandled
  `IOException` opening the log file the first daemon holds (`0xC0000409`), before it reaches the lock,
  so any supervisor crash-loops it. It should refuse cleanly — exit 0 when supervised
- **`CreateProcessAsUser` with no standard handles loses the daemon's stderr** (the `[startup]` lines).
  The service must pass an inheritable stderr file handle, as the task wrapper's `2>>` does
- **The app and CLI fight the service until they know about it.** With the service stopping the
  daemon and the app open, the app installed the Scheduled Task and started a second daemon from
  `%LocalAppData%`. The `WindowsService` platform in the CLI must land before the service ships
- **Velopack's MSI cannot register the service.** Its install hook is `InstallHookDeferred`,
  `Execute="deferred" Impersonate="yes"`: on a machine with UAC on it runs with the user's limited
  token. Sandbox has UAC off (`EnableLUA = 0`), so the hook *could* create a service there — a result
  that does not carry over
- **A silent per-machine Velopack MSI installs to `C:\<packId>`, where Authenticated Users have
  Modify.** `Program Files` is only chosen by the install-scope dialog, which `/qn` skips. A SYSTEM
  service whose binary sits there is a local privilege escalation

## Decisions

- **Velopack stays the only installer, per-user.** No MSI. The app registers the service through a
  privileged helper: one UAC prompt copies `kcap-service.exe` into `%ProgramFiles%\kcap\service\` and
  creates the service
- **The service runs each user's daemon from their own Velopack install**, so the daemon keeps updating
  silently; only a new service binary takes a UAC prompt. No silent service-driven updates; signing is
  not a prerequisite for this design

## Open questions

1. When the app offers the UAC prompt (onboarding, the first time it finds no supervisor, or both), and
   how often it asks again after a decline (the Scheduled Task stays the fallback supervisor)
2. The service's self-removal rule on uninstall: which evidence counts as "no enrolled user has a daemon
   binary left", given a signed-out user's profile may be unloaded
3. Elevation behaviour needs a VM with UAC on; Sandbox cannot show it
4. Multiple Windows users on one machine each with their own server identity: confirm the fixed
   daemons directory and the per-user `flock` keep them apart. Needs a VM
5. Graceful stop: the spike stops by ending the job. Production should ask the daemon first over its
   local control socket and end the job only after a timeout

## Testing

- Unit: the broker's session-state machine (logon, logoff, crash, exit 0, broker restart re-adoption)
  behind seams for the Win32 calls, as `WindowsScheduledTaskServiceManager` already does for `schtasks`
- Windows Sandbox (its account is an administrator, with UAC off): install the app, run the elevated
  service install, sign in, check the service, the daemon's parentage (the broker) and image path (the
  Velopack install), a Velopack update swapping the daemon under a running service, crash recovery,
  `kcap daemon service stop` from the CLI, the app's Start/Repair rows, migration from an existing
  Scheduled Task, and uninstall leaving no service, no task and no daemon
- A VM with UAC on: the install and service-update prompts, a decline falling back to the task, and the
  elevated step refusing to run from anywhere but the user's Velopack install
- Not testable in Sandbox: a real sign-out/sign-in (sign-out ends the Sandbox) and a second Windows
  user; those need a VM
