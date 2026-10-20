using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

sealed partial class LocalServerLaunchdService(
    UserHome home,
    TimeProvider time,
    Func<string, string[], (int ExitCode, string StdOut, string StdErr)>? run = null,
    UnitFileWriter? writeUnit = null,
    Func<LocalRoot, string?>? findLive = null,
    int? uid = null
) : ILocalServerService {
    /// <summary>launchd's own SIGTERM-to-SIGKILL wait, then time for the probe to see the lock released.</summary>
    static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(LocalServerLaunchdUnit.ExitTimeoutSeconds + 15);

    static readonly TimeSpan UnloadPollGap = TimeSpan.FromMilliseconds(250);

    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _run = run ?? ServiceProcess.Run;
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding, durable: true, home: home));

    // As in LaunchdServiceManager: the writer's checks, before anything is unloaded.
    readonly Action<string> _requireSafeAncestors = writeUnit is null ? ServiceFiles.RequireSafeAncestors : _ => { };
    readonly int _uid = uid ?? (int)getuid();

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    public string Describe => "launchd LaunchAgent";

    public string UnitFile(LocalServerUnit unit) => LocalServerLaunchdUnit.PlistPath(home, unit);

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(UnitFile(unit));

    public UnitOwner Owner(LocalServerUnit unit) {
        var (exit, stdout, stderr) = _run("launchctl", LocalServerLaunchdUnit.PrintArgs(_uid, unit));

        return new(LocalServerOwnerReader.FromPrint(exit, stdout, stderr),
            LocalServerOwnerReader.FromPlist(LocalServerLaunchdUnit.PlistPath(home, unit)));
    }

    public UnitActivity Activity(LocalServerUnit unit) {
        var (exit, stdout, stderr) = _run("launchctl", LocalServerLaunchdUnit.PrintArgs(_uid, unit));

        return LocalServerActivityReader.FromPrint(exit, stdout, stderr);
    }

    public void Install(LocalServerUnit unit) {
        _requireSafeAncestors(LocalServerLaunchdUnit.PlistPath(home, unit));
        unit.PrepareLogDirectory();

        // launchd keeps the definition it loaded, so a rewritten plist takes effect only through a reload.
        switch (Probe(unit)) {
            case LabelProbe.Loaded:  Bootout(unit); break;
            case LabelProbe.Unknown: throw Unreadable(unit, "nothing was installed");
        }

        var plist = LocalServerLaunchdUnit.PlistPath(home, unit);
        _writeUnit(plist, LocalServerLaunchdUnit.Plist(unit), null);

        Check(LocalServerLaunchdUnit.EnableArgs(_uid, unit));
        Check(LocalServerLaunchdUnit.BootstrapArgs(_uid, plist));
    }

    /// <summary>Probes before enabling, so a start that cannot go ahead leaves a persisted stop in place.</summary>
    public void Start(LocalServerUnit unit) {
        var state = Probe(unit);
        if (state == LabelProbe.Unknown) throw Unreadable(unit, "nothing was started");

        Check(LocalServerLaunchdUnit.EnableArgs(_uid, unit));

        Check(state == LabelProbe.Loaded
            ? LocalServerLaunchdUnit.KickstartArgs(_uid, unit)
            : LocalServerLaunchdUnit.BootstrapArgs(_uid, LocalServerLaunchdUnit.PlistPath(home, unit)));
    }

    static LocalLifecycleException Unreadable(LocalServerUnit unit, string consequence) =>
        new($"launchd's state for {unit.Label} could not be read; {consequence}.");

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        Check(LocalServerLaunchdUnit.DisableArgs(_uid, unit));
        Bootout(unit);

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
    }

    public async Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        await StopAsync(unit, ct);

        var plist = LocalServerLaunchdUnit.PlistPath(home, unit);
        if (File.Exists(plist)) {
            File.Delete(plist);
            FaultPoints.Hit($"unlink:{plist}");
        }
    }

    /// <summary>Done only when the label is confirmed unloaded, whatever bootout's exit code; a failed bootout
    /// is fine when the label was not loaded at all. bootout returns while the job is still exiting, and launchd
    /// keeps the label loaded until it has, so the confirmation waits as long as launchd's own exit timeout.</summary>
    void Bootout(LocalServerUnit unit) {
        var (exit, _, err) = _run("launchctl", LocalServerLaunchdUnit.BootoutArgs(_uid, unit));
        var deadline       = time.GetUtcNow() + StopBudget;

        var after = Probe(unit);
        while (exit == 0 && after == LabelProbe.Loaded && time.GetUtcNow() < deadline) {
            Thread.Sleep(UnloadPollGap);
            after = Probe(unit);
        }

        if (after != LabelProbe.Absent)
            throw new LocalLifecycleException(
                $"launchctl bootout of {unit.Label} exited {exit}{(string.IsNullOrWhiteSpace(err) ? "" : $" ({err.Trim()})")}, but the label is still {after}.");
    }

    LabelProbe Probe(LocalServerUnit unit) {
        var (exit, stdout, stderr) = _run("launchctl", LocalServerLaunchdUnit.PrintArgs(_uid, unit));
        return LaunchdUnit.ClassifyPrint(exit, stdout, stderr);
    }

    void Check(string[] args) {
        var (exit, _, err) = _run("launchctl", args);
        if (exit != 0)
            throw new LocalLifecycleException($"launchctl {string.Join(' ', args)} failed (exit {exit}): {err.Trim()}");
    }
}
