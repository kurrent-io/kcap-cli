using System.Diagnostics;

namespace kapacitor.Commands;

public static class AgentCommands {
    static readonly string PidPath  = PathHelpers.ConfigPath("agent.pid");
    static readonly string LogPath  = PathHelpers.ConfigPath("agent.log");
    static readonly string LockPath = PathHelpers.ConfigPath("agent.start.lock");

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        var subcommand = args[1];
        var remaining  = args[2..];

        return subcommand switch {
            "start"  => await StartAsync(remaining),
            "stop"   => Stop(),
            "status" => await Status(),
            "logs"   => await Logs(),
            _        => PrintUsage()
        };
    }

    static async Task<int> StartAsync(string[] args) {
        var detached = args.Contains("-d") || args.Contains("--detach");

        return detached ? StartDetached(args) : await StartForegroundAsync(args);
    }

    /// <summary>
    /// Open <c>agent.start.lock</c> with <c>FileShare.None</c>, giving the
    /// caller exclusive access for as long as the returned stream is held.
    /// Returns null if another process already holds the lock — i.e. another
    /// <c>kapacitor agent start</c> is mid-flight (its check + spawn +
    /// WritePidFile window) or a foreground daemon is currently running.
    ///
    /// This is the OS-level barrier the PID-file guard alone can't provide:
    /// without it, two starts launched concurrently can both observe an
    /// absent / stale PID file and both spawn a daemon before either writes.
    /// On POSIX .NET maps <c>FileShare.None</c> to <c>flock(LOCK_EX)</c>;
    /// on Windows it's a native sharing-mode constraint. Both are
    /// per-open-handle, so closing the stream releases the lock — including
    /// when the parent process is SIGKILL'd.
    /// </summary>
    static FileStream? TryAcquireStartLock() {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);

            return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        } catch (IOException) {
            return null;
        }
    }

    static async Task<int> StartForegroundAsync(string[] args) {
        // Take the exclusive start lock BEFORE the PID-file stale-check —
        // otherwise two concurrent `kapacitor agent start` invocations can
        // both observe no live daemon, both spawn, and only one's PID ends
        // up in the file. We hold this lock for the foreground daemon's
        // entire lifetime so any further start (foreground or detached)
        // refuses immediately rather than racing through.
        var startLock = TryAcquireStartLock();

        if (startLock is null) {
            await Console.Error.WriteLineAsync("Another `kapacitor agent start` is already in progress or holds the daemon lock. Run `kapacitor agent stop` first or wait for the other start to complete.");

            return 1;
        }

        try {
            // Defence in depth: the lock prevents a concurrent start from
            // racing past us, but a stale PID file from a prior cleanly-exited
            // daemon (or one whose parent was SIGKILL'd) can still be lying
            // around. IsOurDaemon's StartTime check rejects recycled PIDs.
            if (ReadPidFile() is { } existing && IsOurDaemon(existing.Pid, existing.StartTicks)) {
                await Console.Error.WriteLineAsync($"Agent daemon already running (PID {existing.Pid}). Use `kapacitor agent stop` first.");

                return 1;
            }

            return await SpawnForegroundAsync(args);
        } finally {
            startLock.Dispose();
        }
    }

    static async Task<int> SpawnForegroundAsync(string[] args) {
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

        // Write the PID file so the guard above (and `kapacitor agent stop` /
        // `agent status`) sees the foreground daemon too. Best-effort cleanup
        // on exit; if the parent dies hard, IsOurDaemon's StartTime check
        // handles the recycled-PID case so a stale file doesn't lock anyone
        // out.
        WritePidFile(process);

        try {
            await process.WaitForExitAsync();

            return process.ExitCode;
        } finally {
            // Only delete the PID file if it still points at us. A concurrent
            // legitimate `kapacitor agent start` that ran after our daemon
            // exited (passing the guard because IsOurDaemon returned false on
            // our exiting PID) would have rewritten the file to its own PID;
            // an unconditional delete here would orphan that daemon's PID
            // file and re-open the duplicate-daemon hole this guard is meant
            // to close.
            try {
                if (ReadPidFile() is { } current && current.Pid == process.Id) {
                    File.Delete(PidPath);
                }
            } catch {
                /* best-effort */
            }
        }
    }

    static int StartDetached(string[] args) {
        // Hold the start lock just for the check + spawn + WritePidFile
        // window. We can't keep it past method return because the parent
        // process is about to exit, leaving the daemon detached. Once the
        // PID file is written, subsequent starts will see it via the
        // existing stale-check path.
        var startLock = TryAcquireStartLock();

        if (startLock is null) {
            Console.Error.WriteLine("Another `kapacitor agent start` is already in progress or holds the daemon lock. Run `kapacitor agent stop` first or wait for the other start to complete.");

            return 1;
        }

        using var _ = startLock;

        if (ReadPidFile() is { } existing && IsOurDaemon(existing.Pid, existing.StartTicks)) {
            Console.Error.WriteLine($"Agent daemon already running (PID {existing.Pid}). Use `kapacitor agent stop` first.");

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

        WritePidFile(process);

        Console.Out.WriteLine($"Agent daemon started (PID {process.Id})");
        Console.Out.WriteLine($"  Log:       {LogPath}");
        Console.Out.WriteLine("  Stop with: kapacitor agent stop");
        Console.Out.WriteLine("  Status:    kapacitor agent status");

        return 0;
    }

    static int Stop() {
        if (ReadPidFile() is not { } entry) {
            Console.Error.WriteLine("No agent daemon running (no PID file found).");

            return 1;
        }

        try {
            if (!IsOurDaemon(entry.Pid, entry.StartTicks)) {
                Console.Out.WriteLine("Agent daemon was not running (stale PID file).");
                File.Delete(PidPath);

                return 0;
            }

            var process = Process.GetProcessById(entry.Pid);
            process.Kill(entireProcessTree: true);
            Console.Out.WriteLine($"Agent daemon stopped (PID {entry.Pid}).");
        } catch (ArgumentException) {
            Console.Out.WriteLine("Agent daemon was not running.");
        }

        File.Delete(PidPath);

        return 0;
    }

    static async Task<int> Status() {
        if (ReadPidFile() is not { } entry) {
            await Console.Out.WriteLineAsync("Agent: not running");

            return 0;
        }

        if (IsOurDaemon(entry.Pid, entry.StartTicks)) {
            await Console.Out.WriteLineAsync($"Agent: running (PID {entry.Pid})");
        } else {
            await Console.Out.WriteLineAsync("Agent: not running (stale PID file)");
            File.Delete(PidPath);
        }

        return 0;
    }

    record struct PidEntry(int Pid, long? StartTicks);

    static void WritePidFile(Process process) {
        var dir = Path.GetDirectoryName(PidPath)!;
        Directory.CreateDirectory(dir);

        // Record StartTime alongside PID so a recycled PID can't be mistaken
        // for our daemon. UTC ticks are stable across local timezone changes.
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

        File.WriteAllText(PidPath, content);
    }

    static PidEntry? ReadPidFile() {
        if (!File.Exists(PidPath)) return null;

        var lines = File.ReadAllText(PidPath)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0 || !int.TryParse(lines[0], out var pid)) {
            Console.Error.WriteLine($"Invalid PID file: {PidPath}");
            File.Delete(PidPath);

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

    static async Task<int> Logs() {
        if (!File.Exists(LogPath)) {
            await Console.Error.WriteLineAsync("No log file found.");

            return 1;
        }

        // Tail last 50 lines
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
    /// The daemon is published as a separate AOT binary in the same directory
    /// as the main kapacitor binary (npm packages bundle both).
    /// </summary>
    static string? ResolveDaemonBinary() {
        var dir = AppContext.BaseDirectory;
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(dir, $"kapacitor-daemon{ext}");

        return File.Exists(sibling) ? sibling : null;
    }

    static string DaemonNotFoundMessage() {
        return $"kapacitor-daemon binary not found next to {AppContext.BaseDirectory}. Reinstall the kapacitor package.";
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor agent <start|stop|status|logs>");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  start [-d]  Start the agent daemon (foreground, or -d for background)");
        Console.Error.WriteLine("  stop        Stop the background agent daemon");
        Console.Error.WriteLine("  status      Show agent daemon status");
        Console.Error.WriteLine("  logs        Show recent daemon log output");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options for start:");
        Console.Error.WriteLine("  --name <name>         Daemon name");
        Console.Error.WriteLine("  --server-url <url>    Server URL");
        Console.Error.WriteLine("  --max-agents <n>      Max concurrent agents (default: 5)");
        Console.Error.WriteLine("  --log-file <path>     Log to file instead of console");
        Console.Error.WriteLine("  -d, --detach          Run in background (logs to file automatically)");

        return 1;
    }
}
