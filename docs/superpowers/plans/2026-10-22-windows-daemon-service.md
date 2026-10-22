# Windows daemon service — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking

**Goal:** On Windows, a LocalSystem service (`kcap-service.exe`) supervises each enrolled user's daemon, launched into that user's session as that user, from that user's own Velopack install. The desktop app stays a per-user Velopack install and registers the service through an elevated helper behind one UAC prompt; the Scheduled Task stays the fallback for npm/script installs and for a declined prompt

**Spec:** `docs/superpowers/specs/2026-10-22-windows-daemon-service-design.md` (decisions, spike results, invariants). The spike in `spike/WindowsService/` is the reference implementation for the Win32 parts and is deleted once Task 5 lands

**Tech:** .NET 10 NativeAOT, `System.ServiceProcess.ServiceBase`, `LibraryImport` bindings to `wtsapi32`/`userenv`/`advapi32`/`kernel32`, named pipes with an explicit DACL, TUnit

**Product defaults this plan takes** (the spec's open questions; change them here if the answer differs):
- The app offers the UAC prompt in the onboarding daemon step and from an explicit "Run as a Windows service" action; it never prompts on its own after a decline, and shows a dismissible attention row while the task is the supervisor
- The service removes itself when its machine-side index of enrolled users is empty, pruning any SID whose profile is gone from `ProfileList`

## Delivery

One PR. Its commits follow the parts below, in this order, each building and passing on its own so the
history reads in the same steps a reviewer takes; squash-merge folds them into one change

| Part | Content | Tasks |
|---|---|---|
| A | The daemon refuses a duplicate instead of crashing | 1 |
| B | The app offers to hand an unsupervised daemon to the supervisor (Windows) | 2 |
| C | `kcap-service.exe` and its tests, built into the Windows release archive and the desktop bundle | 3–10 |
| D | CLI `WindowsService` platform, elevated install, migration, `--direct` stop, status JSON | 11–16 |
| E | Desktop app: install and update prompts, fallback, uninstall | 17–20 |
| F | `CLAUDE.md` invariant, README, end-to-end verification | 21–22 |

macOS and Linux keep their behaviour: every new path is Windows-only, and the status JSON's new fields
are omitted when unset

## File map

| Path | Role |
|---|---|
| `src/Capacitor.Cli.Daemon/DaemonRunner.cs` | Take the lock before the file logger opens its file (Task 1) |
| `src/Capacitor.App/Services/DaemonLifecycleController.cs` | Unsupervised-daemon offer (Task 2), supervisor-aware rows (Task 18–19) |
| `src/Capacitor.Cli.Service/` (new) | `kcap-service.exe`: host, supervisor, enrollment, pipe, installer, Win32 bindings |
| `test/Capacitor.Cli.Service.Tests.Unit/` (new) | Its tests, mirroring its directories |
| `src/Capacitor.Cli/Services/WindowsServiceManager.cs` (new) | `IServiceManager` over the pipe |
| `src/Capacitor.Cli/Services/WindowsServicePipeClient.cs` (new) | Pipe client the manager and the uninstall path share |
| `src/Capacitor.Cli/Services/IServiceManager.cs`, `ServiceManagerFactory.cs` | `ServicePlatform.WindowsService` and its selection |
| `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs` | `install --system`, `uninstall --system`, migration |
| `src/Capacitor.Cli/Commands/DaemonCommands.cs` | Hidden `stop --direct` |
| `src/Capacitor.Cli/Commands/ServiceStatusJson.cs` | `supervisor`, `service_upgrade_available` (additive-last) |
| `src/Capacitor.App/Services/KcapCli.cs` | `ServiceInstallSystemAsync`, snapshot fields |
| `src/Capacitor.App/ViewModels/Onboarding/DaemonEnabler.cs` | Windows install row goes through the UAC prompt |
| `src/Capacitor.App/Platform/Windows/WindowsUninstallCleanup.cs` | Unenroll on Velopack uninstall |
| `.github/workflows/cli-build.yml`, `scripts/package-cli.sh`, `.github/workflows/desktop.yml`, `scripts/install-windows-setup*.sh` | Build, gate and bundle `kcap-service.exe` |
| `README.md`, `src/Capacitor.Cli.Core/Resources/help-daemon.txt`, `CLAUDE.md` | Docs and the new invariant |

---

## Part A — the daemon refuses a duplicate cleanly

### Task 1: take the lock before opening the log file

The file logger is added at `DaemonRunner.cs:175`, and `RollingFileLoggerProvider`'s constructor opens the file with `FileShare.Read` (`RollingFileLoggerProvider.cs:30`). The lock is taken at `DaemonRunner.cs:280`. On Windows a second daemon given the same `--log-file` dies on an unhandled `IOException` (`0xC0000409`) before the lock can refuse it, so a supervisor crash-loops it

**Files:** `src/Capacitor.Cli.Daemon/DaemonRunner.cs`; test in `test/Capacitor.Cli.Tests.Integration/` (it runs the real binaries, and only the Windows leg shows the sharing violation)

- [ ] **Step 1: Write the failing test.** Start the published daemon with `--name dup --log-file <tmp>/d.log` and wait for its lock (the `<name>.version` marker); start a second with the same arguments. Assert the second exits `2` within 10 s and its stderr contains `Another kcap-daemon is already running`; with `KCAP_DAEMON_SUPERVISED=dup` set, assert exit `0`. Spawn through Helpers' `KcapProcess` conventions and a `TempDaemonPaths`
- [ ] **Step 2: Run it on Windows** and confirm it fails with the `IOException` crash, not a refusal
- [ ] **Step 3: Move the file-logger registration after the lock.** Keep the `ClearProviders`/`SetMinimumLevel` calls where they are; move only the `if (logFile is not null) AddProvider(...) else AddSimpleConsole(...)` block to directly after `StartupPhase(time, "lock acquired")`. The host is built later, so no logging is lost; startup phases before the lock already go to stderr
- [ ] **Step 4: Check nothing between the two points logs through an `ILogger`** (read the moved range; the host and its loggers do not exist yet there) and run the test on Windows and one Unix leg
- [ ] **Step 5: Commit** — `Refuse a duplicate daemon before opening its log file`

---

## Part B — the app offers to hand an unsupervised daemon to the supervisor

### Task 2: connected, no supervisor, live daemon ⇒ offer takeover

The ensure matrix only runs on `Unreachable` (`DaemonLifecycleController.cs:248-253`), and `Reconcile`'s coexistence warning requires `UnitPresent` (`:708`). A daemon started with `kcap daemon start` is therefore accepted silently. The onboarding `DaemonEnabler` already classifies this row as `ManualIdentityMatch` with a `Takeover` affordance that runs `MutationVerb.Replace`; reuse that wording and verb

**Files:** `src/Capacitor.App/Services/DaemonLifecycleController.cs`; tests in `test/Capacitor.App.Tests.Unit/` beside the existing lifecycle controller tests

- [ ] **Step 1: Failing tests** for `Reconcile` with `attached = true`: (a) state `NotInstalled`, `UnitPresent = false`, `DaemonPid = 1234` raises an attention surface with a takeover action; (b) the same with `UnitPresent = true` keeps today's coexistence message; (c) state `Running` raises nothing
- [ ] **Step 2: Implement** — a new attention kind carrying the takeover action; the action runs `RunLaneMutationAsync(MutationVerb.Replace, …)` through the existing lane so it shares the evidence predicate
- [ ] **Step 3: Smoke-check in Windows Sandbox** with the scenario from the spec (standalone `daemon start -d`, then open the app): the attention row appears and its action leaves a `kcap-daemon-<name>` task running the daemon
- [ ] **Step 4: Commit** — `Offer to supervise a daemon running outside the service`

---

## Part C — `kcap-service.exe`

The spike proved every mechanism here; port it, put each Win32 call behind a seam, and fix what the spike found. The project holds only BCL references — no `Capacitor.Cli.Core`, no `InternalsVisibleTo` to anything but its test assembly — so the code that runs as SYSTEM stays small enough to audit

### Task 3: scaffold the projects

**Files:** `src/Capacitor.Cli.Service/Capacitor.Cli.Service.csproj`, `test/Capacitor.Cli.Service.Tests.Unit/Capacitor.Cli.Service.Tests.Unit.csproj`, `Capacitor.slnx`, `Directory.Packages.props`

- [ ] **Step 1:** `net10.0-windows`, `AssemblyName` `kcap-service`, `RootNamespace` `Capacitor.Cli.Service`, `PublishAot`, `InvariantGlobalization`, `AllowUnsafeBlocks`; repo analyzers and `TreatWarningsAsErrors` stay on. Add `System.ServiceProcess.ServiceController` 10.0.x to central package versions
- [ ] **Step 2:** Test project referencing the service project and `Capacitor.Tests.Helpers`, with the one `InternalsVisibleTo` grant from the service project to it
- [ ] **Step 3:** Add both to `Capacitor.slnx`; confirm `dotnet build Capacitor.slnx -m:1` and that the test project runs (an empty placeholder test is not committed — the first real test lands in Task 5)

### Task 4: Win32 seams and bindings

**Files:** `src/Capacitor.Cli.Service/Native/` (`Native.cs` from the spike plus `CreateServiceW`, `OpenSCManagerW`, `OpenServiceW`, `ChangeServiceConfig2W`, `DeleteService`, `ControlService`, `GetTokenInformation(TokenElevation)`, `ImpersonateLoggedOnUser`, `RevertToSelf`, `MoveFileExW`), `src/Capacitor.Cli.Service/Sessions/` interfaces

- [ ] **Step 1:** Interfaces the supervisor depends on, one per file: `ISessionSource` (active session ids; open a `UserSession` for an id), `IDaemonLauncher` (launch into a session with an environment, a job and a stderr handle; returns a handle-owning `LaunchedDaemon`), `IProcessWaiter` (wait for exit, exit code), `IJobRegistry` (create/open the per-SID job, terminate)
- [ ] **Step 2:** Win32 implementations ported from `spike/WindowsService/`, with the spike's two fixes kept: the daemon receives a `JOB_OBJECT_QUERY` duplicate of its job handle (job names die with their last handle), and adoption by binary path and session works when the job cannot be opened
- [ ] **Step 3:** The launcher passes an inheritable stderr handle (`STARTF_USESTDHANDLES`, handle opened on `daemon-<name>.stderr.log` while impersonating the user, append) so the daemon's `[startup]` lines survive, and still passes `--stderr-file`
- [ ] **Step 4:** Publish AOT and gate: `dotnet publish src/Capacitor.Cli.Service -c Release -r win-x64 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing

### Task 5: the supervisor state machine

**Files:** `src/Capacitor.Cli.Service/Supervision/Supervisor.cs` and one file per state type; `test/Capacitor.Cli.Service.Tests.Unit/Supervision/SupervisorTests.cs`

Contract (from the spike, now under test): one daemon per enrolled SID with a session; a non-zero exit relaunches after 5 s; exit 0 is a deliberate stop; logoff stops relaunching; unlock and relaunch respect a deliberate stop, logon and an explicit start override it; a service stop detaches and leaves daemons running; a start adopts before it launches

- [ ] **Step 1: Failing tests** with fakes for the four seams and a `FakeTimeProvider`: crash → relaunch at 5 s and not before; exit 0 → no relaunch; logoff then exit 1 → no relaunch; deliberate stop then unlock → stays stopped; deliberate stop then logon → starts; detach then exit → nothing; start with a matching process and job → adopted, not launched; start with a matching process and no job → adopted "no job", stop falls back to terminating the process; start with no enrollment → nothing
- [ ] **Step 2: Implement**, porting `spike/WindowsService/Supervisor.cs` onto the seams and `TimeProvider`
- [ ] **Step 3: Graceful stop.** `Stop(sid)` first launches, as the user, the user's own `kcap.exe daemon stop --name <name> --direct` (the `kcap.exe` beside the enrolled daemon; Task 14 adds the flag) and waits up to 15 s for the daemon to exit 0; only then terminates the job. Parsing the daemon's reply stays in a user process, never in SYSTEM. Test both paths
- [ ] **Step 4:** Delete `spike/WindowsService/`
- [ ] **Step 5: Commit** — `Add the Windows daemon supervisor service`

### Task 6: enrollment and the machine-side index

**Files:** `src/Capacitor.Cli.Service/Enrollment/` ; tests

- [ ] **Step 1:** `HKCU\Software\Kurrent\kcap\Daemon`: `Name`, `Profile`, `MaxAgents`, `DaemonPath`. Read from `HKEY_USERS\<SID>` while impersonating the session's token. `DaemonPath` must be absolute, end in `\kcap-daemon.exe`, and be openable while impersonating; anything else is "not enrolled" with a logged reason
- [ ] **Step 2:** Machine index `HKLM\SOFTWARE\Kurrent\kcap\Service\Users\<SID>` written by the service on `enroll` and removed on `unenroll`; `PruneDeparted` drops a SID whose `ProfileList` entry is gone
- [ ] **Step 3:** Tests over a registry seam: tampered `DaemonPath` (relative, wrong file name, UNC) is refused; index add/remove/prune; empty index after prune reports "last user gone"

### Task 7: the control pipe

**Files:** `src/Capacitor.Cli.Service/Control/` ; tests

- [ ] **Step 1:** `\\.\pipe\kcap-service`, DACL SYSTEM and Administrators full, Authenticated Users read/write, `PipeOptions.CurrentUserOnly` off. One JSON request line, one JSON response line, source-generated `JsonSerializerContext`
- [ ] **Step 2:** Verbs: `capabilities` (service version, protocol version, verb list), `status`, `start`, `stop`, `enroll` (the client has written HKCU first; the service validates and indexes it), `unenroll`. The caller's SID comes from `RunAsClient` + `WindowsIdentity.GetCurrent()`, never from the body. Capabilities are assembled beside the dispatch switch so nothing is advertised without a handler, as local control IPC already does
- [ ] **Step 3:** Pure tests for parse/dispatch/serialize with a fake identity: a request naming another SID acts on the caller's; unknown verb → error response, connection kept
- [ ] **Step 4:** After `unenroll` leaves the index empty, the service schedules its own removal (Task 9's `SelfRemove`)

### Task 8: the SCM host

**Files:** `src/Capacitor.Cli.Service/Program.cs`, `Hosting/KcapService.cs`

- [ ] **Step 1:** `ServiceBase` subclass: `CanHandleSessionChangeEvent`, `CanShutdown`; `OnStart` starts the pipe and runs the initial session scan off the SCM thread; `OnSessionChange` → logon/logoff/unlock; `OnStop`/`OnShutdown` → stop the pipe, detach the supervisor
- [ ] **Step 2:** Log to `%ProgramData%\kcap\service\service.log`, a directory created with a protected DACL (SYSTEM and Administrators full, Users read). Rotate at 5 MB, keep one
- [ ] **Step 3:** `Program.cs` routes `service` (default, SCM) and the installer verbs of Task 9; a console run without SCM prints usage and exits 1

### Task 9: the elevated installer verbs

**Files:** `src/Capacitor.Cli.Service/Installation/` ; tests for the planning logic

`kcap-service.exe install` and `uninstall` run only elevated (`TokenElevation`); not elevated → exit 5 with a message. They never take a path argument: the source is always the running executable

- [ ] **Step 1: `install`.** Target `%ProgramFiles%\kcap\service\`. Create it, then set a protected DACL explicitly (SYSTEM, Administrators full; Users read-execute) rather than trusting inheritance. Copy the running `kcap-service.exe` to `kcap-service.new.exe` in the target, then: service absent → `MoveFileEx` into place, `CreateServiceW` (`LocalSystem`, `SERVICE_AUTO_START`, delayed via `SERVICE_CONFIG_DELAYED_AUTO_START_INFO`, failure actions restart 5 s / 5 s / 30 s with a one-day reset, description), start. Service present and older → stop it (daemons keep running and are adopted), replace, start. Same version → start if stopped, exit 0
- [ ] **Step 2: `uninstall`.** Stop the service, ask it nothing (it may be broken), `DeleteService`, remove the directory, `MoveFileEx(DELAY_UNTIL_REBOOT)` for anything still locked. Daemons are left running as the users' own processes; the next sign-in finds no service
- [ ] **Step 3: `SelfRemove`** (from Task 7, running as the service): mark the service for deletion, schedule the directory for removal at reboot, stop
- [ ] **Step 4:** Tests for the pure planner (current state → ordered steps) and the DACL builder; the Win32 calls stay behind the Task 4 seams

### Task 10: build, gate and bundle

**Files:** `.github/workflows/cli-build.yml`, `scripts/package-cli.sh`, `.github/workflows/desktop.yml`, `scripts/install-windows-setup.sh`, `scripts/install-windows-setup.test.sh`

- [ ] **Step 1:** `cli-build.yml`: publish `src/Capacitor.Cli.Service` for `win-*` RIDs into `publish/service/`, tee its log, add it to the AOT warning gate
- [ ] **Step 2:** `package-cli.sh`: on `win-*`, copy `publish/service/kcap-service.exe` into the release archive's `bin/`; not into the npm package (npm installs keep the task)
- [ ] **Step 3:** `desktop.yml` Windows assemble step: copy `trio/bin/kcap-service.exe` into `publish/app/` beside `kcap.exe`
- [ ] **Step 4:** `install-windows-setup.sh` and its test expect `current/kcap-service.exe`
- [ ] **Step 5: Commit** — `Ship kcap-service.exe in the Windows bundle`

---

## Part D — the CLI learns the service

### Task 11: `ServicePlatform.WindowsService`

**Files:** `src/Capacitor.Cli/Services/IServiceManager.cs`, `ServiceManagerFactory.cs`, `WindowsServiceManager.cs`, `WindowsServicePipeClient.cs`; tests in `test/Capacitor.Cli.Tests.Unit/Services/`

- [ ] **Step 1:** `ForCurrentOs` on Windows picks `WindowsService` when the `kcap-service` service exists and `capabilities` answers within 2 s; otherwise `WindowsScheduledTask`. Both behind a seam so the factory is unit-tested without a service
- [ ] **Step 2:** `WindowsServiceManager` maps `Install` → write HKCU enrollment + `enroll` (+ `start` when `startNow`), `Uninstall` → `stop` + `unenroll` + delete HKCU, `Start`/`Stop`/`Status`/`Query` → the pipe. `Query`'s `JobPid` is the service's daemon pid, so `kcap daemon status` and the app's ownership check keep working. `Describe()` → `"Windows service (kcap-service)"`
- [ ] **Step 3:** Tests with a fake pipe client: state mapping for every service status, a pipe that does not answer → `LabelProbe.Unknown` (never `NotInstalled`), install writes enrollment before calling `enroll`

### Task 12: `install --system` and `uninstall --system`

**Files:** `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs`, `src/Capacitor.Cli.Core/Resources/help-daemon.txt`, `README.md`

- [ ] **Step 1:** `kcap daemon service install --system` (Windows only): find `kcap-service.exe` beside the running `kcap.exe`; run it with `install` through `ShellExecuteEx` verb `runas` and wait. UAC declined (`ERROR_CANCELLED`, 1223) → new exit code `ElevationDeclined` and, with `--json`, `outcome: "declined"`, `reason: "elevation_declined"`. Success → continue as a normal install on the `WindowsService` platform
- [ ] **Step 2:** Same flag on a machine whose service is older than the bundled binary performs the upgrade; `kcap daemon service uninstall --system` runs `kcap-service.exe uninstall` elevated
- [ ] **Step 3:** Help text and README (`## CLI commands` daemon section and the Windows note in `## Getting started`) in the same commit, per `CLAUDE.md`
- [ ] **Step 4:** Tests for argument handling and the exit-code mapping, with the elevation launcher behind a seam

### Task 13: migration from the Scheduled Task

**Files:** `WindowsServiceManager.cs`, `DaemonServiceCommands.cs`; tests

- [ ] **Step 1:** Installing on `WindowsService` when a `kcap-daemon-<name>` task exists: enroll and start through the service, wait for the service's daemon to serve (the `--verify` hello the task path already uses), then uninstall the task through `WindowsScheduledTaskServiceManager`. Any failure before the task removal unenrolls and leaves the task as it was; a failure removing the task is reported and retried by the next install
- [ ] **Step 2:** The task's daemon and the service's would share a name: stop the task's daemon (`schtasks /End` + pid-owned kill, as `Uninstall` does) immediately before `enroll`, so the service's launch takes the lock
- [ ] **Step 3:** Tests over both managers' fakes for the success path and each failure point

### Task 14: `kcap daemon stop --direct`

**Files:** `src/Capacitor.Cli/Commands/DaemonCommands.cs`; tests

- [ ] **Step 1:** Hidden flag: `StopByNameAsync` skips the "managed by …, use daemon service stop" refusal (`DaemonCommands.cs:417-423`) and sends the shutdown request straight to the daemon. Used only by the service's graceful stop; not in help
- [ ] **Step 2:** Test that `--direct` reaches `DaemonShutdownClient` with a managed status, and that without it the refusal still prints

### Task 15: status JSON

**Files:** `src/Capacitor.Cli/Commands/ServiceStatusJson.cs`; tests

- [ ] **Step 1:** Add, last and optional: `Supervisor` (`"windows_service"`, `"windows_scheduled_task"`, `"launchd"`, `"systemd"`) and `ServiceUpgradeAvailable` (Windows service older than the bundled `kcap-service.exe`). Existing consumers are unaffected
- [ ] **Step 2:** Render tests for both fields

### Task 16: uninstall from Velopack unenrolls

**Files:** `DaemonServiceCommands.cs` (`uninstall --under`)

- [ ] **Step 1:** On `WindowsService`, `uninstall --under <dir>` sends `stop` + `unenroll` over the pipe for every enrollment whose `DaemonPath` is under `<dir>`, then removes HKCU enrollment. No elevation; Velopack's 30 s hook budget is respected by a 10 s pipe deadline
- [ ] **Step 2:** Test with the fake pipe client
- [ ] **Step 3: Commit** — `Supervise the Windows daemon through kcap-service`

---

## Part E — the desktop app

### Task 17: `KcapCli` and the snapshot

**Files:** `src/Capacitor.App/Services/KcapCli.cs`, `ServiceSnapshot` and its parser; tests

- [ ] **Step 1:** `ServiceInstallSystemAsync(ct)` runs `daemon service install --system --name … --profile … --verify --json`; the result distinguishes success, `declined` and failure
- [ ] **Step 2:** Parse `supervisor` and `service_upgrade_available`; missing fields read as unknown (an older CLI)

### Task 18: install prompt, fallback and the attention row

**Files:** `DaemonEnabler.cs`, `DaemonLifecycleController.cs`, Settings daemon section; tests in `test/Capacitor.App.Tests.Unit/`

- [ ] **Step 1:** Onboarding on Windows: the `Install` affordance explains the one-time admin prompt, then calls `ServiceInstallSystemAsync`. Declined → install the Scheduled Task as today and say so; the step is satisfied either way
- [ ] **Step 2:** While `supervisor` is `windows_scheduled_task` and the bundle carries `kcap-service.exe`, show a dismissible attention row "Run kcap as a Windows service" with the same action. No automatic prompt
- [ ] **Step 3:** The Task 2 takeover row prefers the service when it is installed
- [ ] **Step 4:** View-model tests for each row and the declined path; one smoke test that the action opens no app-owned dialog before the CLI call (the UAC prompt is the OS's)

### Task 19: service update prompt

- [ ] **Step 1:** `service_upgrade_available` → attention row "Update the kcap service (needs admin)" running `ServiceInstallSystemAsync`; declining leaves the older service running, which keeps working because the daemon updates with Velopack regardless
- [ ] **Step 2:** Tests for the row and the decline

### Task 20: uninstall

**Files:** `src/Capacitor.App/Platform/Windows/WindowsUninstallCleanup.cs`

- [ ] **Step 1:** No change in the call (`daemon service uninstall --under`), which Task 16 made service-aware; add a test that the call is still made, with its `--under` directory, when the service is the supervisor
- [ ] **Step 2: Commit** — `Install the Windows service from the desktop app`

---

## Part F — docs and verification

### Task 21: invariants and docs

- [ ] **Step 1:** `CLAUDE.md` Invariants: *The Windows service never runs user-controlled code as SYSTEM* — the only SYSTEM code is `kcap-service.exe` in an administrator-only directory, everything it starts runs with a user's token, and it opens user files only while impersonating that user
- [ ] **Step 2:** Mark the spec implemented; fold the spike section into a short "why it is shaped this way" note

### Task 22: end-to-end verification

- [ ] **Step 1: Windows Sandbox** (UAC off): install the Velopack build; onboarding installs the service; the daemon's parent is `kcap-service.exe` and its image is `%LocalAppData%\kcap\current\kcap-daemon.exe`; a hosted Claude Code agent renders in the app; kill the daemon → back in ~5 s; `kcap daemon service stop`/`start`; restart and kill the service → the daemon is adopted; a Velopack update swaps `current` and the service's next launch runs the new daemon; migration from a pre-existing task; uninstall leaves no service, no task, no daemon, and `%ProgramFiles%\kcap\service` gone or pending reboot
- [ ] **Step 2: A VM with UAC on:** the install prompt, declining it (task fallback), the update prompt, `kcap-service.exe install` refusing to run unelevated
- [ ] **Step 3: A VM with two local users:** both enroll, both daemons run in their own sessions under their own profiles, neither sees the other's status over the pipe, sign-out of one leaves the other running, the last uninstall removes the service
- [ ] **Step 4:** Record results on the spec and the Pages write-up
