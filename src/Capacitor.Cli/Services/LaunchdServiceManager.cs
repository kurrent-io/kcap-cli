using System.Runtime.InteropServices;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

sealed partial class LaunchdServiceManager(
    UserHome home,
    TimeProvider time,
    UnitFileWriter? writeUnit = null,
    Func<string, string[], (int ExitCode, string StdOut, string StdErr)>? runProcess = null,
    Func<string, string[], TimeSpan, (int ExitCode, string StdOut, string StdErr, bool TimedOut)>? runBounded = null
) : IServiceManager, IVerifyServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));

    // The writer's own ancestor checks, run before bootout so a refusal leaves the loaded job alone. An
    // injected writer is a test's own stand-in for the filesystem, and brings no checks.
    readonly Action<string> _requireSafeAncestors = writeUnit is null ? ServiceFiles.RequireSafeAncestors : _ => { };
    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _runProcess = runProcess ?? ServiceProcess.Run;
    readonly Func<string, string[], TimeSpan, (int ExitCode, string StdOut, string StdErr, bool TimedOut)> _runBounded = runBounded ?? ServiceProcess.RunBounded;

    /// <summary>Run one launchctl invocation. A null <paramref name="timeout"/> is the legacy path
    /// (unbounded, through the <c>runProcess</c> seam); a non-null one is the verify transaction's
    /// bounded path — a child that overruns is tree-killed and reported with <c>TimedOut</c>.</summary>
    (int Code, string StdOut, string StdErr, bool TimedOut) RunCtl(TimeSpan? timeout, string[] args) {
        if (timeout is { } t) return _runBounded("launchctl", args, t);
        var (code, stdout, stderr) = _runProcess("launchctl", args);
        return (code, stdout, stderr, false);
    }

    public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);

    public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) =>
        File.Exists(UnitPath(serviceId)) ? LaunchdUnit.EnvFromPlist(File.ReadAllText(UnitPath(serviceId))) : null;

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking launchctl.</summary>
    internal void WriteUnitFiles(ServiceSpec spec) =>
        _writeUnit(LaunchdUnit.PlistPath(home, spec.ServiceId), LaunchdUnit.Plist(spec), null);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    static int Uid() => (int)getuid();

    public string Describe() => "launchd LaunchAgent";

    public string UnitDirectory => LaunchdUnit.AgentsDir(home);

    /// <summary>Refuses an unsafe unit location here as well: a verified install generates first, before it
    /// retires, clears or replaces anything.</summary>
    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) {
        var path = LaunchdUnit.PlistPath(home, spec.ServiceId);
        _requireSafeAncestors(path);
        return [new GeneratedFile(path, LaunchdUnit.Plist(spec))];
    }

    public IReadOnlyList<string> ListInstalled() {
        var dir = LaunchdUnit.AgentsDir(home);
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "io.kurrent.kcap.daemon.*.plist")
            .Select(f => LaunchdUnit.IdFromPlistFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var path = LaunchdUnit.PlistPath(home, serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        var bin = ReadBinaryPathSafe(path); // ProgramArguments[0], not the Label
        var (code, stdout, _) = _runProcess("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
        return new ServiceStatus(LaunchdUnit.StatusFromPrint(code, stdout), bin);
    }

    public string? UnitBinary(string serviceId) => ReadBinaryPathSafe(LaunchdUnit.PlistPath(home, serviceId));

    public ServiceQuery Query(string serviceId)                 => QueryCore(serviceId, null);
    public ServiceQuery Query(string serviceId, TimeSpan t)     => QueryCore(serviceId, t);

    ServiceQuery QueryCore(string serviceId, TimeSpan? timeout) {
        var path = LaunchdUnit.PlistPath(home, serviceId);
        // File.Exists alone reads a DIRECTORY at the path (or an inaccessible ancestor) as
        // absent — open directly so presence and unreadable-but-present are never conflated.
        var unitPresent = LaunchdUnit.TryReadPlist(path, out _) != LaunchdUnit.PlistRead.Absent;
        var bin         = unitPresent ? ReadBinaryPathSafe(path) : null;
        var (code, stdout, stderr, timedOut) = RunCtl(timeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        // A killed-on-timeout print told us nothing about the label — never let its kill exit code
        // masquerade as a real classification; report Unknown so nothing destructive follows.
        var probe = timedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(code, stdout, stderr);
        var state = probe == LabelProbe.Loaded ? LaunchdUnit.StatusFromPrint(code, stdout) : ServiceState.NotInstalled;
        var loaded = probe == LabelProbe.Loaded;
        return new ServiceQuery(probe, unitPresent, state, bin,
            loaded ? LaunchdUnit.PidFromPrint(stdout) : null,
            loaded ? LaunchdUnit.LoadedSpawnType(stdout) : null);
    }

    /// <summary>Total plist-evidence read: <c>File.ReadAllText</c> + <see cref="LaunchdUnit.BinaryFromPlist"/>
    /// together, contained so that ANY failure — an I/O error, malformed XML, a duplicate
    /// <c>ProgramArguments</c> key — yields null rather than escaping. <see cref="Query(string)"/>
    /// and <see cref="Status"/> are total, never-throwing probes; the gate's own contained Phase-A
    /// parse (<c>ServiceVerify</c>'s <c>_readPlist</c> path) remains the sole authority for coded
    /// evidence classification (malformed → <c>evidence_unreadable</c>).</summary>
    static string? ReadBinaryPathSafe(string path) {
        try { return LaunchdUnit.BinaryFromPlist(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>How long a plain install gives <c>bootout</c> and the job's unload together. <c>bootout</c>
    /// returns while a running job is still exiting, and a bootstrap before it has gone fails with EIO.</summary>
    public static readonly TimeSpan UnloadWait = TimeSpan.FromSeconds(15);
    static readonly TimeSpan UnloadPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>Boots out any existing job, waits for launchd to let go of the label, then rewrites the
    /// plist and bootstraps it. A label that is not seen to unload leaves the plist untouched, so the
    /// file on disk keeps describing the job launchd runs.</summary>
    public bool Install(ServiceSpec spec, bool startNow, out string? error) {
        var plistPath = LaunchdUnit.PlistPath(home, spec.ServiceId);
        _requireSafeAncestors(plistPath);
        var started   = time.GetTimestamp();
        RunCtl(UnloadWait, LaunchdUnit.BootoutArgs(Uid(), spec.ServiceId));

        if (WaitUnloaded(spec.ServiceId, started) is { } label) {
            error = label == LabelProbe.Loaded
                ? $"launchd still had the job loaded {UnloadWait.TotalSeconds:0}s after bootout, so its plist was left as it was"
                : "launchctl could not say whether the job unloaded, so its plist was left as it was";
            return false;
        }

        WriteUnitFiles(spec);
        var (code, _, err, _) = RunCtl(null, LaunchdUnit.BootstrapArgs(Uid(), plistPath)); // RunAtLoad starts it
        if (code != 0) {
            error = $"launchctl bootstrap failed (exit {code}): {LaunchdUnit.ToolError(err)}";
            return false;
        }

        if (!startNow) RunCtl(null, LaunchdUnit.KillArgs(Uid(), spec.ServiceId));
        error = null;
        return true;
    }

    /// <summary>Null once the label is absent; otherwise the last probe's answer when the deadline
    /// <paramref name="started"/> + <see cref="UnloadWait"/> passed. Each probe gets only what is left.</summary>
    LabelProbe? WaitUnloaded(string serviceId, long started) {
        var label = LabelProbe.Unknown;
        while (UnloadWait - time.GetElapsedTime(started) is var left && left > TimeSpan.Zero) {
            var (exit, stdout, stderr, timedOut) = RunCtl(left, LaunchdUnit.PrintArgs(Uid(), serviceId));
            label = timedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(exit, stdout, stderr);
            if (label == LabelProbe.Absent) return null;
            Thread.Sleep(UnloadPoll);
        }
        return label;
    }

    /// <summary>The install-verify engine's fresh-install mutation: write + bootstrap, no leading
    /// bootout (the engine already classified the label Absent via <see cref="Query(string)"/>).</summary>
    public void WriteAndBootstrap(ServiceSpec spec)             => WriteAndBootstrapCore(spec, null);
    public void WriteAndBootstrap(ServiceSpec spec, TimeSpan t) => WriteAndBootstrapCore(spec, t);

    void WriteAndBootstrapCore(ServiceSpec spec, TimeSpan? timeout) {
        WriteUnitFiles(spec);
        var (code, _, err, timedOut) = RunCtl(timeout, LaunchdUnit.BootstrapArgs(Uid(), LaunchdUnit.PlistPath(home, spec.ServiceId)));
        if (timedOut)
            throw new TimeoutException("launchctl bootstrap timed out and was terminated");
        if (code != 0)
            throw new InvalidOperationException($"launchctl bootstrap failed (exit {code}): {LaunchdUnit.ToolError(err)}");
    }

    /// <summary>Per launchctl call, which normally answers in milliseconds. Refresh runs under the npm
    /// refresh wrapper's 60s kill.</summary>
    public static readonly TimeSpan RefreshCtlTimeout = TimeSpan.FromSeconds(7);

    /// <summary>How long a reload waits for the daemon to exit once asked to stop.</summary>
    public static readonly TimeSpan ReloadExitWait = TimeSpan.FromSeconds(30);

    /// <summary>The longest a reload can take once the daemon is asked: the stop request and its exit, then
    /// bootout, bootstrap and the rollback bootstrap, each with at most one follow-up probe. A reload is
    /// not started with less than this left, because a kill between bootout and bootstrap leaves the job
    /// unloaded.</summary>
    public static readonly TimeSpan ReloadBudget = TimeSpan.FromSeconds(5) + ReloadExitWait + 6 * RefreshCtlTimeout;

    /// <summary>
    /// Brings an installed job up to the unit this version writes: off the Adaptive process type, and
    /// onto the stable <c>current</c> path when <paramref name="stabilize"/> maps its binary there (a
    /// script install's version directory is never updated in place). The plist is rewritten whenever
    /// it is out of date, but launchd reads it only when the job loads. So a running job is reloaded only
    /// once <paramref name="stopForReload"/> reports that the daemon has accepted being stopped; one that
    /// does not waits for a later refresh, as it does when <paramref name="timeLeft"/> is under
    /// <see cref="ReloadBudget"/>. A job that is not stale counts as <see cref="UnitRefresh.Current"/>
    /// whatever its spawn type unless <paramref name="requirePositiveSpawnType"/> asks for a positive one.
    /// A plist <paramref name="amend"/> rewrites always makes a loaded job stale, and is kept only by a reload.
    /// </summary>
    public UnitRefresh RefreshUnit(
            string serviceId, Func<bool> stopForReload, Func<TimeSpan> timeLeft, out string? error,
            Func<string, string>? stabilize = null, bool requirePositiveSpawnType = false,
            Func<string, string?>? amend = null) {
        error = null;
        var path = LaunchdUnit.PlistPath(home, serviceId);

        switch (LaunchdUnit.TryReadPlist(path, out var original)) {
            case LaunchdUnit.PlistRead.Absent:     return UnitRefresh.UnitMissing;
            case LaunchdUnit.PlistRead.Unreadable: return UnitRefresh.UnitUnreadable;
        }

        var upgraded = LaunchdUnit.UpgradeProcessType(original!);
        if (!LaunchdUnit.DeclaresStandardProcessType(upgraded ?? original!)) return UnitRefresh.UnitUnsupported;

        try {
            return RefreshValidatedUnit(serviceId, path, original!, upgraded, stopForReload, timeLeft, stabilize, requirePositiveSpawnType, amend, out error);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception) {
            error = ex.Message;
            return UnitRefresh.Failed;
        }
    }

    UnitRefresh RefreshValidatedUnit(
            string serviceId, string path, string original, string? upgraded,
            Func<bool> stopForReload, Func<TimeSpan> timeLeft, Func<string, string>? stabilize, bool requirePositiveSpawnType,
            Func<string, string?>? amend, out string? error) {
        error = null;
        var (printExit, printOut, printErr, printTimedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe = printTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(printExit, printOut, printErr);

        var binary = ReadBinaryPathSafe(path);
        var target = binary is not null && stabilize is not null ? stabilize(binary) : binary;
        if (target is not null && LaunchdUnit.WithBinary(upgraded ?? original, target) is { } repointed)
            upgraded = repointed;

        // Kept only once the job reloads or is not loaded: a loaded job keeps its old copy, and a later run
        // comparing against the file would read the amendment as already applied.
        var amended = amend?.Invoke(upgraded ?? original);
        var beforeAmend = upgraded ?? original;

        // A loaded job still running a version directory keeps it after the plist moves on.
        var loadedProgram = LaunchdUnit.LoadedProgram(printOut);
        var pinned = loadedProgram is not null && stabilize is not null && stabilize(loadedProgram) != loadedProgram;
        var spawn  = probe == LabelProbe.Loaded ? LaunchdUnit.LoadedSpawnType(printOut) : null;
        var stale  = probe == LabelProbe.Loaded && (SpawnTypes.IsBackgroundBand(spawn) || pinned || amended is not null);

        // Rewriting the file is safe whatever launchd holds; only the reload depends on what it does.
        if (upgraded is not null) _writeUnit(path, upgraded, null);
        if (probe == LabelProbe.Unknown) return UnitRefresh.Unverified;
        if (probe == LabelProbe.Absent) {
            if (amended is not null) _writeUnit(path, amended, null);
            return UnitRefresh.NotLoaded;
        }
        if (!stale) return !requirePositiveSpawnType || SpawnTypes.IsPositive(spawn) ? UnitRefresh.Current : UnitRefresh.Unverified;
        if (timeLeft() < ReloadBudget) return UnitRefresh.Deferred;

        // Before the stop: a write that fails once the daemon has exited would leave it down.
        if (amended is not null) _writeUnit(path, amended, null);

        (string? Error, LabelProbe? After) reload;
        try {
            // A loaded job with no running daemon hosts nothing, and there is no socket to ask.
            var running = LaunchdUnit.StatusFromPrint(printExit, printOut) == ServiceState.Running;
            if (running && !stopForReload()) {
                if (amended is not null) _writeUnit(path, beforeAmend, null);
                return UnitRefresh.Deferred;
            }

            var (bootoutExit, _, _, bootoutTimedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.BootoutArgs(Uid(), serviceId));
            if ((bootoutTimedOut || bootoutExit != 0) && Probe(serviceId).Label == LabelProbe.Loaded) {
                if (amended is not null) _writeUnit(path, beforeAmend, null);
                error = "launchctl bootout did not unload the job, so it keeps running in the background band until the next update";
                return UnitRefresh.Failed;
            }

            reload = Bootstrap(serviceId, path, acceptAnySpawnType: false);
        } catch {
            // Same rule as every other path short of a reload: the file must not claim what launchd never loaded.
            if (amended is not null) _writeUnit(path, beforeAmend, null);
            RecoverUnloadedJob(serviceId, path, upgraded is not null || amended is not null ? original : null);
            throw;
        }

        if (reload.Error is null) return UnitRefresh.Reloaded;

        // The label loaded from the plist on disk but launchd still reports the wrong band: a second
        // bootstrap over a loaded label fails, and the file already declares Standard, so nothing is rolled back.
        if (reload.After == LabelProbe.Loaded) {
            // The loaded label may be the old job still exiting, so the amendment cannot be taken as applied.
            if (amended is not null) _writeUnit(path, beforeAmend, null);
            error = reload.Error;
            return UnitRefresh.Failed;
        }

        if (upgraded is not null || amended is not null) _writeUnit(path, original, null);
        var rollback = Bootstrap(serviceId, path, acceptAnySpawnType: true);
        var (bootstrapError, rollbackError) = (reload.Error, rollback.Error);

        if (rollbackError is null) {
            error = $"{bootstrapError}; the previous unit was restored and loaded";
        } else {
            error = (rollback.After ?? Probe(serviceId).Label) == LabelProbe.Absent
                ? $"{bootstrapError}; restoring the previous unit also failed ({rollbackError}), so the daemon is not loaded — run `kcap daemon service start`"
                : $"{bootstrapError}; restoring the previous unit also failed ({rollbackError}), and launchd's state for the job is unclear — check `kcap daemon service status`";
        }

        return UnitRefresh.Failed;
    }

    /// <summary>A throw after the bootout would otherwise leave the daemon unloaded. Best effort, so the
    /// caller's exception is the one reported.</summary>
    void RecoverUnloadedJob(string serviceId, string path, string? restore) {
        try {
            if (Probe(serviceId).Label != LabelProbe.Absent) return;
            if (restore is not null) _writeUnit(path, restore, null);
            Bootstrap(serviceId, path, acceptAnySpawnType: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or System.ComponentModel.Win32Exception) {
            // The original failure is rethrown by the caller.
        }
    }

    (LabelProbe Label, string StdOut) Probe(string serviceId) {
        var (exit, stdout, stderr, timedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        return (timedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(exit, stdout, stderr), stdout);
    }

    /// <summary>
    /// A bootstrap counts as a reload only when a follow-up probe finds the label loaded with a positive
    /// spawn type, unless <paramref name="acceptAnySpawnType"/> (the rollback of the previous unit, which
    /// only needs the label back). A bootstrap that timed out may still have loaded the job, so the probe
    /// decides for it too. <c>After</c> is that probe's label, so the caller need not probe again.
    /// </summary>
    (string? Error, LabelProbe? After) Bootstrap(string serviceId, string plistPath, bool acceptAnySpawnType) {
        var (exit, _, err, timedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.BootstrapArgs(Uid(), plistPath));
        if (!timedOut && exit != 0) return ($"launchctl bootstrap failed (exit {exit}): {LaunchdUnit.ToolError(err)}", null);

        var (label, stdout) = Probe(serviceId);
        if (label != LabelProbe.Loaded)
            return (timedOut ? "launchctl bootstrap timed out and was terminated" : "launchctl bootstrap returned but the label is not loaded", label);

        var spawn = LaunchdUnit.LoadedSpawnType(stdout);
        if (acceptAnySpawnType || SpawnTypes.IsPositive(spawn)) return (null, label);

        return (spawn is null
            ? "the label is loaded but launchd reports no spawn type for it"
            : $"the label is loaded but launchd still reports spawn type {spawn}", label);
    }

    /// <summary>
    /// A non-zero <c>bootout</c> is not automatically a failure: the label may already be unloaded. Re-query
    /// with <c>launchctl print</c> to tell that benign case apart from a bootout that actually failed to
    /// unload a live job — only <see cref="LabelProbe.Absent"/> is success; <see cref="LabelProbe.Loaded"/>
    /// or <see cref="LabelProbe.Unknown"/> retain the plist. Success asserts label absence AND file removal.
    /// </summary>
    public bool Uninstall(string serviceId, out string? error)             => UninstallCore(serviceId, null, out error);
    public bool Uninstall(string serviceId, TimeSpan t, out string? error) => UninstallCore(serviceId, t, out error);

    bool UninstallCore(string serviceId, TimeSpan? timeout, out string? error) {
        var path = LaunchdUnit.PlistPath(home, serviceId);
        var (bootoutExit, _, _, bootoutTimedOut) = RunCtl(timeout, LaunchdUnit.BootoutArgs(Uid(), serviceId));

        if (bootoutTimedOut) {
            error = $"launchctl bootout timed out and was terminated — plist retained: {path}";
            return false;
        }

        if (bootoutExit != 0) {
            var (queryExit, stdout, stderr, queryTimedOut) = RunCtl(timeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
            var probe = queryTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(queryExit, stdout, stderr);

            if (probe != LabelProbe.Absent) {
                error = $"launchctl bootout failed (exit {bootoutExit}) and the label is still {probe} — plist retained: {path}";
                return false;
            }
        }

        if (File.Exists(path)) File.Delete(path);
        error = null;
        return true;
    }

    /// <summary>
    /// A KeepAlive job between short-lived incarnations can lose its job (and the ability to catch a
    /// SIGTERM) at any moment, so we probe first and issue the verb that applies: <c>bootstrap</c> for an
    /// unloaded label, <c>kickstart</c> for a loaded one. An <see cref="LabelProbe.Unknown"/> probe means
    /// neither verb is safe to guess — fail without mutating.
    /// </summary>
    public bool Start(string serviceId, out string? error)             => StartCore(serviceId, null, out error);
    public bool Start(string serviceId, TimeSpan t, out string? error) => StartCore(serviceId, t, out error);

    bool StartCore(string serviceId, TimeSpan? timeout, out string? error) {
        var (probeExit, probeOut, probeErr, probeTimedOut) = RunCtl(timeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe = probeTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(probeExit, probeOut, probeErr);

        switch (probe) {
            case LabelProbe.Absent:
                var (bootstrapExit, _, bootstrapErr, bootstrapTimedOut) = RunCtl(timeout, LaunchdUnit.BootstrapArgs(Uid(), LaunchdUnit.PlistPath(home, serviceId)));
                if (bootstrapTimedOut) { error = "launchctl bootstrap timed out and was terminated"; return false; }
                if (bootstrapExit != 0) {
                    error = $"launchctl bootstrap failed (exit {bootstrapExit}): {LaunchdUnit.ToolError(bootstrapErr)}";
                    return false;
                }
                break;
            case LabelProbe.Loaded:
                var (kickstartExit, _, kickstartErr, kickstartTimedOut) = RunCtl(timeout, LaunchdUnit.KickstartArgs(Uid(), serviceId));
                if (kickstartTimedOut) { error = "launchctl kickstart timed out and was terminated"; return false; }
                if (kickstartExit != 0) {
                    error = $"launchctl kickstart failed (exit {kickstartExit}): {kickstartErr.Trim()}";
                    return false;
                }
                break;
            default:
                error = $"cannot start '{serviceId}': launchctl print left the label state {probe} — no action taken";
                return false;
        }

        error = null;
        return true;
    }

    /// <summary>launchd implementation of <see cref="IVerifyServiceManager.StartBootstrapOnly"/>:
    /// re-probes the label immediately before acting. Both launchctl calls share ONE deadline —
    /// the probe gets the full <paramref name="timeout"/>, and the bootstrap gets only what's left
    /// of it — rather than each getting the full budget (which would let the pair invade up to 2x
    /// the caller's forward remainder, including its separately reserved rollback budget).</summary>
    public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) {
        var started = time.GetTimestamp();
        var (probeExit, probeOut, probeErr, probeTimedOut) = RunCtl(timeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe = probeTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(probeExit, probeOut, probeErr);

        if (probe != LabelProbe.Absent) {
            error = $"cannot bootstrap-only '{serviceId}': launchctl print shows the label {probe} — refusing to kickstart";
            return false;
        }

        var remaining = timeout - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) {
            error = "launchctl bootstrap timed out and was terminated";
            return false;
        }

        var (bootstrapExit, _, bootstrapErr, bootstrapTimedOut) = RunCtl(remaining, LaunchdUnit.BootstrapArgs(Uid(), LaunchdUnit.PlistPath(home, serviceId)));
        if (bootstrapTimedOut) { error = "launchctl bootstrap timed out and was terminated"; return false; }
        if (bootstrapExit != 0) {
            error = $"launchctl bootstrap failed (exit {bootstrapExit}): {LaunchdUnit.ToolError(bootstrapErr)}";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Same benign-absence re-query rule as <see cref="Uninstall(string, out string?)"/>, but the plist is
    /// never deleted — stopping unloads the label, it does not remove the service.
    /// </summary>
    public bool Stop(string serviceId, out string? error)             => StopCore(serviceId, null, out error);
    public bool Stop(string serviceId, TimeSpan t, out string? error) => StopCore(serviceId, t, out error);

    bool StopCore(string serviceId, TimeSpan? timeout, out string? error) {
        var (bootoutExit, _, _, bootoutTimedOut) = RunCtl(timeout, LaunchdUnit.BootoutArgs(Uid(), serviceId));

        if (bootoutTimedOut) {
            error = "launchctl bootout timed out and was terminated";
            return false;
        }

        if (bootoutExit != 0) {
            var (queryExit, stdout, stderr, queryTimedOut) = RunCtl(timeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
            var probe = queryTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(queryExit, stdout, stderr);

            if (probe != LabelProbe.Absent) {
                error = $"launchctl bootout failed (exit {bootoutExit}) and the label is still {probe}";
                return false;
            }
        }

        error = null;
        return true;
    }
}
