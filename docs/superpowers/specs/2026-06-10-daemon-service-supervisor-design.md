# Daemon service supervisor (install/uninstall/start/stop)

## Problem

The `kcap` daemon is a long-running process, but nothing supervises it. Users
run it via `kcap daemon start -d` (a one-shot detached spawn) or under tmux, and
neither survives the process dying.

A debugging session on 2026-06-10 traced a recurring "daemon crashes silently"
report to its root cause: the daemon exits with code **137 (SIGKILL)** and the
`death-rattle` instrumentation emits **nothing** — proving the kill comes from
*outside* the .NET runtime and is uncatchable in-process. The evidence:

- Exit 137 = 128 + 9 (SIGKILL).
- Zero `[death-rattle]` lines in 4001 log lines (no managed handler ran).
- The daemon was **idle for 49 minutes** before the kill, so nothing it did
  triggered it.
- The macOS unified log showed a `JETSAM_REASON_MEMORY_IDLE_EXIT` storm at the
  same time (launchd SIGKILLing dozens of idle processes), driven by a
  concurrent heavy `dotnet` build/test run spiking memory pressure.
- It happened under **detached tmux**, which falsified the earlier "Warp block
  recycling" hypothesis — tmux only shields against terminal-driven signals
  (SIGHUP/SIGTERM), not kernel memory reclamation (jetsam) or the Linux OOM
  killer.

**There is no in-process fix** — you cannot survive SIGKILL. The fix is
operational resilience: run the daemon under a real per-user service supervisor
that auto-restarts it after any death (jetsam, OOM, crash, logout/login), on all
three platforms.

## Scope

Add a `service` command group under `kcap daemon` that registers, deregisters,
and controls the daemon as a **per-user** OS service:

```
kcap daemon service install   [--name N] [--profile P] [--max-agents N] [--no-start]
kcap daemon service uninstall [--name N]
kcap daemon service start     [--name N]
kcap daemon service stop      [--name N]
kcap daemon service status    [--name N]
```

Per-user / login-session scope on every platform, **one unit per service id**
(no shared template):

| Platform | Mechanism | Unit location |
|---|---|---|
| macOS | launchd **LaunchAgent** | `~/Library/LaunchAgents/io.kurrent.kcap.daemon.<id>.plist` |
| Linux | systemd **`--user`** unit (one file per id) | `~/.config/systemd/user/kcap-daemon-<id>.service` |
| Windows | Task Scheduler **logon task** | task `kcap-daemon-<id>` |

`<id>` is the **sanitized** service id (see *Name handling*). The existing
ad-hoc lifecycle (`kcap daemon start [-d]`, `stop`, `status`, `logs`, `doctor`)
is **unchanged**. Service management is additive.

### Out of scope (explicitly deferred)

- **System-wide / boot scope** (LaunchDaemon, systemd system unit, true Windows
  Service running as SYSTEM). Rejected: the daemon needs the user's `$HOME`,
  resolved kcap profile, login keychain, and an interactive session to spawn
  Claude/Codex PTY agents. A session-0 service breaks PTY agent spawning.
- **systemd linger** (`loginctl enable-linger`) so the unit runs while logged
  out. Per-user/login scope means "stops at logout" is acceptable; a future
  `--linger` flag can add it.
- **Auto-reinstall on `npm update`.** The platform-package binary path is stable
  across updates; `daemon doctor` flags a unit whose binary path no longer
  exists (see below), and `service install` is idempotent so re-running it fixes
  a moved path.

## Name handling, service id, and escaping

`DaemonNameResolver.Resolve` returns the raw `--name` verbatim — only
`DaemonLockPaths.Sanitize` (filenames) restricts characters. A daemon name may
therefore contain spaces or XML/shell metacharacters, which would otherwise be
interpolated unescaped into plist / unit / Task XML and into the label / instance
/ task id.

Rules:

1. **Service id** = `DaemonLockPaths.Sanitize(resolvedName)` — lowercase,
   `[a-z0-9._-]` only, idempotent. This single id is used for the unit filename,
   launchd `Label`, systemd unit name, Windows task name, **and the `--name`
   value passed to the daemon**. Passing the sanitized id as `--name` keeps the
   service id, the daemon's `flock` slot (`Sanitize` is applied again, no-op),
   and what `kcap daemon status/stop` enumerate all consistent. `install` prints
   the sanitized id when it differs from the input so the user isn't surprised.
2. **Every interpolated value** (binary path, log path, env values, the id) is
   escaped for its target format when generating unit text: XML-escape for plist
   and Task XML; systemd value-escaping for `.service` lines. Generators never
   concatenate raw strings into markup.

This keeps the pure generators total (any input produces well-formed output) and
is the focus of the escaping unit tests.

## Architecture

### `IServiceManager` seam (in `Capacitor.Cli`)

Lives in `Capacitor.Cli` (not `Capacitor.Cli.Core`): its only consumer is
`DaemonCommands` (Cli), and the side-effecting apply uses `ProcessHelpers`
(Cli) — Core must not depend on Cli. `Capacitor.Cli.Tests.Unit` already
references the Cli project and tests Cli-project classes, so this is unit-testable.
Mirrors the existing interface seams in the codebase (`IPtyProcessFactory`,
`IHostedAgentLauncher`). Each implementation splits a **pure unit-text
generator** (testable) from a **thin side-effecting apply** (shells out to the
platform tool).

```csharp
public record ServiceSpec(
    string ServiceId,                              // sanitized id: filename/label/instance/task AND --name
    string DaemonBinaryPath,                       // absolute path to kcap-daemon, resolved at install
    string LogPath,                                // stable log file passed as --log-file
    IReadOnlyDictionary<string,string> Environment,// captured env baked into the unit (see Environment)
    IReadOnlyList<string> ExtraArgs);              // optional --max-agents override (NOT --server-url)

public enum ServiceState { NotInstalled, Installed, Running }

public record ServiceStatus(ServiceState State, string? BinaryPath);

public interface IServiceManager {
    string  Describe();                       // e.g. "launchd LaunchAgent"
    string  GenerateUnit(ServiceSpec spec);   // PURE — the primary tested seam

    IReadOnlyList<string> ListInstalled();    // service ids with a unit file on disk
    ServiceStatus Status(string serviceId);   // state + baked binary path (for doctor)

    void Install(ServiceSpec spec, bool startNow); // idempotent: replaces an existing unit
    void Uninstall(string serviceId);
    void Start(string serviceId);
    void Stop(string serviceId);
}
```

`ListInstalled()` + `Status().BinaryPath` are what let `daemon status` show an
**installed-but-stopped** service (which has no PID file) and let `daemon doctor`
iterate installed units and validate the baked binary path.

Selection (testable on any host):

```csharp
public enum ServicePlatform { Launchd, Systemd, WindowsScheduledTask }

public static class ServiceManagerFactory {
    public static IServiceManager ForPlatform(ServicePlatform p); // construct any manager
    public static IServiceManager ForCurrentOs();                 // detect via OperatingSystem.Is*,
                                                                  // delegate to ForPlatform;
                                                                  // throws PlatformNotSupportedException
}
```

`ForPlatform` is the seam that lets unit tests exercise all three generators on a
single CI OS.

Implementations: `LaunchdServiceManager`, `SystemdServiceManager`,
`WindowsScheduledTaskServiceManager`.

### What the unit runs

The unit execs the **`kcap-daemon` binary directly** — the OS service manager is
the sole supervisor (no `kcap daemon start` CLI layer, no PID-file dance; the
daemon's own `flock` still prevents duplicate names).

The unit **pins**:

- `--name <id>` — the sanitized service id (deterministic; matches the `flock`
  slot and the service id).
- `--log-file <path>` — stable log location (`daemon.log`, or `daemon-<id>.log`
  for a named instance), via `PathHelpers.ConfigPath`.
- optional `--max-agents N` (from `ExtraArgs`) when the user passes an explicit
  override.

Everything else resolves at each launch from the **pinned profile + captured
env** (below), so editing the profile does not require a reinstall.

### Profile + environment (why not `--server-url`)

Two confirmed interactions in the current code make naïve baking wrong:

1. **`--server-url` (and `KCAP_URL`) disable profile resolution.**
   `ProfileResolver.Resolve` returns `Profile = null` whenever a CLI server-url
   or `KCAP_URL` is set, so `DaemonRunner` then skips the profile's
   `ClaudePath` / `CodexPath` / `MaxAgents`. Baking `--server-url` would silently
   strip the daemon's agent-path config.
2. **Supervised jobs don't inherit the interactive shell `PATH`.**
   `CliResolver.Exists` walks `PATH` for bare `claude` / `codex`. launchd agents
   and systemd `--user` units start with a minimal `PATH`, so vendor discovery
   finds nothing and the daemon advertises no spawnable vendors.

So instead of `--server-url`, `install`:

- **Pins the profile by name** via `KCAP_PROFILE=<resolved profile name>` in the
  unit environment. The daemon (which has no working dir, so repo/remote
  resolution doesn't apply) then deterministically resolves the **full** profile
  — server URL *and* `claude_path` / `codex_path` / `max_agents`. `--profile P`
  overrides which profile is pinned; default is the currently-resolved profile
  name. If no named profile resolves (server-url-only setup), fall back to baking
  `KCAP_URL` and require absolute agent paths (next point).
- **Captures environment** from the installing shell into the unit:
  `PATH` (so bare `claude`/`codex` resolve exactly as they do in the terminal),
  plus any set `KCAP_CONFIG_DIR`, `KCAP_PROFILE`, `KCAP_CLAUDE_PATH`,
  `KCAP_CODEX_PATH`. Mechanism per platform: launchd `EnvironmentVariables` dict,
  systemd `Environment=` lines, Windows tasks inherit the user env block but get
  the same keys set for parity/determinism.
- **`daemon doctor`** additionally warns when neither an absolute agent path nor
  a `PATH` entry containing `claude`/`codex` is present in a unit's captured env.

### Restart semantics (the subtle part)

Restart must fire on **crash / SIGKILL** (jetsam, OOM) but **not** on a clean
stop or clean exit. All three managers distinguish a *failure* from a deliberate
*stop*:

| Platform | Restart-on-failure config | Clean stop (no restart) |
|---|---|---|
| launchd | `KeepAlive = { SuccessfulExit = false }` | `launchctl kill SIGTERM` → daemon cooperatively exits 0 → not restarted |
| systemd | `Restart=on-failure`, `RestartSec=5` | `systemctl --user stop` → systemd knows it's a stop, not a failure |
| schtasks | task settings `RestartCount` + `RestartInterval=PT1M` | `schtasks /End` → terminated, not a failure exit |

A SIGKILL is an abnormal exit on all three → it **does** restart (the jetsam/OOM
case we are fixing). A `service stop` keeps the unit installed but down; it
returns at next login (launchd `RunAtLoad`, systemd still `enable`d) or via
`service start`. Only `service uninstall` deregisters.

### Install idempotency / reinstall

`install` is **idempotent**: running it when a unit already exists replaces the
unit and ends in the running state (unless `--no-start`), regardless of the prior
running/stopped state. This is what makes `doctor`'s "re-run `service install` to
refresh a moved binary path" well-defined.

| Platform | Replace-existing behavior |
|---|---|
| launchd | `bootout` the old label (ignore "not loaded"), rewrite plist, `bootstrap`, then start unless `--no-start` |
| systemd | rewrite unit, `daemon-reload`, `enable`, then `restart` (or `stop` if `--no-start`) |
| Windows | `schtasks /Create … /F` overwrites in place, then `/Run` unless `--no-start` |

### Per-verb → platform command mapping

`<uid>` = `id -u`; `<label>` = `io.kurrent.kcap.daemon.<id>`; `<unit>` =
`kcap-daemon-<id>.service`; `<task>` = `kcap-daemon-<id>`.

| verb | macOS | Linux | Windows |
|---|---|---|---|
| install | write plist; `launchctl bootout` (if present); `launchctl bootstrap gui/<uid> <plist>` (`RunAtLoad` starts it); `launchctl kill SIGTERM` if `--no-start` | write unit; `systemctl --user daemon-reload`; `enable <unit>`; `restart` (or leave stopped if `--no-start`) | `schtasks /Create /TN <task> /XML <f> /F`; `/Run` unless `--no-start` |
| uninstall | `launchctl bootout gui/<uid>/<label>`; delete plist | `systemctl --user disable --now <unit>`; delete unit; `daemon-reload` | `schtasks /Delete /TN <task> /F` |
| start | `launchctl kickstart gui/<uid>/<label>` (bootstrap first if not loaded) | `systemctl --user start <unit>` | `schtasks /Run /TN <task>` |
| stop | `launchctl kill SIGTERM gui/<uid>/<label>` | `systemctl --user stop <unit>` | `schtasks /End /TN <task>` |
| status | `launchctl print gui/<uid>/<label>` (exit code → state) | `systemctl --user is-active / is-enabled <unit>` | `schtasks /Query /TN <task> /FO LIST` |
| list-installed | enumerate `~/Library/LaunchAgents/io.kurrent.kcap.daemon.*.plist` | enumerate `~/.config/systemd/user/kcap-daemon-*.service` | `schtasks /Query` filtered to `kcap-daemon-*` |

> **Windows `/End` vs restart-on-failure:** `/End` reports task result
> `0x41306` (terminated), which Task Scheduler does **not** treat as a failure
> exit, so the restart-on-failure setting should not relaunch it. This is the
> one platform behavior to **verify on a real Windows host** during
> implementation; if `/End` does trigger a relaunch, fall back to disabling the
> task (`schtasks /Change /DISABLE`) for `stop` and re-enabling for `start`.

### macOS plist (generated — values XML-escaped)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>              <string>io.kurrent.kcap.daemon.<id></string>
  <key>ProgramArguments</key>   <array>
    <string>/abs/path/kcap-daemon</string>
    <string>--name</string>     <string><id></string>
    <string>--log-file</string> <string>/Users/<u>/.config/kcap/daemon-<id>.log</string>
  </array>
  <key>EnvironmentVariables</key><dict>
    <key>PATH</key>             <string><captured PATH></string>
    <key>KCAP_PROFILE</key>     <string><pinned profile></string>
    <!-- plus any captured KCAP_CONFIG_DIR / KCAP_CLAUDE_PATH / KCAP_CODEX_PATH -->
  </dict>
  <key>RunAtLoad</key>          <true/>
  <key>KeepAlive</key>          <dict><key>SuccessfulExit</key><false/></dict>
  <key>ProcessType</key>        <string>Adaptive</string>
  <key>StandardOutPath</key>    <string>…/daemon-<id>.log</string>
  <key>StandardErrorPath</key>  <string>…/daemon-<id>.log</string>
</dict>
</plist>
```

launchd's default `ThrottleInterval` (10s) is the crash-loop guard.

### Linux systemd per-instance unit (generated — one file per id)

```ini
[Unit]
Description=kcap daemon (<id>)
After=network-online.target
Wants=network-online.target

[Service]
Environment=PATH=<captured PATH>
Environment=KCAP_PROFILE=<pinned profile>
# plus any captured KCAP_CONFIG_DIR / KCAP_CLAUDE_PATH / KCAP_CODEX_PATH
ExecStart=/abs/path/kcap-daemon --name <id> --log-file %h/.config/kcap/daemon-<id>.log
Restart=on-failure
RestartSec=5
StartLimitIntervalSec=60
StartLimitBurst=5

[Install]
WantedBy=default.target
```

One concrete file per id (`kcap-daemon-<id>.service`), **not** a shared
`@.service` template — so per-instance baked args/env are expressible and
uninstalling one id deletes only its file without touching other instances.

### Windows Task Scheduler XML (generated — values XML-escaped)

Logon trigger for the current user; action runs `kcap-daemon.exe --name <id> --log-file …`;
settings: `MultipleInstances=IgnoreNew`, `ExecutionTimeLimit=PT0S` (unlimited),
`DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`,
`StartWhenAvailable=true`, restart-on-failure (`RestartCount=999`,
`RestartInterval=PT1M`). Registered via `schtasks /Create /XML` (avoids
PowerShell quoting; XML is a pure generated string).

### Integration with existing commands

- **`kcap daemon status`** unions the running-PID-file names with
  `IServiceManager.ListInstalled()`, so an installed-but-stopped service appears.
  Each line gains a `service:` field — `not installed` /
  `installed (launchd LaunchAgent, runs at login)` / `installed, running`.
- **`kcap daemon stop`** (ad-hoc) becomes service-aware: if a unit exists for the
  id it does **not** raw-kill (which the supervisor would just restart); instead
  it prints guidance to use `kcap daemon service stop --name N`.
- **`kcap daemon doctor`** iterates `ListInstalled()`; for each unit it reads the
  baked `Status().BinaryPath` and reports if it no longer exists, suggesting
  `kcap daemon service install` (idempotent) to refresh it. Also warns on the
  missing-agent-path env condition above.

## Binary path resolution

`install` resolves the absolute `kcap-daemon` path via the existing
`ResolveDaemonBinary()` (sibling of the running `kcap` binary) and writes it into
the unit. The npm platform-package path
(`…/@kurrent/kcap-<rid>/bin/kcap-daemon`) is stable across `npm update`, so units
survive upgrades. A prefix change is fixed by re-running `service install`
(idempotent) — surfaced by `daemon doctor`, not silently broken.

## Testing

- **Pure generators** (`GenerateUnit`) per platform via `ForPlatform`: TUnit
  tests assert the output contains the absolute binary path, pinned `--name`/
  `--log-file`, the baked `PATH`/`KCAP_PROFILE` env, the correct label / unit
  name / task id, and the restart keys (`KeepAlive` / `Restart=on-failure` /
  `RestartCount`).
- **Escaping tests:** ids and paths containing XML/shell metacharacters and
  spaces produce well-formed plist / `.service` / Task XML (parse the plist and
  Task XML with `XDocument` in the test to assert validity).
- **Sanitization:** `Sanitize` mapping of messy names → ids, and idempotency.
- **Command-vector builders** (the `launchctl`/`systemctl`/`schtasks` argument
  lists for each verb) are pure helpers → asserted directly, including the
  replace-existing (idempotent install) vectors.
- **`ServiceManagerFactory.ForPlatform`** constructs each manager on any host;
  `ForCurrentOs()` maps `OperatingSystem.Is*` correctly and throws otherwise.
- The thin side-effecting `Install/Uninstall/Start/Stop/ListInstalled` shell-outs
  are **not** run in CI (can't register real units); they are kept trivial (build
  vector → run via existing `ProcessHelpers`).

## AOT

AOT-safe throughout: hand-built unit strings (no XML/JSON reflection
serialization), `Process` shell-out, plain file I/O. (`XDocument` is used only in
tests for validation, not at runtime.) Verify with `dotnet publish -c Release`
and the IL3050/IL2026 grep per CLAUDE.md.

## Docs (same PR — CLAUDE.md mandate)

- `src/Capacitor.Cli.Core/Resources/help-daemon.txt` — add the `service`
  subcommand group and its options (`--name`, `--profile`, `--max-agents`,
  `--no-start`).
- `README.md` — `## Getting started` gains a "keep the daemon alive" note
  recommending `kcap daemon service install`; the `## CLI commands` daemon
  section documents `service install/uninstall/start/stop/status` and the
  profile/env-capture behavior.

## Files

New:

- `src/Capacitor.Cli/Services/IServiceManager.cs` (interface, `ServiceSpec`, `ServiceState`, `ServiceStatus`)
- `src/Capacitor.Cli/Services/ServiceManagerFactory.cs` (`ServicePlatform`, `ForPlatform`, `ForCurrentOs`)
- `src/Capacitor.Cli/Services/LaunchdServiceManager.cs`
- `src/Capacitor.Cli/Services/SystemdServiceManager.cs`
- `src/Capacitor.Cli/Services/WindowsScheduledTaskServiceManager.cs`
- `src/Capacitor.Cli/Services/ServiceEnvironment.cs` (captures PATH + KCAP_* and resolves the pinned profile name)
- `test/Capacitor.Cli.Tests.Unit/Services/*ServiceManagerTests.cs`

Modified:

- `src/Capacitor.Cli/Commands/DaemonCommands.cs` — `service` subcommand dispatch; `status`/`stop`/`doctor` service-awareness (incl. `ListInstalled` union)
- `src/Capacitor.Cli.Core/Resources/help-daemon.txt`
- `README.md`
