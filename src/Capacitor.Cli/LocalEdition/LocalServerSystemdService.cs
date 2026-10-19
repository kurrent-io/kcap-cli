using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

sealed class LocalServerSystemdService(
    UserHome home,
    TimeProvider time,
    Func<string, string[], (int ExitCode, string StdOut, string StdErr)>? run = null,
    UnitFileWriter? writeUnit = null,
    Func<LocalRoot, string?>? findLive = null
) : ILocalServerService {
    /// <summary><c>systemctl stop</c> returns once the unit is down, killing what is left at its timeout, so this
    /// only has to cover the probe seeing the lock released.</summary>
    static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(15);

    /// <summary>systemctl's exit status for a unit it has no record of (LSB "program is not installed").</summary>
    const int UnitNotLoadedExit = 5;

    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _run = run ?? ServiceProcess.Run;
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding, durable: true, home: home));

    public string Describe => "systemd --user unit";

    public string UnitFile(LocalServerUnit unit) => LocalServerSystemdUnit.UnitPath(home, unit);

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(UnitFile(unit));

    public UnitOwner Owner(LocalServerUnit unit) {
        var (exit, stdout, stderr) = _run("systemctl", LocalServerSystemdUnit.ShowArgs(unit));

        return new(LocalServerOwnerReader.FromShow(exit, stdout, stderr),
            LocalServerOwnerReader.FromUnitFile(LocalServerSystemdUnit.UnitPath(home, unit)));
    }

    public UnitActivity Activity(LocalServerUnit unit) {
        var (exit, stdout, stderr) = _run("systemctl", LocalServerSystemdUnit.ActivityArgs(unit));

        return LocalServerActivityReader.FromShow(exit, stdout, stderr);
    }

    public void Install(LocalServerUnit unit) {
        unit.PrepareLogDirectory();
        _writeUnit(LocalServerSystemdUnit.UnitPath(home, unit), LocalServerSystemdUnit.Unit(unit), null);

        Check(LocalServerSystemdUnit.DaemonReloadArgs());
        Check(LocalServerSystemdUnit.EnableArgs(unit));
        ResetFailed(unit);
        // One start either way: a running unit keeps its old definition until restarted, and a fresh one is
        // started by the same call rather than started and then interrupted.
        Check(LocalServerSystemdUnit.RestartArgs(unit));
    }

    public void Start(LocalServerUnit unit) {
        ResetFailed(unit);
        Check(LocalServerSystemdUnit.EnableNowArgs(unit));
    }

    /// <summary>A unit that hit its start limit refuses every start until this clears it. Best effort: there is
    /// nothing to clear on a unit that has not failed.</summary>
    void ResetFailed(LocalServerUnit unit) => _run("systemctl", LocalServerSystemdUnit.ResetFailedArgs(unit));

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        Check(LocalServerSystemdUnit.DisableNowArgs(unit));

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
    }

    public async Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        var path = LocalServerSystemdUnit.UnitPath(home, unit);

        if (File.Exists(path)) {
            await StopAsync(unit, ct);
            File.Delete(path);
            FaultPoints.Hit($"unlink:{path}");
        } else {
            // systemd can still run a unit whose file is gone; there is nothing left to disable, but it must stop.
            // Only "not loaded" means there was nothing to stop: a server that has not yet taken its lock would
            // pass the probe below, so no other failure may.
            var (exit, _, err) = _run("systemctl", LocalServerSystemdUnit.StopArgs(unit));
            if (exit is not (0 or UnitNotLoadedExit))
                throw new LocalLifecycleException(
                    $"systemctl {string.Join(' ', LocalServerSystemdUnit.StopArgs(unit))} failed (exit {exit}): {err.Trim()}");

            await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
        }

        Check(LocalServerSystemdUnit.DaemonReloadArgs());
    }

    void Check(string[] args) {
        var (exit, _, err) = _run("systemctl", args);
        if (exit != 0)
            throw new LocalLifecycleException($"systemctl {string.Join(' ', args)} failed (exit {exit}): {err.Trim()}");
    }
}
