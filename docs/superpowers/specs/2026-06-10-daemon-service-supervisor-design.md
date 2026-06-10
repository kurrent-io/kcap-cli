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
kcap daemon service install   [--name N] [--max-agents N] [--server-url URL] [--no-start]
kcap daemon service uninstall [--name N]
kcap daemon service start     [--name N]
kcap daemon service stop      [--name N]
kcap daemon service status    [--name N]
```

Per-user / login-session scope on every platform:

| Platform | Mechanism | Unit location |
|---|---|---|
| macOS | launchd **LaunchAgent** | `~/Library/LaunchAgents/io.kurrent.kcap.daemon[.<name>].plist` |
| Linux | systemd **`--user`** unit (templated) | `~/.config/systemd/user/kcap-daemon@.service` |
| Windows | Task Scheduler **logon task** | task `kcap-daemon[-<name>]` |

The existing ad-hoc lifecycle (`kcap daemon start [-d]`, `stop`, `status`,
`logs`, `doctor`) is **unchanged**. Service management is additive.

### Out of scope (explicitly deferred)

- **System-wide / boot scope** (LaunchDaemon, systemd system unit, true Windows
  Service running as SYSTEM). Rejected: the daemon needs the user's `$HOME`,
  resolved kcap profile, login keychain, and an interactive session to spawn
  Claude/Codex PTY agents. A session-0 service breaks PTY agent spawning.
- **systemd linger** (`loginctl enable-linger`) so the unit runs while logged
  out. Per-user/login scope means "stops at logout" is acceptable; a future
  `--linger` flag can add it.
- **Auto-reinstall on `npm update`.** The platform-package binary path is stable
  across updates; `daemon doctor` will flag a unit whose binary path no longer
  exists (see below).

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
    string Name,
    string DaemonBinaryPath,           // absolute path to kcap-daemon, resolved at install
    string LogPath,                    // stable log file passed as --log-file
    IReadOnlyList<string> ExtraArgs);  // optional baked overrides (--max-agents, --server-url)

public enum ServiceState { NotInstalled, Installed, Running }

public interface IServiceManager {
    string  Describe();                     // e.g. "launchd LaunchAgent"
    string  GenerateUnit(ServiceSpec spec); // PURE — the primary tested seam
    ServiceState Status(string name);
    void    Install(ServiceSpec spec, bool startNow);
    void    Uninstall(string name);
    void    Start(string name);
    void    Stop(string name);
}
```

Selection:

```csharp
public static class ServiceManagerFactory {
    public static IServiceManager ForCurrentOs(); // Launchd | Systemd | WindowsScheduledTask
                                                   // throws PlatformNotSupportedException otherwise
}
```

Implementations: `LaunchdServiceManager`, `SystemdServiceManager`,
`WindowsScheduledTaskServiceManager`.

### What the unit runs

The unit execs the **`kcap-daemon` binary directly** — the OS service manager is
the sole supervisor (no `kcap daemon start` CLI layer, no PID-file dance; the
daemon's own `flock` still prevents duplicate names).

The unit **pins only**:

- `--name <name>` — must be deterministic so it matches the `flock` slot.
- `--log-file <path>` — stable log location (`daemon.log`, or
  `daemon-<name>.log` for a named instance), via `PathHelpers.ConfigPath`.

Everything else (`server-url`, `max-agents`, claude/codex paths) keeps resolving
from the active profile + env **at each launch** — `DaemonRunner.RunAsync`
already does `AppConfig.ResolveActiveProfile(args)` and reads these from the
resolved profile. So editing the profile does **not** require a reinstall.
`--max-agents` / `--server-url` on `install` are optional overrides baked into
`ExtraArgs` for users who want them frozen.

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

### Per-verb → platform command mapping

| verb | macOS | Linux | Windows |
|---|---|---|---|
| install | write plist; `launchctl bootstrap gui/<uid> <plist>` (`RunAtLoad` starts it); follow with `launchctl kill SIGTERM` if `--no-start` | write unit; `systemctl --user daemon-reload`; `enable --now kcap-daemon@<name>` (or `enable` only if `--no-start`) | `schtasks /Create /TN <task> /XML <f> /F`; `/Run` unless `--no-start` |
| uninstall | `launchctl bootout gui/<uid>/<label>`; delete plist | `systemctl --user disable --now kcap-daemon@<name>`; delete unit; `daemon-reload` | `schtasks /Delete /TN <task> /F` |
| start | `launchctl kickstart gui/<uid>/<label>` (bootstrap first if not loaded) | `systemctl --user start kcap-daemon@<name>` | `schtasks /Run /TN <task>` |
| stop | `launchctl kill SIGTERM gui/<uid>/<label>` | `systemctl --user stop kcap-daemon@<name>` | `schtasks /End /TN <task>` |
| status | `launchctl print gui/<uid>/<label>` (exit code → state) | `systemctl --user is-active / is-enabled` | `schtasks /Query /TN <task> /FO LIST` |

`<uid>` = `Environment.GetEnvironmentVariable("UID")` fallback `id -u`; `<label>`
= `io.kurrent.kcap.daemon[.<name>]`.

### macOS plist (generated)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>              <string>io.kurrent.kcap.daemon.<name></string>
  <key>ProgramArguments</key>   <array>
    <string>/abs/path/kcap-daemon</string>
    <string>--name</string>     <string><name></string>
    <string>--log-file</string> <string>/Users/<u>/.config/kcap/daemon-<name>.log</string>
  </array>
  <key>RunAtLoad</key>          <true/>
  <key>KeepAlive</key>          <dict><key>SuccessfulExit</key><false/></dict>
  <key>ProcessType</key>        <string>Adaptive</string>
  <key>StandardOutPath</key>    <string>…/daemon-<name>.log</string>
  <key>StandardErrorPath</key>  <string>…/daemon-<name>.log</string>
</dict>
</plist>
```

launchd's default `ThrottleInterval` (10s) is the crash-loop guard.

### Linux systemd templated unit (generated)

```ini
[Unit]
Description=kcap daemon (%i)
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/abs/path/kcap-daemon --name %i --log-file %h/.config/kcap/daemon-%i.log
Restart=on-failure
RestartSec=5
StartLimitIntervalSec=60
StartLimitBurst=5

[Install]
WantedBy=default.target
```

Instance name `%i` = the daemon `--name`. `kcap daemon service install --name X`
operates on `kcap-daemon@X.service`.

### Windows Task Scheduler XML (generated)

Logon trigger for the current user; action runs `kcap-daemon.exe --name … --log-file …`;
settings: `MultipleInstances=IgnoreNew`, `ExecutionTimeLimit=PT0S` (unlimited),
`DisallowStartIfOnBatteries=false`, `StopIfGoingOnBatteries=false`,
`StartWhenAvailable=true`, restart-on-failure (`RestartCount=999`,
`RestartInterval=PT1M`). Registered via `schtasks /Create /XML` (avoids
PowerShell quoting; XML is a pure generated string).

### Integration with existing commands

- **`kcap daemon status`** gains a `service:` line per name — `not installed` /
  `installed (launchd LaunchAgent, runs at login)` / `installed, running` — from
  `IServiceManager.Status`.
- **`kcap daemon stop`** (ad-hoc) becomes service-aware: if a unit exists for the
  name it does **not** raw-kill (which the supervisor would just restart);
  instead it prints guidance to use `kcap daemon service stop --name N`.
- **`kcap daemon doctor`** gains a check: for each installed unit, verify the
  baked `DaemonBinaryPath` still exists; if not, report it and suggest
  `kcap daemon service install` to refresh the path (covers a moved npm prefix).

## Binary path resolution

`install` resolves the absolute `kcap-daemon` path via the existing
`ResolveDaemonBinary()` (sibling of the running `kcap` binary) and writes it into
the unit. The npm platform-package path
(`…/@kurrent/kcap-<rid>/bin/kcap-daemon`) is stable across `npm update`, so units
survive upgrades. A prefix change requires `service install` again — surfaced by
`daemon doctor`, not silently broken.

## Testing

- **Pure generators** (`GenerateUnit`) per OS: TUnit tests assert the output
  contains the absolute binary path, pinned `--name`/`--log-file` args, the
  correct label / `@%i` instance / task name, and the restart keys
  (`KeepAlive`/`Restart=on-failure`/`RestartCount`). Deterministic strings, no OS
  calls.
- **Command-vector builders** (the `launchctl`/`systemctl`/`schtasks` argument
  lists for each verb) are pure helpers → asserted directly.
- **`ServiceManagerFactory.ForCurrentOs()`** returns the right type per
  `OperatingSystem.Is*`, throws `PlatformNotSupportedException` otherwise.
- The thin `Install/Uninstall/Start/Stop` shell-outs are **not** run in CI
  (can't register real units); they are kept trivial (build vector → run via
  existing `ProcessHelpers`).

## AOT

AOT-safe throughout: hand-built unit strings (no XML/JSON reflection
serialization), `Process` shell-out, plain file I/O. Verify with
`dotnet publish -c Release` and the IL3050/IL2026 grep per CLAUDE.md.

## Docs (same PR — CLAUDE.md mandate)

- `src/Capacitor.Cli.Core/Resources/help-daemon.txt` — add the `service`
  subcommand group and its options.
- `README.md` — `## Getting started` gains a "keep the daemon alive" note
  recommending `kcap daemon service install`; the `## CLI commands` daemon
  section documents `service install/uninstall/start/stop/status`.

## Files

New:

- `src/Capacitor.Cli/Services/IServiceManager.cs` (interface, `ServiceSpec`, `ServiceState`)
- `src/Capacitor.Cli/Services/ServiceManagerFactory.cs`
- `src/Capacitor.Cli/Services/LaunchdServiceManager.cs`
- `src/Capacitor.Cli/Services/SystemdServiceManager.cs`
- `src/Capacitor.Cli/Services/WindowsScheduledTaskServiceManager.cs`
- `test/Capacitor.Cli.Tests.Unit/Services/*ServiceManagerTests.cs`

Modified:

- `src/Capacitor.Cli/Commands/DaemonCommands.cs` — `service` subcommand dispatch; `status`/`stop`/`doctor` service-awareness
- `src/Capacitor.Cli.Core/Resources/help-daemon.txt`
- `README.md`
