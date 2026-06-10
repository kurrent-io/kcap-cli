using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

public static class DaemonCommands {
    static readonly string LogPath = PathHelpers.ConfigPath("daemon.log");

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        var subcommand = args[1];
        var remaining  = args[2..];

        return subcommand switch {
            "start"   => await StartAsync(remaining),
            "stop"    => await StopAsync(remaining),
            "status"  => await Status(remaining),
            "logs"    => await Logs(),
            "doctor"  => await DoctorAsync(remaining),
            "service" => await ServiceAsync(remaining),
            _         => PrintUsage()
        };
    }

    static string ResolveName(string[] args) =>
        DaemonNameResolver.Resolve(args, AppConfig.ResolvedProfile?.Profile?.Daemon?.Name);

    static async Task<int> StartAsync(string[] args) {
        var detached = args.Contains("-d") || args.Contains("--detach");

        return detached ? StartDetached(args) : await StartForegroundAsync(args);
    }

    /// <summary>
    /// Open the per-name <c>&lt;name&gt;.start</c> lock with
    /// <c>FileShare.None</c> for the CLI-side critical section that wraps
    /// the PID-file stale-check + daemon spawn. The daemon itself takes a
    /// separate <c>&lt;name&gt;.lock</c> via <see cref="DaemonLock"/>; this
    /// lock just keeps two concurrent <c>kcap daemon start --name X</c>
    /// invocations from both observing a stale PID file and both spawning.
    /// </summary>
    static FileStream? TryAcquireStartLock(string daemonName) {
        try {
            DaemonLockPaths.EnsureDirectory();

            return new(
                DaemonLockPaths.StartLockPath(daemonName),
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.None
            );
        } catch (IOException) {
            return null;
        }
    }

    static async Task<int> StartForegroundAsync(string[] args) {
        var name = ResolveName(args);

        var startLock = TryAcquireStartLock(name);

        if (startLock is null) {
            await Console.Error.WriteLineAsync(
                $"Another `kcap daemon start --name {name}` is already in progress or holds the daemon lock. "
              + $"Run `kcap daemon stop --name {name}` first or wait for the other start to complete."
            );

            return 1;
        }

        try {
            if (ReadPidFile(name) is { } existing && IsOurDaemon(existing.Pid, existing.StartTicks)) {
                await Console.Error.WriteLineAsync(
                    $"Daemon '{name}' already running (PID {existing.Pid}). "
                  + $"Use `kcap daemon stop --name {name}` first."
                );

                return 1;
            }

            return await SpawnForegroundAsync(name, args);
        } finally {
            startLock.Dispose();
        }
    }

    static async Task<int> SpawnForegroundAsync(string name, string[] args) {
        var daemonPath = ResolveDaemonBinary();

        if (daemonPath is null) {
            await Console.Error.WriteLineAsync(DaemonNotFoundMessage());

            return 1;
        }

        var psi = new ProcessStartInfo {
            FileName        = daemonPath,
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        foreach (var arg in args) {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);

        if (process is null) {
            await Console.Error.WriteLineAsync($"Failed to start {daemonPath}");

            return 1;
        }

        WritePidFile(name, process);

        try {
            await process.WaitForExitAsync();

            return process.ExitCode;
        } finally {
            try {
                if (ReadPidFile(name) is { } current && current.Pid == process.Id) {
                    File.Delete(DaemonLockPaths.PidPath(name));
                }
            } catch {
                /* best-effort */
            }
        }
    }

    static int StartDetached(string[] args) {
        var name = ResolveName(args);

        var startLock = TryAcquireStartLock(name);

        if (startLock is null) {
            Console.Error.WriteLine(
                $"Another `kcap daemon start --name {name}` is already in progress or holds the daemon lock. "
              + $"Run `kcap daemon stop --name {name}` first or wait for the other start to complete."
            );

            return 1;
        }

        using var _ = startLock;

        if (ReadPidFile(name) is { } existing && IsOurDaemon(existing.Pid, existing.StartTicks)) {
            Console.Error.WriteLine(
                $"Daemon '{name}' already running (PID {existing.Pid}). "
              + $"Use `kcap daemon stop --name {name}` first."
            );

            return 1;
        }

        var daemonPath = ResolveDaemonBinary();

        if (daemonPath is null) {
            Console.Error.WriteLine(DaemonNotFoundMessage());

            return 1;
        }

        var psi = new ProcessStartInfo {
            FileName               = daemonPath,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            CreateNoWindow         = true
        };
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(LogPath);

        foreach (var arg in args.Where(a => a is not "-d" and not "--detach")) {
            psi.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = psi };
        process.Start();

        // Close stdin so the child doesn't wait for input
        process.StandardInput.Close();

        // AI-630: the daemon binary acquires its own per-name flock at
        // startup and exits with code 2 if another live daemon already
        // holds it (the race we couldn't catch via the PID-file guard
        // alone, e.g. when the previous daemon's PID file got wiped but
        // the process is still alive). Without this short readiness wait
        // we'd happily write a stale PID file and tell the user the
        // detached daemon is running when it actually just exited. The
        // flock acquisition happens *very* early in DaemonRunner.RunAsync
        // (before SignalR connect), so 1.5s is more than enough to catch
        // an immediate exit without making the success path feel sluggish.
        if (process.WaitForExit(1500)) {
            Console.Error.WriteLine(
                $"Daemon '{name}' failed to start (exit code {process.ExitCode}). "
              + $"Check `kcap daemon doctor` to see whether the name is held by another process."
            );

            // Translate the daemon's flock-conflict exit code straight
            // through so wrappers / CI can distinguish "name in use" from
            // a generic spawn failure.
            return process.ExitCode == 0 ? 1 : process.ExitCode;
        }

        WritePidFile(name, process);

        Console.Out.WriteLine($"Daemon '{name}' started (PID {process.Id})");
        Console.Out.WriteLine($"  Log:       {LogPath}");
        Console.Out.WriteLine($"  Stop with: kcap daemon stop --name {name}");
        Console.Out.WriteLine($"  Status:    kcap daemon status --name {name}");

        return 0;
    }

    // ── stop ────────────────────────────────────────────────────────────────

    static async Task<int> StopAsync(string[] args) {
        string? name;

        try {
            name = ExtractFlagValue(args, "--name");
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var yes = args.Contains("--yes") || args.Contains("-y");

        if (name is not null) return StopByName(name);

        // No --name: enumerate all running daemons.
        var candidates = EnumerateRunningNames();

        switch (candidates.Count) {
            case 0:
                await Console.Out.WriteLineAsync("No daemons are running.");

                return 0;
            case 1:
                return StopByName(candidates[0]);
        }

        await Console.Out.WriteLineAsync($"Found {candidates.Count} running daemons:");

        foreach (var n in candidates) {
            await Console.Out.WriteLineAsync($"  • {n}");
        }

        if (!yes) {
            await Console.Out.WriteAsync($"Stop all {candidates.Count}? [y/N] ");
            var reply = await Console.In.ReadLineAsync();

            if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");

                return 0;
            }
        }

        var failed = 0;

        foreach (var n in candidates) {
            if (StopByName(n) != 0) failed++;
        }

        return failed == 0 ? 0 : 1;
    }

    static int StopByName(string name) {
        if (ReadPidFile(name) is not { } entry) {
            Console.Error.WriteLine($"No daemon '{name}' running (no PID file found).");

            return 1;
        }

        try {
            if (!IsOurDaemon(entry.Pid, entry.StartTicks)) {
                Console.Out.WriteLine($"Daemon '{name}' was not running (stale PID file).");
                File.Delete(DaemonLockPaths.PidPath(name));

                return 0;
            }

            var process = Process.GetProcessById(entry.Pid);
            process.Kill(entireProcessTree: true);
            Console.Out.WriteLine($"Daemon '{name}' stopped (PID {entry.Pid}).");
        } catch (ArgumentException) {
            Console.Out.WriteLine($"Daemon '{name}' was not running.");
        }

        try { File.Delete(DaemonLockPaths.PidPath(name)); } catch {
            /* best-effort */
        }

        return 0;
    }

    // ── status ──────────────────────────────────────────────────────────────

    static async Task<int> Status(string[] args) {
        string? explicitName;

        try {
            explicitName = ExtractFlagValue(args, "--name");
        } catch (ArgumentException ex) {
            await Console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var names = explicitName is not null ? [explicitName] : EnumerateRunningNames();

        if (names.Count == 0) {
            await Console.Out.WriteLineAsync("Daemon: not running");

            return 0;
        }

        foreach (var name in names) {
            if (ReadPidFile(name) is not { } entry) {
                await Console.Out.WriteLineAsync($"Daemon '{name}': not running");

                continue;
            }

            if (IsOurDaemon(entry.Pid, entry.StartTicks)) {
                await Console.Out.WriteLineAsync($"Daemon '{name}': running (PID {entry.Pid})");
            } else {
                await Console.Out.WriteLineAsync($"Daemon '{name}': not running (stale PID file)");

                try { File.Delete(DaemonLockPaths.PidPath(name)); } catch {
                    /* best-effort */
                }
            }
        }

        return 0;
    }

    // ── doctor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every per-name daemon entry on disk — both <c>*.lock</c> and
    /// <c>*.pid</c> files. For each, attempts a non-destructive
    /// <c>flock</c> acquire to classify the entry as HELD (a live daemon
    /// owns the path) or STALE (file lying around with no holder, or
    /// orphan PID with no corresponding lock). With <c>--clean</c> removes
    /// the stale entries (held ones are never touched).
    /// </summary>
    static async Task<int> DoctorAsync(string[] args) {
        var clean = args.Contains("--clean");

        DaemonLockPaths.EnsureDirectory();

        var names = DaemonLockPaths.EnumerateNames();

        if (names.Count == 0) {
            await Console.Out.WriteLineAsync($"No daemon files found under {DaemonLockPaths.Directory}.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Inspecting {DaemonLockPaths.Directory}\n");
        var staleCount = 0;
        var heldCount  = 0;

        foreach (var name in names) {
            var lockPath = DaemonLockPaths.LockPath(name);
            var pidPath  = DaemonLockPaths.PidPath(name);

            var hasLock = File.Exists(lockPath);
            File.Exists(pidPath);

            string? instanceId = null;

            if (hasLock) {
                try { instanceId = (await File.ReadAllTextAsync(lockPath)).Trim().Split('\n', 2)[0]; } catch {
                    /* best-effort */
                }
            }

            var instancePrefix = instanceId is { Length: >= 8 } ? instanceId[..8] : instanceId ?? "(unknown)";

            // Probing the lock requires the file to exist. An orphan PID
            // file (no .lock alongside) is reported as STALE — there's no
            // live daemon if the flock isn't held by anyone, and a daemon
            // that's running would always own a .lock too.
            FileStream? probe = null;

            if (hasLock) {
                try {
                    probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
                } catch (IOException) {
                    // Lock is held by a live process, OR the file went away
                    // between EnumerateNames and our open. Either way: treat
                    // as held — we only down-classify to stale when probe
                    // succeeds (which means we DID get the lock cleanly).
                }
            }

            switch (probe) {
                case null when hasLock: {
                    heldCount++;
                    var pidEntry = ReadPidFile(name);
                    var alive    = pidEntry is { } e && IsOurDaemon(e.Pid, e.StartTicks);
                    var pidStr   = pidEntry is { } e2 ? e2.Pid.ToString() : "?";

                    var aliveStr = alive
                        ? $"PID {pidStr}"
                        : pidEntry is null
                            ? "(no pid file)"
                            : "(stale pid)";
                    await Console.Out.WriteLineAsync($"  {name,-20}  HELD     instance={instancePrefix}  {aliveStr}");

                    break;
                }
                case null: {
                    // No .lock file at all but a .pid is present — orphan.
                    staleCount++;
                    await Console.Out.WriteLineAsync($"  {name,-20}  STALE    instance=(none)   (orphan pid file, no lock)");

                    if (clean) {
                        try { File.Delete(pidPath); } catch {
                            /* best-effort */
                        }
                    }

                    break;
                }
                default: {
                    await probe.DisposeAsync();
                    staleCount++;
                    await Console.Out.WriteLineAsync($"  {name,-20}  STALE    instance={instancePrefix}  (no holder)");

                    if (clean) {
                        try { File.Delete(lockPath); } catch {
                            /* best-effort */
                        }

                        try { File.Delete(pidPath); } catch {
                            /* best-effort */
                        }
                    }

                    break;
                }
            }
        }

        await Console.Out.WriteLineAsync($"\n{heldCount} held, {staleCount} stale.");

        if (staleCount > 0 && !clean) {
            await Console.Out.WriteLineAsync("Re-run with --clean to remove stale entries.");
        }

        return 0;
    }

    // ── shared helpers ──────────────────────────────────────────────────────

    record struct PidEntry(int Pid, long? StartTicks);

    static void WritePidFile(string daemonName, Process process) {
        var pidPath = DaemonLockPaths.PidPath(daemonName);
        DaemonLockPaths.EnsureDirectory();

        long? startTicks = null;

        try {
            startTicks = process.StartTime.ToUniversalTime().Ticks;
        } catch (Exception) {
            // Best-effort: if StartTime isn't readable (race with rapid exit, OS
            // permission quirks), fall back to PID-only — IsOurDaemon will then
            // use the ProcessName check.
        }

        var content = startTicks is { } t
            ? $"{process.Id}\n{t}"
            : process.Id.ToString();

        File.WriteAllText(pidPath, content);
    }

    static PidEntry? ReadPidFile(string daemonName) {
        var pidPath = DaemonLockPaths.PidPath(daemonName);

        if (!File.Exists(pidPath)) return null;

        var lines = File.ReadAllText(pidPath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || !int.TryParse(lines[0], out var pid)) {
            Console.Error.WriteLine($"Invalid PID file: {pidPath}");
            File.Delete(pidPath);

            return null;
        }

        long? startTicks = lines.Length > 1 && long.TryParse(lines[1], out var ticks) ? ticks : null;

        return new PidEntry(pid, startTicks);
    }

    /// <summary>
    /// Verify that a PID belongs to our daemon. The strong check is StartTime
    /// equality — PIDs get recycled, but a recycled process won't have the
    /// same start instant. Falls back to ProcessName for legacy PID files
    /// written before StartTime was recorded.
    /// </summary>
    static bool IsOurDaemon(int pid, long? expectedStartTicks) {
        try {
            var process = Process.GetProcessById(pid);

            if (expectedStartTicks is { } expected) {
                try {
                    return process.StartTime.ToUniversalTime().Ticks == expected;
                } catch (Exception) {
                    // StartTime read failed — fall through to name-based check
                }
            }

            // Legacy PID file (no StartTime recorded) or StartTime unreadable:
            // best-effort match by process image name.
            var daemonPath = ResolveDaemonBinary();

            var ourName = daemonPath is not null
                ? Path.GetFileNameWithoutExtension(daemonPath)
                : "kcap-daemon";

            return string.Equals(process.ProcessName, ourName, StringComparison.OrdinalIgnoreCase);
        } catch (ArgumentException) {
            return false; // process doesn't exist
        }
    }

    /// <summary>
    /// Returns the daemon names that currently have a PID file on disk
    /// (per-name layout under <c>~/.config/kcap/daemons/</c>). Used by
    /// <c>daemon stop</c> / <c>daemon status</c> without <c>--name</c>.
    /// </summary>
    static List<string> EnumerateRunningNames() {
        DaemonLockPaths.EnsureDirectory();

        var dir = DaemonLockPaths.Directory;

        if (!Directory.Exists(dir)) return [];

        return [
            .. Directory.EnumerateFiles(dir, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Order()
        ];
    }

    /// <summary>
    /// Returns the value of <c>--flag &lt;value&gt;</c> from <paramref name="args"/>,
    /// or <c>null</c> if the flag isn't present.
    ///
    /// <para>Throws <see cref="ArgumentException"/> if the flag is present but
    /// the following token is missing or itself looks like another flag
    /// (starts with <c>-</c>). The strict-value check protects destructive
    /// commands like <c>daemon stop --yes --name</c> — without it, a missing
    /// value would silently fall through to the no-<c>--name</c> enumeration
    /// path and (with <c>--yes</c>) skip the multi-daemon prompt, stopping
    /// every daemon the user owns. Surfacing the bad invocation forces the
    /// user to fix their command line before anything destructive happens.</para>
    /// </summary>
    static string? ExtractFlagValue(string[] args, string flag) {
        for (var i = 0; i < args.Length; i++) {
            if (args[i] != flag) continue;

            if (i + 1 >= args.Length || string.IsNullOrEmpty(args[i + 1]) || args[i + 1].StartsWith('-')) {
                var got = i + 1 < args.Length ? $"'{args[i + 1]}'" : "<end of args>";

                throw new ArgumentException(
                    $"{flag} requires a value (got {got}). " +
                    "Pass a value (e.g. --name laptop) or omit the flag entirely."
                );
            }

            return args[i + 1];
        }

        return null;
    }

    static async Task<int> Logs() {
        if (!File.Exists(LogPath)) {
            await Console.Error.WriteLineAsync("No log file found.");

            return 1;
        }

        var lines = await File.ReadAllLinesAsync(LogPath);
        var start = Math.Max(0, lines.Length - 50);

        for (var i = start; i < lines.Length; i++) {
            await Console.Out.WriteLineAsync(lines[i]);
        }

        await Console.Error.WriteLineAsync($"\n--- {LogPath} ({lines.Length} lines total) ---");

        return 0;
    }

    // ── service (OS supervisor: launchd / systemd / scheduled task) ───────────

    static async Task<int> ServiceAsync(string[] args) {
        if (args.Length == 0) return ServiceUsage();

        var action  = args[0];
        var rest    = args[1..];
        var noStart = rest.Contains("--no-start");

        IServiceManager manager;
        try {
            manager = ServiceManagerFactory.ForCurrentOs();
        } catch (PlatformNotSupportedException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return 1;
        }

        var id = ServiceText.ServiceId(ResolveName(rest));

        switch (action) {
            case "install":   return await ServiceInstall(manager, rest, id, startNow: !noStart);
            case "uninstall": manager.Uninstall(id); await Console.Out.WriteLineAsync($"Service '{id}' uninstalled ({manager.Describe()})."); return 0;
            case "start":     manager.Start(id);     await Console.Out.WriteLineAsync($"Service '{id}' started.");   return 0;
            case "stop":      manager.Stop(id);      await Console.Out.WriteLineAsync($"Service '{id}' stopped (still installed)."); return 0;
            case "status":    return await ServiceStatus(manager, id);
            default:          return ServiceUsage();
        }
    }

    static async Task<int> ServiceInstall(IServiceManager manager, string[] args, string id, bool startNow) {
        var daemonPath = ResolveDaemonBinary();
        if (daemonPath is null) { await Console.Error.WriteLineAsync(DaemonNotFoundMessage()); return 1; }

        var profileName = ExtractFlagValue(args, "--profile") ?? AppConfig.ResolvedProfile?.ProfileName;
        var env         = ServiceEnvironment.Capture(profileName);

        var extra = new List<string>();
        if (ExtractFlagValue(args, "--max-agents") is { } mx) { extra.Add("--max-agents"); extra.Add(mx); }

        var logPath = PathHelpers.ConfigPath($"daemon-{id}.log");
        var spec    = new ServiceSpec(id, daemonPath, logPath, env, extra);

        manager.Install(spec, startNow);

        await Console.Out.WriteLineAsync($"Service '{id}' installed ({manager.Describe()}).");
        await Console.Out.WriteLineAsync("  Auto-restarts on crash/SIGKILL; starts at login.");
        await Console.Out.WriteLineAsync($"  Log:       {logPath}");
        await Console.Out.WriteLineAsync($"  Stop:      kcap daemon service stop --name {id}");
        await Console.Out.WriteLineAsync($"  Remove:    kcap daemon service uninstall --name {id}");
        return 0;
    }

    static async Task<int> ServiceStatus(IServiceManager manager, string id) {
        var status = manager.Status(id);
        await Console.Out.WriteLineAsync($"Service '{id}': {status.State} ({manager.Describe()})");
        if (status.BinaryPath is { } bin) await Console.Out.WriteLineAsync($"  binary: {bin}");
        return 0;
    }

    static int ServiceUsage() {
        Console.Error.WriteLine("Usage: kcap daemon service <install|uninstall|start|stop|status> [--name N]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  install [--name N] [--profile P] [--max-agents N] [--no-start]");
        Console.Error.WriteLine("  uninstall [--name N]   Stop and remove the service unit");
        Console.Error.WriteLine("  start [--name N]       Start the installed service now");
        Console.Error.WriteLine("  stop [--name N]        Stop the running service (stays installed)");
        Console.Error.WriteLine("  status [--name N]      Show installed/running state");
        return 1;
    }

    /// <summary>
    /// Resolve the kcap-daemon executable shipped alongside this binary.
    /// </summary>
    static string? ResolveDaemonBinary() {
        var dir     = AppContext.BaseDirectory;
        var ext     = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(dir, $"kcap-daemon{ext}");

        return File.Exists(sibling) ? sibling : null;
    }

    static string DaemonNotFoundMessage() =>
        $"kcap-daemon binary not found next to {AppContext.BaseDirectory}. Reinstall the kcap package.";

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kcap daemon <start|stop|status|logs|doctor>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  start [-d] [--name <n>]    Start the daemon (foreground, or -d for background)");
        Console.Error.WriteLine("  stop [--name <n>] [--yes]  Stop a running daemon (prompts on multi unless --yes)");
        Console.Error.WriteLine("  status [--name <n>]        Show daemon status (lists all when --name omitted)");
        Console.Error.WriteLine("  logs                       Show recent daemon log output");
        Console.Error.WriteLine("  doctor [--clean]           Diagnose lock-file state, optionally clean stale entries");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options for start:");
        Console.Error.WriteLine("  --name <name>         Daemon name (defaults to OS username)");
        Console.Error.WriteLine("  --server-url <url>    Server URL");
        Console.Error.WriteLine("  --max-agents <n>      Max concurrent hosted coding agents (default: 5)");
        Console.Error.WriteLine("  --log-file <path>     Log to file instead of console");
        Console.Error.WriteLine("  -d, --detach          Run in background (logs to file automatically)");

        return 1;
    }
}
