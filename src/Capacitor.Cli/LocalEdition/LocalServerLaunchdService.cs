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

    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _run = run ?? ServiceProcess.Run;
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));
    readonly int _uid = uid ?? (int)getuid();

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    public string Describe => "launchd LaunchAgent";

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(LocalServerLaunchdUnit.PlistPath(home, unit));

    public void Install(LocalServerUnit unit) {
        unit.PrepareLogDirectory();

        // launchd keeps the definition it loaded, so a rewritten plist takes effect only through a reload.
        if (Probe(unit) == LabelProbe.Loaded) Bootout(unit);

        var plist = LocalServerLaunchdUnit.PlistPath(home, unit);
        _writeUnit(plist, LocalServerLaunchdUnit.Plist(unit), null);

        Check(LocalServerLaunchdUnit.EnableArgs(_uid, unit));
        Check(LocalServerLaunchdUnit.BootstrapArgs(_uid, plist));
    }

    public void Start(LocalServerUnit unit) {
        Check(LocalServerLaunchdUnit.EnableArgs(_uid, unit));

        switch (Probe(unit)) {
            case LabelProbe.Absent:
                Check(LocalServerLaunchdUnit.BootstrapArgs(_uid, LocalServerLaunchdUnit.PlistPath(home, unit)));
                break;
            case LabelProbe.Loaded:
                Check(LocalServerLaunchdUnit.KickstartArgs(_uid, unit));
                break;
            default:
                throw new LocalLifecycleException($"launchd's state for {unit.Label} could not be read; nothing was started.");
        }
    }

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        Check(LocalServerLaunchdUnit.DisableArgs(_uid, unit));
        Bootout(unit);

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
    }

    public async Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        await StopAsync(unit, ct);

        var plist = LocalServerLaunchdUnit.PlistPath(home, unit);
        if (File.Exists(plist)) File.Delete(plist);
    }

    /// <summary>A failed bootout is fine when the label turns out not to be loaded at all.</summary>
    void Bootout(LocalServerUnit unit) {
        var (exit, _, err) = _run("launchctl", LocalServerLaunchdUnit.BootoutArgs(_uid, unit));
        if (exit == 0) return;

        var after = Probe(unit);
        if (after != LabelProbe.Absent)
            throw new LocalLifecycleException(
                $"launchctl bootout of {unit.Label} failed (exit {exit}: {err.Trim()}) and the label is still {after}.");
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
