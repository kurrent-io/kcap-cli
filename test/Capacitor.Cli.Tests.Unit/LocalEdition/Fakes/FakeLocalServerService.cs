using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>A per-user unit that only records: each call appends its name to the shared log, and
/// <see cref="Owner"/> names the root of whatever was last installed.</summary>
sealed class FakeLocalServerService(List<string> log, UserHome home) : ILocalServerService {
    string? _installedRoot;

    public Exception? InstallFailure { get; set; }

    public Exception? UninstallFailure { get; set; }

    /// <summary>Runs after a <see cref="Start"/> is logged, e.g. to change what the fake server answers.</summary>
    public Action? OnStart { get; set; }

    /// <summary>Replaces the readings <see cref="Owner"/> would otherwise derive from the installs.</summary>
    public UnitOwner? OwnerOverride { get; set; }

    public string Describe => "fake unit";

    public void Install(LocalServerUnit unit) {
        log.Add("install");
        if (InstallFailure is { } failure) throw failure;

        _installedRoot = unit.Root.Directory;

        var file = UnitFile(unit);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, unit.Root.Directory);
    }

    public void Start(LocalServerUnit unit) {
        log.Add("start");
        OnStart?.Invoke();
    }

    public Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        log.Add("stop");
        return Task.CompletedTask;
    }

    public Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        log.Add("uninstall");
        if (UninstallFailure is { } failure) throw failure;

        _installedRoot = null;

        var file = UnitFile(unit);
        if (File.Exists(file)) {
            File.Delete(file);
            FaultPoints.Hit($"unlink:{file}");
        }

        return Task.CompletedTask;
    }

    public string UnitFile(LocalServerUnit unit) => Path.Combine(home.Path, "Library", "LaunchAgents", unit.Label + ".plist");

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(UnitFile(unit));

    public UnitOwner Owner(LocalServerUnit unit) =>
        OwnerOverride ?? (_installedRoot is { } root
            ? new(new OwnerReading.Root(root), new OwnerReading.Root(root))
            : new(OwnerReading.Absent.Instance, OwnerReading.Absent.Instance));
}
