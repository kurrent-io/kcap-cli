using System.Diagnostics;
using kapacitor.Config;

namespace kapacitor.Commands;

public static class AgentCommands {
    static readonly string LogPath = PathHelpers.ConfigPath("agent.log");

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        var subcommand = args[1];
        var remaining  = args[2..];

        return subcommand switch {
            "start"  => await StartAsync(remaining),
            "stop"   => await StopAsync(remaining),
            "status" => await Status(remaining),
            "logs"   => await Logs(),
            "doctor" => await DoctorAsync(remaining),
            _        => PrintUsage()
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
    /// lock just keeps two concurrent <c>kapacitor agent start --name X</c>
    /// invocations from both observing a stale PID file and both spawning.
    /// </summary>
    static FileStream? TryAcquireStartLock(string daemonName) {
        try {
            AgentLockPaths.EnsureDirectory();

            return new FileStream(
                AgentLockPaths.StartLockPath(daemonName),
                FileMode.OpenOrCreate, FileAccess.Write, FileShare.None
            );
        } catch (IOException) {
            return null;
        }
    }

    static async Task<int> StartForegroundAsync(string[] args) {
        var name = ResolveName(args);

        // One-shot migration of pre-AI-630 legacy files so an earlier
        // foreground daemon that wrote ~/.config/kapacitor/agent.pid is
        // visible through our per-name PID-file check below. Idempotent.
        AgentLockMigration.MigrateLegacyFiles(name);

        var startLock = TryAcquireStartLock(name);

        if (startLock is null) {
            await Console.Error.WriteLineAsync(
                $"Another `kapacitor agent start --name {name}` is already in progress or holds the daemon lock. "
                + $"Run `kapacitor agent stop --name {name}` first or wait for the other start to complete."
            );

            return 1;
        }

        try {
            if (ReadPidFile(name) is { } existing && IsOurDaemon(existing.Pid, existing.StartTicks)) {
                await Console.Error.WriteLineAsync(
                    $"Agent daemon '{name}' already running (PID {existing.Pid}). "
                    + $"Use `kapacitor agent stop --name {name}` first."
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
                    File.Delete(AgentLockPaths.PidPath(name));
                }
            } catch {
                /* best-effort */
            }
        }
    }

    static int StartDetached(string[] args) {
        var name = ResolveName(args);

        AgentLockMigration.MigrateLegacyFiles(name);

        var startLock = TryAcquireStartLock(name);

        if (startLock is null) {
            Console.Error.WriteLine(
                $"Another `kapacitor agent start --name {name}` is already in progress or holds the daemon lock. "
                + $"Run `kapacitor agent stop --name {name}` first or wait for the other start to complete."
            );

            return 1;
        }

        using var _ = startLock;

        if (ReadPidFile(name) is { } existing && IsOurDaemon(existing.Pid, existing.StartTicks)) {
            Console.Error.WriteLine(
                $"Agent daemon '{name}' already running (PID {existing.Pid}). "
                + $"Use `kapacitor agent stop --name {name}` first."
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

        WritePidFile(name, process);

        Console.Out.WriteLine($"Agent daemon '{name}' started (PID {process.Id})");
        Console.Out.WriteLine($"  Log:       {LogPath}");
        Console.Out.WriteLine($"  Stop with: kapacitor agent stop --name {name}");
        Console.Out.WriteLine($"  Status:    kapacitor agent status --name {name}");

        return 0;
    }

    // ── stop ────────────────────────────────────────────────────────────────

    static async Task<int> StopAsync(string[] args) {
        var name = ExtractFlagValue(args, "--name");
        var yes  = args.Contains("--yes") || args.Contains("-y");

        if (name is not null) return StopByName(name);

        // No --name: enumerate. Legacy path (pre-AI-630): if no per-name
        // PID files exist but the legacy `agent.pid` does, migrate it
        // under the OS-username default name so this command can find it.
        var candidates = EnumerateRunningNames();

        if (candidates.Count == 0) {
            await Console.Out.WriteLineAsync("No agent daemons are running.");

            return 0;
        }

        if (candidates.Count == 1) return StopByName(candidates[0]);

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
            Console.Error.WriteLine($"No agent daemon '{name}' running (no PID file found).");

            return 1;
        }

        try {
            if (!IsOurDaemon(entry.Pid, entry.StartTicks)) {
                Console.Out.WriteLine($"Agent daemon '{name}' was not running (stale PID file).");
                File.Delete(AgentLockPaths.PidPath(name));

                return 0;
            }

            var process = Process.GetProcessById(entry.Pid);
            process.Kill(entireProcessTree: true);
            Console.Out.WriteLine($"Agent daemon '{name}' stopped (PID {entry.Pid}).");
        } catch (ArgumentException) {
            Console.Out.WriteLine($"Agent daemon '{name}' was not running.");
        }

        try { File.Delete(AgentLockPaths.PidPath(name)); } catch { /* best-effort */ }

        return 0;
    }

    // ── status ──────────────────────────────────────────────────────────────

    static async Task<int> Status(string[] args) {
        var explicitName = ExtractFlagValue(args, "--name");

        var names = explicitName is not null ? [explicitName] : EnumerateRunningNames();

        if (names.Count == 0) {
            await Console.Out.WriteLineAsync("Agent: not running");

            return 0;
        }

        foreach (var name in names) {
            if (ReadPidFile(name) is not { } entry) {
                await Console.Out.WriteLineAsync($"Agent '{name}': not running");

                continue;
            }

            if (IsOurDaemon(entry.Pid, entry.StartTicks)) {
                await Console.Out.WriteLineAsync($"Agent '{name}': running (PID {entry.Pid})");
            } else {
                await Console.Out.WriteLineAsync($"Agent '{name}': not running (stale PID file)");
                try { File.Delete(AgentLockPaths.PidPath(name)); } catch { /* best-effort */ }
            }
        }

        return 0;
    }

    // ── doctor ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists every <c>agents/&lt;name&gt;.lock</c> entry, attempts a
    /// non-destructive <c>flock</c> acquire on each to classify it as held
    /// (another process owns the lock) or stale (file lying around with no
    /// holder), and prints the holding PID + instance id prefix for each.
    /// With <c>--clean</c> removes the stale entries.
    /// </summary>
    static async Task<int> DoctorAsync(string[] args) {
        var clean = args.Contains("--clean");

        AgentLockPaths.EnsureDirectory();

        var names = AgentLockPaths.EnumerateNames();

        if (names.Count == 0) {
            await Console.Out.WriteLineAsync($"No agent daemon files found under {AgentLockPaths.Directory}.");

            return 0;
        }

        await Console.Out.WriteLineAsync($"Inspecting {AgentLockPaths.Directory}\n");
        var staleCount = 0;
        var heldCount  = 0;

        foreach (var name in names) {
            var lockPath = AgentLockPaths.LockPath(name);
            var pidPath  = AgentLockPaths.PidPath(name);

            string? instanceId = null;
            try { instanceId = File.ReadAllText(lockPath).Trim().Split('\n', 2)[0]; }
            catch { /* best-effort */ }

            var instancePrefix = instanceId is { Length: >= 8 } ? instanceId[..8] : instanceId ?? "(unknown)";

            // Try to acquire the lock non-destructively. If we succeed,
            // the lock was stale (no live holder). Drop the FileStream
            // immediately to release.
            FileStream? probe = null;

            try {
                probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            } catch (IOException) {
                // Lock is held by a live process, OR the file went away
                // between EnumerateNames and our open. Either way: treat
                // as held — we only down-classify to stale when probe
                // succeeds (which means we DID get the lock cleanly).
            }

            if (probe is null) {
                heldCount++;
                var pidEntry = ReadPidFile(name);
                var alive    = pidEntry is { } e && IsOurDaemon(e.Pid, e.StartTicks);
                var pidStr   = pidEntry is { } e2 ? e2.Pid.ToString() : "?";
                var aliveStr = alive ? $"PID {pidStr}" : pidEntry is null ? "(no pid file)" : "(stale pid)";
                await Console.Out.WriteLineAsync($"  {name,-20}  HELD     instance={instancePrefix}  {aliveStr}");
            } else {
                probe.Dispose();
                staleCount++;
                await Console.Out.WriteLineAsync($"  {name,-20}  STALE    instance={instancePrefix}  (no holder)");

                if (clean) {
                    try { File.Delete(lockPath); } catch { /* best-effort */ }
                    try { File.Delete(pidPath); }  catch { /* best-effort */ }
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
        var pidPath = AgentLockPaths.PidPath(daemonName);
        AgentLockPaths.EnsureDirectory();

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
        var pidPath = AgentLockPaths.PidPath(daemonName);

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
            var ourName    = daemonPath is not null
                ? Path.GetFileNameWithoutExtension(daemonPath)
                : "kapacitor-daemon";

            return string.Equals(process.ProcessName, ourName, StringComparison.OrdinalIgnoreCase);
        } catch (ArgumentException) {
            return false; // process doesn't exist
        }
    }

    /// <summary>
    /// Returns the daemon names that currently have a PID file on disk
    /// (per-name layout under <c>~/.config/kapacitor/agents/</c>). Used by
    /// <c>agent stop</c> / <c>agent status</c> without <c>--name</c>.
    /// </summary>
    static List<string> EnumerateRunningNames() {
        AgentLockPaths.EnsureDirectory();

        var dir = AgentLockPaths.Directory;
        if (!Directory.Exists(dir)) return [];

        return [
            .. Directory.EnumerateFiles(dir, "*.pid")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .Order()
        ];
    }

    static string? ExtractFlagValue(string[] args, string flag) {
        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] == flag) return args[i + 1];
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

    /// <summary>
    /// Resolve the kapacitor-daemon executable shipped alongside this binary.
    /// </summary>
    static string? ResolveDaemonBinary() {
        var dir = AppContext.BaseDirectory;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(dir, $"kapacitor-daemon{ext}");

        return File.Exists(sibling) ? sibling : null;
    }

    static string DaemonNotFoundMessage() =>
        $"kapacitor-daemon binary not found next to {AppContext.BaseDirectory}. Reinstall the kapacitor package.";

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor agent <start|stop|status|logs|doctor>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  start [-d] [--name <n>]    Start the agent daemon (foreground, or -d for background)");
        Console.Error.WriteLine("  stop [--name <n>] [--yes]  Stop a running agent daemon (prompts on multi unless --yes)");
        Console.Error.WriteLine("  status [--name <n>]        Show agent daemon status (lists all when --name omitted)");
        Console.Error.WriteLine("  logs                       Show recent daemon log output");
        Console.Error.WriteLine("  doctor [--clean]           Diagnose lock-file state, optionally clean stale entries");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options for start:");
        Console.Error.WriteLine("  --name <name>         Daemon name (defaults to OS username)");
        Console.Error.WriteLine("  --server-url <url>    Server URL");
        Console.Error.WriteLine("  --max-agents <n>      Max concurrent agents (default: 5)");
        Console.Error.WriteLine("  --log-file <path>     Log to file instead of console");
        Console.Error.WriteLine("  -d, --detach          Run in background (logs to file automatically)");

        return 1;
    }
}
