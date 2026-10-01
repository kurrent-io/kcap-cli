using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

sealed class WindowsScheduledTaskServiceManager(
        ConfigRoot config, UnitFileWriter? writeUnit = null, Func<string, int?>? daemonPid = null) : IServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));
    readonly Func<string, int?> _daemonPid = daemonPid ?? (id => DaemonPidProbe.ValidatedPid(DaemonStore.FromEnvironment(), id));

    public string Describe() => "Windows Scheduled Task";

    public string UnitDirectory => config.Directory;

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) {
        var wrapperPath = WindowsTaskUnit.WrapperPath(config, spec.ServiceId);
        return [
            new GeneratedFile(wrapperPath, WindowsTaskUnit.Wrapper(spec)),
            new GeneratedFile(TaskXmlTempPath(config, spec.ServiceId), WindowsTaskUnit.TaskXml(spec, wrapperPath)),
        ];
    }

    static string TaskXmlTempPath(ConfigRoot config, string id) => config.Path($"daemon-service-{id}.task.xml");

    public IReadOnlyList<string> ListInstalled() {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", "/Query", "/FO", "LIST");
        if (code != 0) return [];
        return [.. stdout.Split('\n')
            .Where(l => l.TrimStart().StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
            .Select(l => WindowsTaskUnit.IdFromTaskName(Path.GetFileName(l.Split(':', 2)[1].Trim())))
            .Where(id => id is not null).Select(id => id!).Distinct().Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", WindowsTaskUnit.QueryArgs(serviceId));
        var wrapper = WindowsTaskUnit.WrapperPath(config, serviceId);
        // Report the daemon binary baked inside the wrapper (not the wrapper itself)
        // so doctor catches a moved kcap-daemon.exe even when the wrapper still exists.
        var bin = File.Exists(wrapper) ? WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(wrapper)) : null;
        return new ServiceStatus(WindowsTaskUnit.StatusFromQuery(code, stdout), bin);
    }

    public ServiceQuery Query(string serviceId) {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", WindowsTaskUnit.QueryArgs(serviceId));
        var wrapper = WindowsTaskUnit.WrapperPath(config, serviceId);
        var bin = File.Exists(wrapper) ? WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(wrapper)) : null;
        var state = WindowsTaskUnit.StatusFromQuery(code, stdout);
        var probe = state != ServiceState.NotInstalled ? LabelProbe.Loaded : LabelProbe.Absent;
        var jobPid = state == ServiceState.Running && OperatingSystem.IsWindows()
            ? TaskOwnedPid(_daemonPid(serviceId), WindowsProcessTable.Snapshot())
            : null;
        return new ServiceQuery(probe, File.Exists(wrapper), state, bin, jobPid);
    }

    /// The task's job pid, launchd's sense: the running daemon, when the task is what started it. The
    /// action is `conhost --headless cmd /c wrapper`, so a daemon the task runs is a child of a live
    /// cmd.exe that is itself a child of conhost.exe. A daemon started by hand has neither above it.
    internal static int? TaskOwnedPid(int? daemonPid, IReadOnlyDictionary<int, WindowsProcessEntry> table) {
        if (daemonPid is not { } pid || !table.TryGetValue(pid, out var daemon)) return null;
        if (!table.TryGetValue(daemon.ParentPid, out var wrapper) || !IsImage(wrapper, "cmd.exe")) return null;
        if (!table.TryGetValue(wrapper.ParentPid, out var host) || !IsImage(host, "conhost.exe")) return null;
        return pid;
    }

    static bool IsImage(WindowsProcessEntry entry, string exe) => string.Equals(entry.ExeName, exe, StringComparison.OrdinalIgnoreCase);

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking schtasks.</summary>
    internal IReadOnlyList<GeneratedFile> WriteUnitFiles(ServiceSpec spec) {
        var files = GenerateFiles(spec);
        foreach (var f in files) {
            // schtasks /XML wants UTF-16; the .cmd wrapper is fine as UTF-8.
            var encoding = f.Path.EndsWith(".task.xml", StringComparison.Ordinal) ? Encoding.Unicode : Encoding.UTF8;
            _writeUnit(f.Path, f.Content, encoding);
        }
        return files;
    }

    public void Install(ServiceSpec spec, bool startNow) {
        var files = WriteUnitFiles(spec);
        var xmlPath = files.First(f => f.Path.EndsWith(".task.xml", StringComparison.Ordinal)).Path;
        ServiceProcess.Check("schtasks", WindowsTaskUnit.CreateArgs(spec.ServiceId, xmlPath));
        File.Delete(xmlPath); // the task XML is only needed for registration
        if (startNow) ServiceProcess.Check("schtasks", WindowsTaskUnit.RunArgs(spec.ServiceId));
    }

    /// <summary>No distinct verify path for scheduled tasks yet — delegate mechanically to <see cref="Install"/>.</summary>
    public void WriteAndBootstrap(ServiceSpec spec) => Install(spec, startNow: true);

    // Deleting a task leaves its running instance alone, so the wrapper and daemon are ended first — the
    // same "removed means stopped" launchd and systemd give.
    public bool Uninstall(string serviceId, out string? error) {
        ServiceProcess.Run("schtasks", WindowsTaskUnit.EndArgs(serviceId));
        KillDaemon(serviceId);
        ServiceProcess.Run("schtasks", WindowsTaskUnit.DeleteArgs(serviceId));
        var wrapper = WindowsTaskUnit.WrapperPath(config, serviceId);
        if (File.Exists(wrapper)) File.Delete(wrapper);
        error = null;
        return true;
    }

    public bool Start(string serviceId, out string? error) {
        ServiceProcess.Check("schtasks", WindowsTaskUnit.RunArgs(serviceId));
        error = null;
        return true;
    }

    /// `schtasks /End` ends the wrapper, which is what stops the restart loop, but it does not reliably take
    /// the daemon with it: a daemon the wrapper restarted outlives its console host. So the daemon is stopped
    /// by its validated pid once the loop that would restart it is gone.
    public bool Stop(string serviceId, out string? error) {
        ServiceProcess.Check("schtasks", WindowsTaskUnit.EndArgs(serviceId));
        error = KillDaemon(serviceId);
        return error is null;
    }

    string? KillDaemon(string serviceId) {
        if (_daemonPid(serviceId) is not { } pid || pid == Environment.ProcessId) return null;
        try {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return null;
        } catch (ArgumentException) {
            return null; // already gone
        } catch (InvalidOperationException ex) {
            return $"the daemon (PID {pid}) survived the task's end and could not be stopped: {ex.Message}";
        } catch (System.ComponentModel.Win32Exception ex) {
            return $"the daemon (PID {pid}) survived the task's end and could not be stopped: {ex.Message}";
        }
    }
}
