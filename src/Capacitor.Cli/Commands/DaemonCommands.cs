using System.Diagnostics;
using System.Net.Sockets;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Commands;

public sealed class DaemonCommands(
        DaemonStore store, ConfigRoot config, ProfileContext profiles, UserHome home,
        HarnessRegistry harnesses, BinaryProbe binaries, TimeProvider time, StandardStreams console) {
    string LogPath { get; } = config.Path("daemon.log");

    /// <summary>The sibling capture for the daemon's raw stderr/stdout — where a detached start points
    /// the child's fds (<c>--stderr-file</c>) and the launchd unit its <c>StandardErrorPath</c>. It
    /// holds what the primary log cannot: native fatal messages written straight to fd 2, and the
    /// pre-host startup breadcrumbs <c>DaemonRunner.StartupPhase</c> writes before the logger exists.
    /// So <c>daemon logs</c> surfaces it too — a startup that never reached the logger still leaves a
    /// trace here.</summary>
    string StderrCapturePath => Path.ChangeExtension(LogPath, null) + ".out.log";

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        var subcommand = args[1];
        var remaining  = args[2..];

        return subcommand switch {
            "start"   => await StartAsync(remaining),
            "stop"    => await StopAsync(remaining),
            "restart" => await RestartAsync(remaining),
            "status"  => await Status(remaining),
            "logs"    => await Logs(),
            "doctor"  => await DoctorAsync(remaining),
            "service" => await DaemonServiceCommands.DispatchAsync(store, config, profiles, home, time, remaining),
            "shim"    => await DaemonShimCommands.DispatchAsync(remaining, time),
            "consent" => await DaemonConsentCommand.HandleAsync(store, profiles, remaining),
            "reviewer" => await DaemonReviewerCommand.HandleAsync(store, profiles, binaries, remaining),
            _         => PrintUsage()
        };
    }

    string ResolveName(string[] args) => DaemonNameResolver.Resolve(args, profiles.DaemonName);

    async Task<int> StartAsync(string[] args) {
        var detached = args.Contains("-d") || args.Contains("--detach");

        return detached ? StartDetached(args) : await StartForegroundAsync(args);
    }

    /// <summary>
    /// Open the per-name <c>&lt;name&gt;.start</c> lock with
    /// <c>FileShare.None</c> for the CLI-side critical section that wraps
    /// the PID-file stale-check + daemon spawn. The daemon itself takes a
    /// separate <c>&lt;name&gt;.lock</c> via <c>DaemonLock</c>; this
    /// lock just keeps two concurrent <c>kcap daemon start --name X</c>
    /// invocations from both observing a stale PID file and both spawning.
    /// </summary>
    FileStream? TryAcquireStartLock(string daemonName) {
        try {
            store.EnsureDirectory();

            return new(
                store.StartLockPath(daemonName),
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.None
            );
        } catch (IOException) {
            return null;
        }
    }

    async Task<int> StartForegroundAsync(string[] args) {
        var name = ResolveName(args);

        var startLock = TryAcquireStartLock(name);

        if (startLock is null) {
            await console.Error.WriteLineAsync(
                $"Another `kcap daemon start --name {name}` is already in progress or holds the daemon lock. "
              + $"Run `kcap daemon stop --name {name}` first or wait for the other start to complete."
            );

            return 1;
        }

        try {
            if (DaemonPidProbe.ReadPidFile(store, name) is { } existing && DaemonPidProbe.IsOurDaemon(existing.Pid, existing.StartToken)) {
                await console.Error.WriteLineAsync(
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

    async Task<int> SpawnForegroundAsync(string name, string[] args) {
        var daemonPath = UnitIdentity.ResolveDaemonBinary();

        if (daemonPath is null) {
            await console.Error.WriteLineAsync(DaemonNotFoundMessage());

            return 1;
        }

        var psi = new ProcessStartInfo {
            FileName        = daemonPath,
            UseShellExecute = false,
            CreateNoWindow  = true
        };

        ApplyEnvironment(psi.Environment);

        foreach (var arg in args) {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);

        if (process is null) {
            await console.Error.WriteLineAsync($"Failed to start {daemonPath}");

            return 1;
        }

        // The daemon owns its PID file end to end: it writes it inside
        // the flock during startup (DaemonLock.TryAcquire) and deletes it under
        // the flock in Dispose. The supervisor neither writes nor deletes it.
        //
        // We deliberately do NOT delete the PID file when the child exits here. On
        // a clean exit the daemon already removed it via Dispose, so there's
        // nothing to clean up; on a hard death (SIGKILL / native abort — Dispose
        // never runs) the leftover PID file IS the unclean-exit breadcrumb the
        // next start reads, so deleting it here would erase exactly what this
        // change exists to preserve. A stale PID from a hard death is harmless:
        // the kernel released the flock, the next TryAcquire overwrites it (after
        // logging the breadcrumb), and status/doctor already treat a
        // dead-PID/foreign-token file as stale.
        await process.WaitForExitAsync();

        return process.ExitCode;
    }

    int StartDetached(string[] args) {
        var name = ResolveName(args);

        var startLock = TryAcquireStartLock(name);

        if (startLock is null) {
            console.Error.WriteLine(
                $"Another `kcap daemon start --name {name}` is already in progress or holds the daemon lock. "
              + $"Run `kcap daemon stop --name {name}` first or wait for the other start to complete."
            );

            return 1;
        }

        using var _ = startLock;

        if (DaemonPidProbe.ReadPidFile(store, name) is { } existing && DaemonPidProbe.IsOurDaemon(existing.Pid, existing.StartToken)) {
            console.Error.WriteLine(
                $"Daemon '{name}' already running (PID {existing.Pid}). "
              + $"Use `kcap daemon stop --name {name}` first."
            );

            return 1;
        }

        var daemonPath = UnitIdentity.ResolveDaemonBinary();

        if (daemonPath is null) {
            console.Error.WriteLine(DaemonNotFoundMessage());

            return 1;
        }

        if (DetachedDigestGate(daemonPath, Environment.GetEnvironmentVariable, console.Error) is int gateExit) return gateExit;

        // Redirect ALL three standard streams so the detached daemon does not
        // inherit our stdout/stderr. Left un-redirected, the daemon
        // keeps the terminal — or a capturing parent's pipe — open for its
        // whole lifetime, so `kcap daemon start -d` appears to hang: it returns
        // and the daemon reparents to init, but anything reading our piped
        // output blocks on EOF that never comes. The daemon logs to --log-file,
        // so it has no use for these streams. (Same pipe-leak hazard and fix as
        // the watcher / what's-done spawns.)
        var psi = new ProcessStartInfo {
            FileName               = daemonPath,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        psi.ArgumentList.Add("--log-file");
        psi.ArgumentList.Add(LogPath);

        ApplyEnvironment(psi.Environment);

        // we close the daemon's std pipes just below (anti-hang), which
        // means a runtime/native fatal message written straight to fd 2 would be
        // lost — the reason hard daemon deaths currently leave no trace. Point the
        // daemon's fds at a sibling capture file so those messages survive. Same
        // ".out.log" convention as the launchd unit's StandardErrorPath.
        psi.ArgumentList.Add("--stderr-file");
        psi.ArgumentList.Add(StderrCapturePath);

        foreach (var arg in args.Where(a => a is not "-d" and not "--detach")) {
            psi.ArgumentList.Add(arg);
        }

        // Stop the child from inheriting a capturing parent's pipe handles — std
        // handles on Windows, any fd >= 3 on Unix.
        ProcessHelpers.PreventInheritedHandles();

        var process = new Process { StartInfo = psi };
        process.Start();

        // Close the redirected streams from our side so we don't hold pipe FDs
        // open and the child doesn't wait on stdin.
        process.StandardInput.Close();
        process.StandardOutput.Close();
        process.StandardError.Close();

        // the daemon binary acquires its own per-name flock at
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
            console.Error.WriteLine(
                $"Daemon '{name}' failed to start (exit code {process.ExitCode}). "
              + $"Check `kcap daemon doctor` to see whether the name is held by another process."
            );

            // Translate the daemon's flock-conflict exit code straight
            // through so wrappers / CI can distinguish "name in use" from
            // a generic spawn failure.
            return process.ExitCode == 0 ? 1 : process.ExitCode;
        }

        // No supervisor PID-file write here: the daemon wrote its own
        // inside the flock during startup — which the readiness wait above just
        // confirmed it survived — and owns its deletion. A redundant out-of-flock
        // write could recreate the file after a clean daemon exit and trip the
        // successor's unclean-exit breadcrumb falsely.

        console.Out.WriteLine($"Daemon '{name}' started (PID {process.Id})");
        console.Out.WriteLine($"  Log:       {LogPath}");
        console.Out.WriteLine($"  Stop with: kcap daemon stop --name {name}");
        console.Out.WriteLine($"  Status:    kcap daemon status --name {name}");

        return 0;
    }

    /// <summary>
    /// Overlays the daemon/config roots plus the Antigravity ADC trio onto a CLI-spawned daemon's
    /// <see cref="ProcessStartInfo.Environment"/> — the same derivation
    /// <c>ServiceEnvironment.Capture</c> runs at <c>daemon service install</c>, reused here so
    /// `daemon start` and `start -d` (and the desktop app, which shells out to the latter) do not
    /// leave hosted Antigravity without it just because the daemon didn't come from a service install.
    /// </summary>
    void ApplyEnvironment(IDictionary<string, string?> env) {
        var isWindows = OperatingSystem.IsWindows();

        ApplySpawnEnvironment(
            env, store.Directory, config.Directory, isWindows,
            adcCredentialsPath: isWindows ? null : Capacitor.Cli.Harness.Antigravity.AntigravityAdcTrio.ExistingCredentialsPath(home),
            gcloudProject:      isWindows ? null : GcloudConfig.DefaultProject(home));
    }

    /// <summary>Pure half of <see cref="ApplyEnvironment"/>: the daemon/config roots are written, not
    /// left to the child to derive (a derived root is one HOME change away from a different one, and
    /// the sandboxes that do rewrite HOME name their own root anyway), and off Windows the trio is
    /// completed via <see cref="Capacitor.Cli.Harness.Antigravity.AntigravityAdcTrio.Complete"/>.
    /// <c>Complete</c> is seeded from what the environment already carries, so a trio member the
    /// operator exported wins over the derived default — it only fills what is absent, and handing it
    /// an empty view would let the derived default overwrite the exported value on the copy back.
    /// A present-but-EMPTY export is dropped from the seed so the derivation still runs — matching how
    /// <c>ServiceEnvironment.Capture</c> treats an empty capture as unset — except an empty
    /// <c>AGY_ADC_AUTH</c>, which is a deliberate refusal of ADC auth and is kept. Without that drop an
    /// empty credentials path both blocks derivation and manufactures <c>AGY_ADC_AUTH=1</c> for a path
    /// agy cannot read.</summary>
    internal static void ApplySpawnEnvironment(
            IDictionary<string, string?> env, string daemonsDir, string configDir, bool isWindows,
            string? adcCredentialsPath, string? gcloudProject) {
        env[DaemonStore.DaemonsDirEnvVar] = daemonsDir;
        env[ConfigRoot.ConfigDirEnvVar]   = configDir;

        if (isWindows) return;

        var trio = new Dictionary<string, string>();
        foreach (var (k, v) in env) {
            if (v is null) continue;
            if (v.Length > 0 || k == Capacitor.Cli.Harness.Antigravity.AntigravityAdcTrio.FlagKey)
                trio[k] = v;
        }

        Capacitor.Cli.Harness.Antigravity.AntigravityAdcTrio.Complete(trio, adcCredentialsPath, gcloudProject);

        foreach (var (k, v) in trio) env[k] = v;
    }

    /// <summary><c>DaemonRunner.BootCarriers.Seed</c>'s twin: the daemon project defines the
    /// canonical constant, but the CLI project does not reference the daemon project, so the
    /// literal is duplicated here (same pattern as <c>ServiceVerify</c>'s duplicated verify codes).</summary>
    const string SeedVar = "KCAP_CONSENT_SEED_DEFAULT";

    /// <summary>
    /// Gates a detached spawn on the embedded daemon digest, but ONLY when
    /// <see cref="SeedVar"/> is PRESENT (<c>is not null</c> — the exact-value contract: an empty
    /// value still activates, it is a deliberate refusal, not absence) in the process env — that
    /// directive is set exclusively by an app-managed start (the desktop supervisor / a
    /// self-respawn), never by a bare `kcap daemon start -d` typed at a terminal, so a manual start
    /// is never gated. This gate's only job is the digest; an empty/invalid directive value is not
    /// separately checked here — the daemon child refuses it itself (<c>consent_seed_invalid</c>,
    /// exit 0) once it actually boots. When gated, <see cref="DaemonDigest.Matches"/> false
    /// (including the fail-closed dev/test placeholder) means the sibling daemon binary doesn't
    /// match what this CLI build shipped with — refuse before spawning anything, on
    /// stdout/stderr/exit-code contract the app-side caller parses. <paramref name="env"/> is
    /// injected so this is testable without touching real process env.
    /// </summary>
    internal static int? DetachedDigestGate(string daemonPath, Func<string, string?> env, TextWriter stderr) {
        if (env(SeedVar) is null) return null; // manual start: no gate

        if (DaemonDigest.Matches(daemonPath)) return null;

        stderr.WriteLine("daemon_start_reason=package_inconsistent");

        return 43;
    }

    // ── stop ────────────────────────────────────────────────────────────────

    /// <summary>The daemon's own stop fits in about 18 s; past this it is killed.</summary>
    internal static readonly TimeSpan StopExitWait = TimeSpan.FromSeconds(30);

    async Task<int> StopAsync(string[] args) {
        string? name;

        try {
            name = ExtractFlagValue(args, "--name");
        } catch (ArgumentException ex) {
            await console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var yes     = args.Contains("--yes") || args.Contains("-y");
        // Only kcap-service needs it; under launchd or systemd a stop that skips the manager is restarted
        var direct  = OperatingSystem.IsWindows() && args.Contains("--direct");
        var request = new ShutdownRequestDto(args.Contains("--keep-agents") ? "keep" : "end", "final");

        if (name is not null) return await StopByNameAsync(name, request, direct);

        // No --name: enumerate all running daemons.
        var candidates = EnumerateRunningNames();

        switch (candidates.Count) {
            case 0:
                await console.Out.WriteLineAsync("No daemons are running.");

                return 0;
            case 1:
                return await StopByNameAsync(candidates[0], request);
        }

        await console.Out.WriteLineAsync($"Found {candidates.Count} running daemons:");

        foreach (var n in candidates) {
            await console.Out.WriteLineAsync($"  • {n}");
        }

        if (!yes) {
            await console.Out.WriteAsync($"Stop all {candidates.Count}? [y/N] ");
            var reply = await console.In.ReadLineAsync();

            if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await console.Out.WriteLineAsync("Cancelled.");

                return 0;
            }
        }

        var failed = 0;

        foreach (var n in candidates) {
            if (await StopByNameAsync(n, request) != 0) failed++;
        }

        return failed == 0 ? 0 : 1;
    }

    /// <param name="direct">Asks the daemon itself even when a service manages it: the Windows service's own
    /// graceful stop, which would otherwise be refused here and sent back to the service</param>
    async Task<int> StopByNameAsync(string name, ShutdownRequestDto request, bool direct = false) {
        var manager = direct ? null : TryServiceManager();
        if (manager is not null && ManagedState(manager, DaemonStore.Sanitize(name)) != ServiceState.NotInstalled) {
            console.Out.WriteLine(
                $"Daemon '{name}' is managed by {manager.Describe()}; a raw stop would be auto-restarted.");
            console.Out.WriteLine($"Use: kcap daemon service stop --name {name}  (or uninstall to remove it)");

            return 0;
        }

        if (DaemonPidProbe.ReadPidFile(store, name) is not { } entry) {
            // ReadPidFile returns null for BOTH an absent file and a present-but-
            // unparseable one (empty/partial — e.g. a mid-write SIGKILL; it no
            // longer unlinks the latter).
            if (!File.Exists(store.PidPath(name))) {
                console.Error.WriteLine($"No daemon '{name}' running (no PID file found).");

                return 1;
            }

            // Present-but-unusable: a leftover hard-death breadcrumb. `stop` is an
            // explicit cleanup path so it may remove it — but only under the flock,
            // so we can't race a concurrent `daemon start` that just wrote a valid
            // PID and delete a LIVE daemon's file.
            if (TryCleanupMarkersUnderLock(name)) {
                console.Out.WriteLine($"Daemon '{name}' was not running (removed unusable PID file).");

                return 0;
            }

            console.Error.WriteLine(
                $"Daemon '{name}' appears to be running (its lock is held) but its PID file is unreadable; "
              + $"not removing it. Retry, or run `kcap daemon doctor`.");

            return 1;
        }

        var outcome = ShutdownOutcome.NoAnswer;

        try {
            // entry is bound ONCE above and reused for both the ownership classification and (on
            // the live branch) the actual kill target. Re-reading the PID file here — e.g. via a
            // second DaemonPidProbe.ValidatedPid(store, name) call — would let a concurrent `daemon start`
            // that wins the race between the two reads hand us a freshly-written, genuinely-live PID
            // to kill that this call never validated from the same snapshot.
            if (!DaemonPidProbe.IsOurDaemon(entry.Pid, entry.StartToken)) {
                // A dead/foreign PID. Clean up under the flock so we don't unlink a
                // PID a concurrent start wrote after our read (TOCTOU).
                if (TryCleanupMarkersUnderLock(name))
                    console.Out.WriteLine($"Daemon '{name}' was not running (stale PID file).");
                else
                    console.Out.WriteLine($"Daemon '{name}' is running (started concurrently); leaving it.");

                return 0;
            }

            // Refuse to kill ourselves. IsOurDaemon has already said this PID is a live process
            // whose start token matches, so every identity check upstream has PASSED — the PID is
            // simply not a daemon. A real daemon is never the process running `daemon stop`, so
            // reaching here means the PID file is describing something it should not, and killing
            // its tree would take down this process and everything above it.
            //
            // Without this, Process.Kill(entireProcessTree: true) throws
            // "Cannot be used to terminate a process tree containing the calling process" — an
            // opaque InvalidOperationException that says nothing about WHICH pid file caused it.
            // That is exactly how this surfaced: a random, always-different CI test failing
            // with an identical stack, because the pid file named the test runner itself.
            if (entry.Pid == Environment.ProcessId) {
                console.Error.WriteLine(
                    $"Daemon '{name}' resolves to the current process (PID {entry.Pid}); refusing to stop it. "
                  + $"Its PID file at {store.PidPath(name)} does not describe a daemon.");

                return 1;
            }

            // Re-checked after the wait: a daemon that finished its stop just past the bound has left its
            // ledger as it meant to, and its pid may already name another process.
            outcome = await DaemonShutdownClient.StopAsync(store, name, request, StopExitWait, time, CancellationToken.None);

            if (outcome == ShutdownOutcome.Exited || !DaemonPidProbe.IsOurDaemon(entry.Pid, entry.StartToken)) {
                console.Out.WriteLine($"Daemon '{name}' stopped (PID {entry.Pid}).");
                TryCleanupMarkersUnderLock(name);

                return 0;
            }

            var process = Process.GetProcessById(entry.Pid);
            var killed  = false;

            try {
                process.Kill(entireProcessTree: true);
                killed = true;
            } catch (InvalidOperationException) when (process.HasExited) {
                // A BENIGN race, not corruption: the daemon exited on its own between
                // GetProcessById and Kill. .NET reports that as InvalidOperationException too, so
                // without this arm an ordinary well-timed stop would be accused of having a
                // malformed PID file. The outcome the caller asked for has happened — the daemon is
                // gone — so report it like the ArgumentException path below and fall through to the
                // marker cleanup.
                //
                // No test: reproducing it needs the process to exit inside that window, which cannot
                // be scheduled deterministically. Distinguished by HasExited rather than by message.
                console.Out.WriteLine($"Daemon '{name}' was not running.");
            } catch (InvalidOperationException ex) {
                // What remains is the safety rejection: .NET refuses to kill a process tree that
                // contains the caller. The self-PID guard above handles the case we can name in
                // advance; this covers the ANCESTOR case, which cannot be detected portably. Report
                // which daemon and which PID rather than letting an unattributed exception escape.
                console.Error.WriteLine(
                    $"Daemon '{name}' (PID {entry.Pid}) could not be stopped: {ex.Message} "
                  + $"Its PID file at {store.PidPath(name)} appears not to describe a daemon.");

                return 1;
            }

            if (killed) {
                // Wait for it to actually exit so the kernel releases its flock before
                // we try to reclaim it for cleanup below.
                try { process.WaitForExit(5000); } catch { /* best-effort */ }
                console.Out.WriteLine($"Daemon '{name}' stopped (PID {entry.Pid}).");
            }
        } catch (ArgumentException) {
            console.Out.WriteLine($"Daemon '{name}' was not running.");
        }

        MarkLedgerInterrupted(store, name, time, console.Error, KeepsSavedResumes(request, outcome));

        // Clean up the killed daemon's markers. The kill was a SIGKILL, so the
        // daemon's own Dispose never ran. Do it under the flock: if a `daemon
        // start` raced in after the kill and took the lock, the fresh PID belongs
        // to the NEW daemon and TryCleanupMarkersUnderLock (correctly) won't touch
        // it.
        TryCleanupMarkersUnderLock(name);

        return 0;
    }

    /// <summary>A killed daemon's agents are not resumed, except those <paramref name="where"/> excludes.</summary>
    internal static void MarkLedgerInterrupted(DaemonStore store, string name, TimeProvider time, TextWriter stderr, Func<ResumeLedgerEntry, bool>? where = null) {
        try {
            ResumeLedgerStore.For(store, name, time).MarkInterrupted("stopped_by_operator", where);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            stderr.WriteLine($"Daemon '{name}': could not mark its resume ledger interrupted ({ex.Message}).");
        }
    }

    /// <summary>A keep-agents stop that overran had already stopped some agents cleanly and recorded them
    /// resuming; only the rest are killed mid-run. Null marks every entry.</summary>
    internal static Func<ResumeLedgerEntry, bool>? KeepsSavedResumes(ShutdownRequestDto request, ShutdownOutcome outcome) =>
        request.Agents == "keep" && outcome == ShutdownOutcome.Overran ? e => e.State != ResumeState.Resuming : null;

    /// <summary>
    /// Remove the daemon-owned on-disk markers (PID file + version marker) for
    /// <paramref name="name"/>, but ONLY while holding the exclusive daemon flock.
    /// Holding it proves no daemon is live under this name and blocks any
    /// concurrent <c>daemon start</c> until we release — its
    /// <c>DaemonLock.TryAcquire</c> waits on this same flock, then writes a fresh
    /// PID afterward. Every daemon (supervisor-started, foreground, or
    /// self-respawned) takes this flock before writing its PID, so this is the
    /// race-free way for the CLI to clean up markers without unlinking a live
    /// daemon's PID file. The lock file itself is never deleted;
    /// <c>doctor --clean</c> owns that. Returns false if a live daemon
    /// holds the flock (caller should treat the name as running).
    /// </summary>
    bool TryCleanupMarkersUnderLock(string name) {
        FileStream held;

        try {
            held = new FileStream(
                store.LockPath(name), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        } catch (IOException) {
            return false; // a live daemon owns the name
        }

        using (held) {
            try { File.Delete(store.PidPath(name)); } catch { /* best-effort */ }
            try { File.Delete(store.RestartPendingPath(name)); } catch { /* best-effort */ }
            DaemonVersionMarker.Delete(store, name);
        }

        return true;
    }

    // ── restart ───────────────────────────────────────────────────────────────

    /// <summary>"force" &gt; "when-idle" &gt; "now" (bare). Force always wins.</summary>
    internal static string ParseRestartMode(string[] args) {
        if (args.Contains("--force"))     return "force";
        if (args.Contains("--when-idle")) return "when-idle";

        return "now";
    }

    async Task<int> RestartAsync(string[] args) {
        string? name;

        try {
            name = ExtractFlagValue(args, "--name");
        } catch (ArgumentException ex) {
            await console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var mode = ParseRestartMode(args);

        var targets = name is not null ? [name] : EnumerateRunningNames();

        if (targets.Count == 0) {
            await console.Out.WriteLineAsync("No daemons are running.");

            return 0;
        }

        var failed = 0;

        foreach (var n in targets) {
            if (await RestartOne(n, mode) != 0) failed++;
        }

        return failed == 0 ? 0 : 1;
    }

    async Task<int> RestartOne(string name, string mode) {
        LocalFrame? reply;

        try {
            reply = await DaemonRestartClient.RequestAsync(store, name, mode, default);
        } catch (Exception ex) when (ex is SocketException or IOException) {
            await console.Error.WriteLineAsync($"Daemon '{name}': not reachable ({ex.Message}).");

            return 1;
        }

        switch (reply?.Type) {
            case FrameType.RestartAck:
                await console.Out.WriteLineAsync(reply.Text == "restarting"
                    ? $"Daemon '{name}': restart accepted; agents are stopped and resumed on the new daemon."
                    : $"Daemon '{name}': restart {reply.Text}.");

                return 0;
            case FrameType.Error:
                await console.Error.WriteLineAsync($"Daemon '{name}': {reply.Text}");

                return 1;
            default:
                await console.Error.WriteLineAsync($"Daemon '{name}': unexpected reply.");

                return 1;
        }
    }

    // ── status ──────────────────────────────────────────────────────────────

    /// <summary>How long <c>status</c> waits for a daemon's control socket before reporting it as still
    /// starting. Short: a bound daemon accepts the connection immediately, and an unbound one refuses
    /// it at once — the timeout only bounds the rare mid-bind race.</summary>
    static readonly TimeSpan ServingProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The running daemon's status line, distinguishing a daemon that is actually serving from one
    /// whose PID is live but whose local control socket does not yet accept a connection. A validated
    /// PID proves only that the process exists; a reachable control socket proves the daemon has bound
    /// its listener. A live process whose socket is not yet reachable has acquired its lock but has not
    /// finished binding — it is starting, not serving — which a PID alone reports as flatly "running".
    /// Reachability, not a well-formed Hello, is the signal: an older daemon accepts the connection and
    /// drops the unknown Hello frame, and it is serving all the same.
    /// </summary>
    internal static string DescribeRunningDaemon(int pid, bool serving) =>
        serving
            ? $"running (PID {pid})"
            : $"running (PID {pid}, starting — not yet serving)";

    /// <summary>The status line for a service job launchd holds in the background band; null for any
    /// other spawn type, including none.</summary>
    internal static string? DescribePriority(string daemonName, string? loadedSpawnType) =>
        SpawnTypes.IsBackgroundBand(loadedSpawnType)
            ? $"  priority: loaded as {loadedSpawnType} — background priority; run `kcap daemon service refresh --name {daemonName} --force` to reload (its agents are stopped and resumed)"
            : null;

    async Task<int> Status(string[] args) {
        string? explicitName;

        try {
            explicitName = ExtractFlagValue(args, "--name");
        } catch (ArgumentException ex) {
            await console.Error.WriteLineAsync(ex.Message);

            return 1;
        }

        var manager    = TryServiceManager();
        var serviceIds = manager?.ListInstalled() ?? [];

        var names = explicitName is not null
            ? new List<string> { explicitName }
            : EnumerateRunningNames().Concat(serviceIds).Distinct().Order().ToList();

        if (names.Count == 0) {
            await console.Out.WriteLineAsync("Daemon: not running");

            return 0;
        }

        // Read each name's PID entry once, then probe every validated-live daemon AT ONCE. Each probe
        // carries its own ServingProbeTimeout, so a status over several daemons whose sockets accept
        // but stay silent costs one timeout rather than one per daemon; results are rendered below in
        // the original name order.
        var entries = names.Select(n => (Name: n, Entry: DaemonPidProbe.ReadPidFile(store, n))).ToList();

        var servingProbes = entries
            .Where(e => e.Entry is { } pe && DaemonPidProbe.IsOurDaemon(pe.Pid, pe.StartToken))
            .ToDictionary(e => e.Name, e => HelloProbe.RunAsync(store, e.Name, time, ServingProbeTimeout));

        await Task.WhenAll(servingProbes.Values);

        foreach (var (name, entry) in entries) {
            if (entry is not { } pe) {
                await console.Out.WriteLineAsync($"Daemon '{name}': not running");
            } else if (DaemonPidProbe.IsOurDaemon(pe.Pid, pe.StartToken)) {
                // Socket-reachable is the serving signal: a daemon that accepts the control-socket
                // connection has bound its listener, so it is serving even if it is an older build
                // that cannot answer the Hello frame. Only a refused or timed-out connection —
                // unreachable — is the still-starting daemon a validated PID alone cannot tell apart.
                var serving = servingProbes.TryGetValue(name, out var probe) && probe.Result.Reachable;
                await console.Out.WriteLineAsync($"Daemon '{name}': {DescribeRunningDaemon(pe.Pid, serving)}");

                // Version of the *running* daemon (from the marker it wrote at
                // startup), so the user can confirm a self-update took effect.
                if (DaemonVersionMarker.TryRead(store, name) is { } version)
                    await console.Out.WriteLineAsync($"  version: {CapacitorVersion.Display(version)}");

                if (DaemonRestartMarker.TryRead(store, name) is { } marker)
                    await console.Out.WriteLineAsync($"  {marker.Describe()}");
            } else {
                // report the stale PID file but do NOT delete it. Its
                // presence is now the durable hard-death signal the next daemon
                // reads to log the unclean-exit breadcrumb (DaemonLock); a passive
                // `status` read that removed it would give that breadcrumb a false
                // negative (run status after a SIGKILL, lose the trace). Cleanup of
                // stale entries stays with the explicit paths — `daemon stop` and
                // `daemon doctor --clean`.
                await console.Out.WriteLineAsync($"Daemon '{name}': not running (stale PID file)");
            }

            if (manager is not null) {
                var query = manager.Query(DaemonStore.Sanitize(name));
                if (query.Probe == LabelProbe.Loaded && query.State != ServiceState.NotInstalled)
                    await console.Out.WriteLineAsync($"  service: {query.State} ({manager.Describe()})");
                if (DescribePriority(name, query.LoadedSpawnType) is { } priority)
                    await console.Out.WriteLineAsync(priority);
            }

            if (DescribeResumeLedger(ResumeLedgerStore.For(store, name).ReadAll()) is { } ledger)
                await console.Out.WriteLineAsync(ledger);
        }

        return 0;
    }

    /// <summary>Null when no entry is resuming or interrupted.</summary>
    internal static string? DescribeResumeLedger(IReadOnlyList<ResumeLedgerEntry> entries) {
        var resuming    = entries.Count(e => e.State == ResumeState.Resuming);
        var interrupted = entries.Where(e => e.State == ResumeState.Interrupted).ToList();
        if (resuming == 0 && interrupted.Count == 0) return null;

        var reasons = interrupted.Select(e => e.LastError ?? "unknown").Distinct().Order(StringComparer.Ordinal).ToList();

        return $"  resuming: {resuming}  interrupted: {interrupted.Count}"
             + (reasons.Count > 0 ? $" ({string.Join(", ", reasons)})" : "");
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
    async Task<int> DoctorAsync(string[] args) {
        var clean = args.Contains("--clean");

        var claude = harnesses.Of<ClaudeHarness>().Paths;

        // MCP-registrations audit runs BEFORE the daemon-file early return below — a machine
        // with no daemon state still has registrations worth checking (duplicate Claude-scope
        // entries cost one extra server process per session; a stale absolute binary path
        // breaks the servers outright after an npm re-layout).
        await McpDoctorSection.RunAsync(console.Out, clean,
            claude.UserConfigJson, claude.UserSettings,
            McpDoctorSection.DefaultJsonRegistrations(harnesses),
            harnesses.Of<CodexHarness>().Paths.ConfigToml,
            Environment.ProcessPath is { } exe ? InstallPaths.Stabilize(exe) : null);
        await console.Out.WriteLineAsync();

        store.EnsureDirectory();

        var names = store.EnumerateNames();

        if (names.Count == 0) {
            await console.Out.WriteLineAsync($"No daemon files found under {store.Directory}.");
            await ReportInstalledServices();

            return 0;
        }

        await console.Out.WriteLineAsync($"Inspecting {store.Directory}\n");
        var staleCount = 0;
        var heldCount  = 0;

        foreach (var name in names) {
            var lockPath = store.LockPath(name);
            var pidPath  = store.PidPath(name);

            var hasLock = File.Exists(lockPath);

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
                    var pidEntry = DaemonPidProbe.ReadPidFile(store, name);
                    var alive    = pidEntry is { } e && DaemonPidProbe.IsOurDaemon(e.Pid, e.StartToken);
                    var pidStr   = pidEntry is { } e2 ? e2.Pid.ToString() : "?";

                    var aliveStr = alive
                        ? $"PID {pidStr}"
                        : pidEntry is null
                            ? "(no pid file)"
                            : "(stale pid)";
                    await console.Out.WriteLineAsync($"  {name,-20}  HELD     instance={instancePrefix}  {aliveStr}");

                    break;
                }
                case null: {
                    // No .lock file at all — leftover pid and/or marker files
                    // with no live daemon. Describe what's actually present
                    // (a version-marker-only orphan is common now that every
                    // start writes one) rather than always blaming the pid file.
                    staleCount++;
                    var leftovers = File.Exists(pidPath) ? "orphan pid file, no lock" : "marker-only, no lock/pid";
                    await console.Out.WriteLineAsync($"  {name,-20}  STALE    instance=(none)   ({leftovers})");

                    if (clean) {
                        // No lock file now, but a `daemon start` could be creating
                        // one this instant. Delete the orphan markers under the
                        // flock (TryCleanupMarkersUnderLock acquires it first), so
                        // if a start wins the race we skip rather than unlink its
                        // fresh PID.
                        TryCleanupMarkersUnderLock(name);
                    }

                    break;
                }
                default: {
                    staleCount++;
                    await console.Out.WriteLineAsync($"  {name,-20}  STALE    instance={instancePrefix}  (no holder)");

                    if (clean) {
                        // Delete the daemon-owned markers WHILE still holding the
                        // probe's flock: a concurrent `daemon start` must
                        // take this same flock, so it can't slip a live daemon in
                        // and have us unlink its fresh PID. Do NOT delete the lock
                        // file — unlinking it (even while holding it) lets a later
                        // start create a new lock inode and acquire a SECOND
                        // independent flock at the same path. Leave it;
                        // it's inert and the next start reuses it.
                        try { File.Delete(pidPath); } catch {
                            /* best-effort */
                        }

                        try { File.Delete(store.RestartPendingPath(name)); } catch {
                            /* best-effort */
                        }

                        DaemonVersionMarker.Delete(store, name);
                    }

                    await probe.DisposeAsync();

                    break;
                }
            }
        }

        await console.Out.WriteLineAsync($"\n{heldCount} held, {staleCount} stale.");

        if (staleCount > 0 && !clean) {
            await console.Out.WriteLineAsync("Re-run with --clean to remove stale entries.");
        }

        await ReportInstalledServices();

        return 0;
    }

    // ── shared helpers ──────────────────────────────────────────────────────
    //
    // PID-file parsing and identity validation (formerly ReadPidFile/IsOurDaemon here)
    // now live in DaemonPidProbe — see its ValidatedPid for the combined check.

    /// <summary>
    /// Returns the daemon names that currently have a PID file on disk
    /// (per-name layout under <c>~/.config/kcap/daemons/</c>). Used by
    /// <c>daemon stop</c> / <c>daemon status</c> without <c>--name</c>.
    /// </summary>
    List<string> EnumerateRunningNames() {
        store.EnsureDirectory();

        var dir = store.Directory;

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
    internal static string? ExtractFlagValue(string[] args, string flag) {
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

    async Task<int> Logs() {
        var shown = await TailAsync(LogPath);

        // The stderr capture holds what the primary log cannot — native fatal messages and the
        // pre-host startup breadcrumbs — so a startup that never reached the logger still leaves a
        // trace an operator can find here rather than in an undocumented sibling file. Surfaced only
        // when it has content: a clean run leaves it empty.
        shown |= await TailAsync(StderrCapturePath, skipIfEmpty: true);

        if (!shown) {
            await console.Error.WriteLineAsync("No log file found.");

            return 1;
        }

        return 0;
    }

    /// <summary>Prints the last 50 lines of <paramref name="path"/> with a trailing banner, or returns
    /// false if it is absent (or empty, when <paramref name="skipIfEmpty"/>). Read write-sharing: the
    /// daemon holds both this log and the stderr capture open for its whole life, and a plain read
    /// denies it Write on Windows.</summary>
    async Task<bool> TailAsync(string path, bool skipIfEmpty = false) {
        if (!File.Exists(path)) return false;
        if (skipIfEmpty && new FileInfo(path).Length == 0) return false;

        var lines = (await File.ReadAllTextSharedAsync(path)).Split('\n');
        // A trailing newline yields a final empty element that is not a line.
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var start = Math.Max(0, count - 50);

        for (var i = start; i < count; i++)
            await console.Out.WriteLineAsync(lines[i].TrimEnd('\r'));

        await console.Error.WriteLineAsync($"\n--- {path} ({count} lines total) ---");

        return true;
    }

    // ── service evidence for status/doctor (the verbs live in DaemonServiceCommands) ──

    /// <summary>Service manager for this OS, or null if the OS is unsupported.</summary>
    IServiceManager? TryServiceManager() {
        try { return ServiceManagerFactory.ForCurrentOs(config, home, time); }
        catch (PlatformNotSupportedException) { return null; }
    }

    /// <summary>A Windows service that does not answer counts as managing the name: a raw stop under it would
    /// be relaunched. Every other manager reports as it always has</summary>
    static ServiceState ManagedState(IServiceManager manager, string id) {
        if (manager is not WindowsServiceManager) return manager.Status(id).State;
        try { return manager.Status(id).State; }
        catch (InvalidOperationException) { return ServiceState.Installed; }
    }

    /// <summary>
    /// Doctor add-on: list OS-installed services and flag any whose baked binary
    /// path no longer exists (e.g. a moved npm prefix — fixed by re-running
    /// <c>kcap daemon service install</c>). No-op on unsupported OSes / when none.
    /// </summary>
    async Task ReportInstalledServices() {
        var manager = TryServiceManager();
        if (manager is null) return;

        var installed = manager.ListInstalled();
        if (installed.Count == 0) return;

        await console.Out.WriteLineAsync($"\nInstalled services ({manager.Describe()}):");

        foreach (var sid in installed) {
            var st   = manager.Status(sid);
            var bad  = st.BinaryPath is { } b && !File.Exists(b);
            var note = bad ? "  ⚠ binary missing — re-run `kcap daemon service install`" : "";
            await console.Out.WriteLineAsync($"  {sid,-20}  {st.State}{note}");
        }

        await ReportUnitDirectoryExposure(console.Out, manager.UnitDirectory);
    }

    /// <summary>Reports the directories on the path to the unit directory that another account owns, and
    /// the ones their group can write.
    ///
    /// <para>Advisory, not a failure: whether this matters depends on who else is in that group, which the
    /// bits do not say — umask 002 against a user-private group produces it for a group of one, and that is
    /// the common case on Debian and Ubuntu. Install refuses only the world-writable form, which no umask
    /// produces. Reporting is what is left for the case a program cannot judge but an operator can.</para></summary>
    internal static async Task ReportUnitDirectoryExposure(TextWriter output, string unitDirectory) {
        if (!OperatingSystem.IsWindows() && !FileOwner.Available)
            await output.WriteLineAsync(
                "\n  ⚠ could not check who owns the directories on the path to the unit directory: no working `stat` found");
        else if (DirectoryExposure.ForeignOwned(unitDirectory) is { Count: > 0 } foreign) {
            await output.WriteLineAsync("\n  ⚠ owned by another account on the path to the unit directory:");

            foreach (var (d, owner) in foreign) await output.WriteLineAsync($"      {d}  (uid {owner})");

            await output.WriteLineAsync(
                "    That account can replace the unit the daemon loads, whatever the directory's mode.");
        }

        if (DirectoryExposure.GrantingWrite(unitDirectory, UnixFileMode.GroupWrite) is not { Count: > 0 } shared)
            return;

        await output.WriteLineAsync("\n  ⚠ group-writable on the path to the unit directory:");

        foreach (var d in shared) await output.WriteLineAsync($"      {d}");

        await output.WriteLineAsync(
            "    A member of those directories' group can replace the unit the daemon loads. Harmless when\n"
          + "    the group is yours alone — `chmod g-w` them if it is shared with other accounts.");
    }

    internal static string DaemonNotFoundMessage() =>
        $"kcap-daemon binary not found next to {AppContext.BaseDirectory}. Reinstall the kcap package.";

    int PrintUsage() {
        console.Error.WriteLine("Usage: kcap daemon <start|stop|restart|status|logs|doctor|service|shim|consent|reviewer>");
        console.Error.WriteLine();
        console.Error.WriteLine("  start [-d] [--name <n>]    Start the daemon (foreground, or -d for background)");
        console.Error.WriteLine("  stop [--name <n>] [--yes] [--keep-agents]  Stop a running daemon and end its agents (prompts on multi unless --yes);");
        console.Error.WriteLine("                             --keep-agents keeps them for the next start to resume");
        console.Error.WriteLine("  restart [--name <n>] [--when-idle] [--force]  Restart now, resuming its agents (--when-idle queues; --force skips the flow-round wait)");
        console.Error.WriteLine("  status [--name <n>]        Show daemon status (lists all when --name omitted)");
        console.Error.WriteLine("  logs                       Show recent daemon log output");
        console.Error.WriteLine("  doctor [--clean]           Diagnose lock-file state, optionally clean stale entries");
        console.Error.WriteLine("  service <action>           Manage the OS service (launchd/systemd/Scheduled Task)");
        console.Error.WriteLine("  consent <verb>             Manage the launch-consent policy (run `kcap daemon consent` for verbs)");
        console.Error.WriteLine();
        console.Error.WriteLine("Options for start:");
        console.Error.WriteLine("  --name <name>         Daemon name (defaults to OS username)");
        console.Error.WriteLine("  --server-url <url>    Server URL");
        console.Error.WriteLine("  --max-agents <n>      Max concurrent hosted coding agents (default: 5; 0 = unlimited)");
        console.Error.WriteLine("  --log-file <path>     Log to file instead of console");
        console.Error.WriteLine("  -d, --detach          Run in background (logs to file automatically)");

        return 1;
    }
}

/// <summary>Pre-mutation viability for <c>service install --verify</c>: does the unit's pinned profile
/// resolve to a valid server URL? Mirrors the daemon's own resolution — <see cref="ProfileResolver"/>
/// over the captured <c>KCAP_URL</c>/<c>KCAP_PROFILE</c> — and the daemon's validity check (absolute
/// http/https), so the CLI rejects before the transaction destroys anything what the daemon would only
/// reject at startup.</summary>
static class ServiceInstallViability {
    public static async Task<bool> PinnedProfileServerUrlValidAsync(IReadOnlyDictionary<string, string> env, ConfigRoot root) {
        var envUrl     = env.GetValueOrDefault(ProfileOverrides.UrlVar);
        var envProfile = env.GetValueOrDefault(ProfileOverrides.ProfileVar);

        var config   = await AppConfig.LoadProfileConfig(root);
        var resolver = new ProfileResolver(config, cliServerUrl: null, envUrl, envProfile,
            repoConfig: null, repoRemoteUrls: [], repoPath: null);
        var url = resolver.Resolve().ServerUrl;

        return url is not null
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https";
    }
}
