using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;

/// <summary>A per-user unit that only records: each call appends its name to the shared log. The unit file is a real
/// file under the home holding the root it serves, and <see cref="Owner"/> reads it, as the real adapters read the
/// plist; the loaded job is <see cref="LoadedRoot"/>.</summary>
sealed class FakeLocalServerService(List<string> log, UserHome home) : ILocalServerService {
    public Exception? InstallFailure { get; set; }

    public Exception? UninstallFailure { get; set; }

    public Exception? StopFailure { get; set; }

    /// <summary>Runs after an <see cref="Install"/> is logged, before it acts.</summary>
    public Action? OnInstall { get; set; }

    /// <summary>Runs after a <see cref="Start"/> is logged, e.g. to change what the fake server answers.</summary>
    public Action? OnStart { get; set; }

    /// <summary>Replaces the readings <see cref="Owner"/> would otherwise derive.</summary>
    public UnitOwner? OwnerOverride { get; set; }

    /// <summary>The root the loaded job serves, or null when no job is loaded.</summary>
    public string? LoadedRoot { get; set; }

    /// <summary>Answered by <see cref="Activity"/>, one per call, before anything else.</summary>
    public Queue<UnitActivity> Activities { get; } = new();

    /// <summary>Replaces the activity <see cref="Activity"/> would otherwise derive.</summary>
    public UnitActivity? ActivityOverride { get; set; }

    /// <summary>The unit the last <see cref="Install"/> wrote.</summary>
    public LocalServerUnit? Installed { get; private set; }

    /// <summary>Set by an install or start, cleared by a stop or uninstall.</summary>
    public bool Running { get; private set; }

    public string Describe => "fake unit";

    void Log(string entry) {
        lock (log) log.Add(entry);
    }

    public void Install(LocalServerUnit unit) {
        Log("install");
        OnInstall?.Invoke();
        if (InstallFailure is { } failure) throw failure;

        LoadedRoot = unit.Root.Directory;
        Running    = true;
        Installed  = unit;

        var file = UnitFile(unit);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, unit.Root.Directory);
    }

    public void Start(LocalServerUnit unit) {
        Log("start");
        Running = true;
        OnStart?.Invoke();
    }

    public Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        Log("stop");
        if (StopFailure is { } failure) throw failure;
        Running = false;
        return Task.CompletedTask;
    }

    /// <summary>Unloads the job, then, as the real verified stop does, refuses to remove the unit file while
    /// anything of the stack is still alive.</summary>
    public Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        Log("uninstall");
        if (UninstallFailure is { } failure) throw failure;

        LoadedRoot = null;
        Running    = false;

        if (LocalStackProbe.FindLive(unit.Root) is { } live)
            throw new LocalLifecycleException($"The fake unit stopped, but {live}.");

        var file = UnitFile(unit);
        if (File.Exists(file)) {
            File.Delete(file);
            FaultPoints.Hit($"unlink:{file}");
        }

        return Task.CompletedTask;
    }

    /// <summary>Unloads and removes the unit as the real quick paths do: whatever of the stack is still alive is left for
    /// the caller to find.</summary>
    public Task UninstallWithinAsync(LocalServerUnit unit, TimeSpan grace, CancellationToken ct) {
        Log("uninstall-within");
        if (UninstallFailure is { } failure) throw failure;

        LoadedRoot = null;
        Running    = false;

        if (File.Exists(UnitFile(unit))) File.Delete(UnitFile(unit));

        return Task.CompletedTask;
    }

    public string UnitFile(LocalServerUnit unit) => Path.Combine(home.Path, "Library", "LaunchAgents", unit.Label + ".plist");

    public bool IsInstalled(LocalServerUnit unit) => File.Exists(UnitFile(unit));

    public UnitOwner Owner(LocalServerUnit unit) {
        if (OwnerOverride is { } owner) return owner;

        var file = UnitFile(unit);

        return new(
            LoadedRoot is { } loaded ? new OwnerReading.Root(loaded) : OwnerReading.Absent.Instance,
            File.Exists(file) ? new OwnerReading.Root(File.ReadAllText(file)) : OwnerReading.Absent.Instance);
    }

    public UnitActivity Activity(LocalServerUnit unit) =>
        Activities.TryDequeue(out var next) ? next : ActivityOverride ?? (Running ? new UnitActivity.Running(Environment.ProcessId) : UnitActivity.Stopped.Instance);
}
