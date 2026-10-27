using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>A Scheduled Task backend that records what the Windows service manager asks of it</summary>
sealed class FakeTaskManager : IServiceManager {
    public readonly HashSet<string> Installed = [];
    public readonly HashSet<string> Running = [];
    public readonly List<string> Calls = [];
    public bool RefuseUninstall;
    public bool DaemonStopFails;

    public string Describe() => "fake task";
    public string UnitDirectory => "";
    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
    public IReadOnlyList<string> ListInstalled() => [.. Installed];
    public ServiceStatus Status(string serviceId) => new(Installed.Contains(serviceId) ? ServiceState.Running : ServiceState.NotInstalled, null);
    public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => null;
    public ServiceQuery Query(string serviceId) =>
        new(LabelProbe.Absent, false, Running.Contains(serviceId) ? ServiceState.Running : ServiceState.NotInstalled, null, null);

    public bool Install(ServiceSpec spec, bool startNow, out string? error) {
        Calls.Add(startNow ? $"install {spec.ServiceId}" : $"install {spec.ServiceId} stopped");
        Installed.Add(spec.ServiceId);
        error = null;
        return true;
    }

    public void WriteAndBootstrap(ServiceSpec spec) => Install(spec, true, out _);

    public bool Uninstall(string serviceId, out string? error) {
        Calls.Add($"uninstall {serviceId}");
        error = RefuseUninstall ? "task is busy" : null;
        if (!RefuseUninstall) Installed.Remove(serviceId);
        return !RefuseUninstall;
    }

    public bool Start(string serviceId, out string? error) {
        Calls.Add($"start {serviceId}");
        error = null;
        return true;
    }
    public bool Stop(string serviceId, out string? error) { error = null; return true; }
}
