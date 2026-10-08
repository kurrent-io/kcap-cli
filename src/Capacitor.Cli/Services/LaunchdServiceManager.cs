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

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking launchctl.</summary>
    internal void WriteUnitFiles(ServiceSpec spec) =>
        _writeUnit(LaunchdUnit.PlistPath(home, spec.ServiceId), LaunchdUnit.Plist(spec), null);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    static int Uid() => (int)getuid();

    public string Describe() => "launchd LaunchAgent";

    public string UnitDirectory => LaunchdUnit.AgentsDir(home);

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(LaunchdUnit.PlistPath(home, spec.ServiceId), LaunchdUnit.Plist(spec))];

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

    public void Install(ServiceSpec spec, bool startNow) {
        var plistPath = LaunchdUnit.PlistPath(home, spec.ServiceId);
        // idempotent: bootout an existing job (ignore failure), then rewrite + bootstrap.
        ServiceProcess.Run("launchctl", LaunchdUnit.BootoutArgs(Uid(), spec.ServiceId));
        WriteUnitFiles(spec);
        ServiceProcess.Check("launchctl", LaunchdUnit.BootstrapArgs(Uid(), plistPath)); // RunAtLoad starts it
        if (!startNow) ServiceProcess.Run("launchctl", LaunchdUnit.KillArgs(Uid(), spec.ServiceId));
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
            throw new InvalidOperationException($"launchctl bootstrap failed (exit {code}): {err.Trim()}");
    }

    /// <summary>Per launchctl call, which normally answers in milliseconds. Refresh runs under the npm
    /// refresh wrapper's 60s kill.</summary>
    public static readonly TimeSpan RefreshCtlTimeout = TimeSpan.FromSeconds(7);

    /// <summary>The longest a reload can take once the daemon is asked: the idle-restart request, then
    /// bootout, bootstrap and the rollback bootstrap, each with at most one follow-up probe. A reload is
    /// not started with less than this left, because a kill between bootout and bootstrap leaves the job
    /// unloaded.</summary>
    public static readonly TimeSpan ReloadBudget = TimeSpan.FromSeconds(5) + 6 * RefreshCtlTimeout;

    /// <summary>
    /// Brings an installed job up to the unit this version writes: off the Adaptive process type, and
    /// onto the stable <c>current</c> path when <paramref name="stabilize"/> maps its binary there (a
    /// script install's version directory is never updated in place). The plist is rewritten whenever
    /// it is out of date, but launchd reads it only when the job loads, and the reload kills whatever
    /// the daemon hosts. So a running job is reloaded only once <paramref name="requestIdleRestart"/>
    /// reports that the daemon accepted an idle-only restart. A busy daemon refuses that restart, and
    /// the reload waits for a later refresh, as it does when <paramref name="timeLeft"/> is under
    /// <see cref="ReloadBudget"/>.
    /// </summary>
    public UnitRefresh RefreshUnit(
            string serviceId, Func<bool> requestIdleRestart, Func<TimeSpan> timeLeft, out string? error,
            Func<string, string>? stabilize = null) {
        error = null;
        var path = LaunchdUnit.PlistPath(home, serviceId);

        if (LaunchdUnit.TryReadPlist(path, out var original) != LaunchdUnit.PlistRead.Ok)
            return UnitRefresh.Unchanged;

        var (printExit, printOut, printErr, printTimedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        var probe    = printTimedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(printExit, printOut, printErr);
        var upgraded = LaunchdUnit.UpgradeProcessType(original!);

        // A plist this writer cannot upgrade would reload as Adaptive again.
        if (!LaunchdUnit.DeclaresStandardProcessType(upgraded ?? original!)) return UnitRefresh.Unchanged;

        var binary = ReadBinaryPathSafe(path);
        var target = binary is not null && stabilize is not null ? stabilize(binary) : binary;
        if (target is not null && LaunchdUnit.WithBinary(upgraded ?? original!, target) is { } repointed)
            upgraded = repointed;

        // A loaded job still running a version directory keeps it after the plist moves on.
        var loadedProgram = LaunchdUnit.LoadedProgram(printOut);
        var pinned = loadedProgram is not null && stabilize is not null && stabilize(loadedProgram) != loadedProgram;
        var stale  = probe == LabelProbe.Loaded && (SpawnTypes.IsBackgroundBand(LaunchdUnit.LoadedSpawnType(printOut)) || pinned);

        // Rewriting the file is safe whatever launchd holds; only the reload depends on what it does.
        if (upgraded is not null) _writeUnit(path, upgraded, null);
        if (probe == LabelProbe.Unknown) return UnitRefresh.Unverified;
        if (!stale) return upgraded is null ? UnitRefresh.Unchanged : UnitRefresh.Rewritten;
        if (timeLeft() < ReloadBudget) return UnitRefresh.Deferred;

        // A loaded job with no running daemon hosts nothing, and there is no socket to ask.
        var running = LaunchdUnit.StatusFromPrint(printExit, printOut) == ServiceState.Running;
        if (running && !requestIdleRestart()) return UnitRefresh.Deferred;

        // The accepted restart is already exiting the daemon. Booting out now unloads the job before
        // launchd can relaunch it from the definition it cached at load.
        var (bootoutExit, _, _, bootoutTimedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.BootoutArgs(Uid(), serviceId));
        if ((bootoutTimedOut || bootoutExit != 0) && Probe(serviceId).Label == LabelProbe.Loaded) {
            error = "launchctl bootout did not unload the job, so it keeps running as Adaptive until the next update";
            return UnitRefresh.Failed;
        }

        var reload = Bootstrap(serviceId, path, acceptAdaptive: false);
        if (reload.Error is null) return UnitRefresh.Reloaded;

        if (upgraded is not null) _writeUnit(path, original!, null);
        var rollback = Bootstrap(serviceId, path, acceptAdaptive: true);
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

    (LabelProbe Label, string StdOut) Probe(string serviceId) {
        var (exit, stdout, stderr, timedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.PrintArgs(Uid(), serviceId));
        return (timedOut ? LabelProbe.Unknown : LaunchdUnit.ClassifyPrint(exit, stdout, stderr), stdout);
    }

    /// <summary>
    /// A bootstrap that timed out may still have loaded the job, so it counts as success when a probe then
    /// finds the label loaded, and, unless <paramref name="acceptAdaptive"/>, loaded as something other
    /// than Adaptive: a bootout that did not take leaves the old job loaded. <c>After</c> is that probe's
    /// result, so the caller need not probe again.
    /// </summary>
    (string? Error, LabelProbe? After) Bootstrap(string serviceId, string plistPath, bool acceptAdaptive) {
        var (exit, _, err, timedOut) = RunCtl(RefreshCtlTimeout, LaunchdUnit.BootstrapArgs(Uid(), plistPath));
        if (!timedOut)
            return (exit == 0 ? null : $"launchctl bootstrap failed (exit {exit}): {err.Trim()}", null);

        var (label, stdout) = Probe(serviceId);
        var loaded = label == LabelProbe.Loaded && (acceptAdaptive || SpawnTypes.IsPositive(LaunchdUnit.LoadedSpawnType(stdout)));

        return (loaded ? null : "launchctl bootstrap timed out and was terminated", label);
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
                    error = $"launchctl bootstrap failed (exit {bootstrapExit}): {bootstrapErr.Trim()}";
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
            error = $"launchctl bootstrap failed (exit {bootstrapExit}): {bootstrapErr.Trim()}";
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
