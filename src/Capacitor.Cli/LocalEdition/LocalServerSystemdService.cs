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

    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _run = run ?? ServiceProcess.Run;
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));

    public string Describe => "systemd --user unit";

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(LocalServerSystemdUnit.UnitPath(home, unit));

    public void Install(LocalServerUnit unit) {
        unit.PrepareLogDirectory();
        _writeUnit(LocalServerSystemdUnit.UnitPath(home, unit), LocalServerSystemdUnit.Unit(unit), null);

        Check(LocalServerSystemdUnit.DaemonReloadArgs());
        Check(LocalServerSystemdUnit.EnableNowArgs(unit));
        // A unit that was already running keeps its old definition until restarted.
        Check(LocalServerSystemdUnit.RestartArgs(unit));
    }

    public void Start(LocalServerUnit unit) => Check(LocalServerSystemdUnit.EnableNowArgs(unit));

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        Check(LocalServerSystemdUnit.DisableNowArgs(unit));

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
    }

    public async Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        var path = LocalServerSystemdUnit.UnitPath(home, unit);

        if (File.Exists(path)) {
            await StopAsync(unit, ct);
            File.Delete(path);
        }

        Check(LocalServerSystemdUnit.DaemonReloadArgs());
    }

    void Check(string[] args) {
        var (exit, _, err) = _run("systemctl", args);
        if (exit != 0)
            throw new LocalLifecycleException($"systemctl {string.Join(' ', args)} failed (exit {exit}): {err.Trim()}");
    }
}
