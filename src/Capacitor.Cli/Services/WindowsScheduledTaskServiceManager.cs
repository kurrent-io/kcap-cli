using System.Diagnostics;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

sealed class WindowsScheduledTaskServiceManager(
        ConfigRoot                                                   config,
        UnitFileWriter?                                              writeUnit    = null,
        Func<string, int?>?                                          daemonPid    = null,
        Func<string[], (int ExitCode, string StdOut, string StdErr)>? schtasks     = null,
        Func<IReadOnlyDictionary<int, WindowsProcessEntry>>?         processTable = null,
        Func<int, string?>?                                          killTree     = null) : IServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));
    readonly Func<string, int?> _daemonPid = daemonPid ?? (id => DaemonPidProbe.ValidatedPid(DaemonStore.FromEnvironment(), id));
    readonly Func<string[], (int ExitCode, string StdOut, string StdErr)> _schtasks = schtasks ?? (args => ServiceProcess.Run("schtasks", args));
    readonly Func<IReadOnlyDictionary<int, WindowsProcessEntry>> _processTable = processTable ?? Snapshot;
    readonly Func<int, string?> _killTree = killTree ?? KillTree;

    static IReadOnlyDictionary<int, WindowsProcessEntry> Snapshot() =>
        OperatingSystem.IsWindows() ? WindowsProcessTable.Snapshot() : new Dictionary<int, WindowsProcessEntry>();

    void CheckSchtasks(string[] args) {
        if (Schtasks(args) is { } error) throw new InvalidOperationException(error);
    }

    string? Schtasks(string[] args) {
        var (code, _, err) = _schtasks(args);
        return code == 0 ? null : $"schtasks {string.Join(' ', args)} failed (exit {code}): {err.Trim()}";
    }

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

    public IReadOnlyList<string> ListInstalled() => TryListInstalled() ?? [];

    // schtasks names a task by its folder path, `\kcap-daemon-x`; the split is explicit because
    // Path.GetFileName only treats a backslash as a separator on Windows.
    static string LeafTaskName(string taskPath) => taskPath[(taskPath.LastIndexOf('\\') + 1)..];

    public IReadOnlyList<string>? TryListInstalled() {
        var (code, stdout, _) = _schtasks(["/Query", "/FO", "LIST"]);
        if (code != 0) return null;
        return [.. stdout.Split('\n')
            .Where(l => l.TrimStart().StartsWith("TaskName:", StringComparison.OrdinalIgnoreCase))
            .Select(l => WindowsTaskUnit.IdFromTaskName(LeafTaskName(l.Split(':', 2)[1].Trim())))
            .Where(id => id is not null).Select(id => id!).Distinct().Order()];
    }

    public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) {
        var (code, _, err) = _schtasks(WindowsTaskUnit.QueryArgs(serviceId));
        if (code != 0 && !ConfirmedAbsent(serviceId))
            throw new InvalidOperationException(
                $"Could not read the state of scheduled task {WindowsTaskUnit.TaskName(serviceId)} (exit {code}): {err.Trim()}");

        var wrapper = WrapperOf(serviceId, taskExists: code == 0);
        return File.Exists(wrapper) ? WindowsTaskUnit.EnvFromWrapper(File.ReadAllText(wrapper)) : null;
    }

    /// <summary>
    /// The wrapper an installed task runs, read from the task's own definition: one installed under another
    /// <c>KCAP_CONFIG_DIR</c> or rooted <c>HOME</c> has it elsewhere, and a file left under this root is not
    /// necessarily the task's. The path under this root stands only for a task that is gone. An installed
    /// task whose definition cannot be read, or whose action no kcap release wrote, throws: without its
    /// wrapper, nothing says which daemon it runs.
    /// </summary>
    string WrapperOf(string serviceId, bool taskExists) {
        var local = WindowsTaskUnit.WrapperPath(config, serviceId);
        if (!taskExists) return local;

        var (code, xml, err) = _schtasks(WindowsTaskUnit.QueryXmlArgs(serviceId));
        if (code != 0)
            throw new InvalidOperationException(
                $"Could not read the definition of scheduled task {WindowsTaskUnit.TaskName(serviceId)} (exit {code}): {err.Trim()}");

        return WindowsTaskUnit.WrapperFromTaskXml(xml)
            ?? throw new InvalidOperationException(
                $"Scheduled task {WindowsTaskUnit.TaskName(serviceId)} runs an action kcap did not write; it is left as it is.");
    }

    /// <summary>A failed query of one task proves nothing about it: only a listing that succeeds and leaves
    /// it out says it is gone.</summary>
    bool ConfirmedAbsent(string serviceId) => TryListInstalled() is { } installed && !installed.Contains(serviceId);

    public ServiceStatus Status(string serviceId) {
        var (code, stdout, err) = _schtasks(WindowsTaskUnit.QueryArgs(serviceId));
        if (code != 0 && !ConfirmedAbsent(serviceId))
            throw new InvalidOperationException(
                $"Could not read the state of scheduled task {WindowsTaskUnit.TaskName(serviceId)} (exit {code}): {err.Trim()}");

        var wrapper = WrapperOf(serviceId, taskExists: code == 0);
        // Report the daemon binary baked inside the wrapper (not the wrapper itself)
        // so doctor catches a moved kcap-daemon.exe even when the wrapper still exists.
        var bin = File.Exists(wrapper) ? WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(wrapper)) : null;
        return new ServiceStatus(WindowsTaskUnit.StatusFromQuery(code, stdout), bin);
    }

    public ServiceQuery Query(string serviceId) {
        var (code, stdout, _) = ServiceProcess.Run("schtasks", WindowsTaskUnit.QueryArgs(serviceId));
        var wrapper = WrapperOf(serviceId, taskExists: code == 0);
        var bin = File.Exists(wrapper) ? WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(wrapper)) : null;
        var state = WindowsTaskUnit.StatusFromQuery(code, stdout);
        var probe = state != ServiceState.NotInstalled ? LabelProbe.Loaded : LabelProbe.Absent;
        var jobPid = state == ServiceState.Running ? TaskOwnedPid(_daemonPid(serviceId), _processTable()) : null;
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

    internal static readonly Encoding WrapperEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking schtasks.</summary>
    internal IReadOnlyList<GeneratedFile> WriteUnitFiles(ServiceSpec spec) {
        var files = GenerateFiles(spec);
        foreach (var f in files) {
            // schtasks /XML wants UTF-16. The .cmd wrapper is UTF-8 without a BOM: cmd.exe reads a BOM as part
            // of the first command, so `@echo off` would fail and the console would echo every line.
            var encoding = f.Path.EndsWith(".task.xml", StringComparison.Ordinal) ? Encoding.Unicode : WrapperEncoding;
            _writeUnit(f.Path, f.Content, encoding);
        }
        return files;
    }

    /// <summary>A refused registration puts the previous wrapper back: an existing task runs whatever its
    /// wrapper names at the next sign-in, so a failed install must not leave it repointed.</summary>
    public bool Install(ServiceSpec spec, bool startNow, out string? error) {
        var wrapperPath = WindowsTaskUnit.WrapperPath(config, spec.ServiceId);
        var previous    = File.Exists(wrapperPath) ? File.ReadAllText(wrapperPath) : null;

        var files = WriteUnitFiles(spec);
        var xmlPath = files.First(f => f.Path.EndsWith(".task.xml", StringComparison.Ordinal)).Path;
        error = Schtasks(WindowsTaskUnit.CreateArgs(spec.ServiceId, xmlPath));
        File.Delete(xmlPath); // the task XML is only needed for registration

        if (error is not null) {
            if (previous is null) File.Delete(wrapperPath);
            else _writeUnit(wrapperPath, previous, WrapperEncoding);
            return false;
        }

        if (startNow) error = Schtasks(WindowsTaskUnit.RunArgs(spec.ServiceId));
        return error is null;
    }

    /// <summary>
    /// Sets one variable in the task's wrapper. The wrapper sets its environment once, ahead of the loop that
    /// relaunches the daemon, and cmd reads a running batch file by byte offset: a running task is therefore
    /// stopped through <paramref name="stopDaemon"/> and ended before the wrapper is rewritten, then run again.
    /// A daemon that does not accept the stop leaves the wrapper as it was.
    /// </summary>
    public UnitRefresh SetEnvironment(string serviceId, string key, string value, Func<bool> stopDaemon, out string? error) {
        error = null;

        var (code, stdout, err) = _schtasks(WindowsTaskUnit.QueryArgs(serviceId));
        if (code != 0) {
            if (ConfirmedAbsent(serviceId)) return UnitRefresh.UnitMissing;
            error = $"Could not read the state of scheduled task {WindowsTaskUnit.TaskName(serviceId)} (exit {code}): {err.Trim()}";
            return UnitRefresh.Failed;
        }

        var wrapper = WrapperOf(serviceId, taskExists: true);
        if (!File.Exists(wrapper)) return UnitRefresh.UnitMissing;

        var previous = File.ReadAllText(wrapper);
        if (WindowsTaskUnit.EnvFromWrapper(previous).TryGetValue(key, out var current) && current == value) return UnitRefresh.Current;

        var rewritten = WindowsTaskUnit.WithEnvironmentValue(previous, key, value)
            ?? throw new InvalidOperationException($"{wrapper} is not a wrapper kcap wrote, so it was left unchanged");

        if (WindowsTaskUnit.StatusFromQuery(code, stdout) != ServiceState.Running) {
            _writeUnit(wrapper, rewritten, WrapperEncoding);
            return UnitRefresh.NotLoaded;
        }

        if (!stopDaemon()) return UnitRefresh.Deferred;

        var (endCode, _, endErr) = _schtasks(WindowsTaskUnit.EndArgs(serviceId));
        if (endCode != 0) {
            var (stateCode, state, _) = _schtasks(WindowsTaskUnit.QueryArgs(serviceId));
            if (stateCode != 0 || WindowsTaskUnit.StatusFromQuery(stateCode, state) == ServiceState.Running) {
                error = $"schtasks /End failed (exit {endCode}): {endErr.Trim()}";
                return UnitRefresh.Failed;
            }
        }

        // A daemon still exiting has accepted the stop and kept its agents for the next one to resume.
        if (_daemonPid(serviceId) is { } pid && pid != Environment.ProcessId && _killTree(pid) is { } killError) {
            error = killError;
            return UnitRefresh.Failed;
        }

        _writeUnit(wrapper, rewritten, WrapperEncoding);

        if (Schtasks(WindowsTaskUnit.RunArgs(serviceId)) is { } runError) {
            error = $"the daemon stopped to take the change but did not start again ({runError}); start it with `kcap daemon service start --name {serviceId}`";
            return UnitRefresh.Failed;
        }

        return UnitRefresh.Reloaded;
    }

    /// <summary>No distinct verify path for scheduled tasks yet — delegate mechanically to <see cref="Install"/>.</summary>
    public void WriteAndBootstrap(ServiceSpec spec) {
        if (!Install(spec, startNow: true, out var error)) throw new InvalidOperationException(error);
    }

    // Deleting a task leaves its running instance alone, so the wrapper and daemon are ended first — the
    // same "removed means stopped" launchd and systemd give. A daemon that survives keeps the task and
    // wrapper, so it is still reported and a retry can stop it.
    public bool Uninstall(string serviceId, out string? error) {
        // Resolved before anything is stopped: a same-named task kcap did not write is left running.
        var query = _schtasks(WindowsTaskUnit.QueryArgs(serviceId));
        var taskExists = query.ExitCode == 0;
        if (!taskExists && !ConfirmedAbsent(serviceId)) {
            error = $"Could not confirm that scheduled task {WindowsTaskUnit.TaskName(serviceId)} is absent (exit {query.ExitCode}): {query.StdErr.Trim()}";
            return false;
        }

        string wrapper;
        try {
            wrapper = WrapperOf(serviceId, taskExists);
        } catch (InvalidOperationException ex) {
            error = ex.Message;
            return false;
        }

        var owned = TaskOwnedDaemon(serviceId);
        var (endCode, _, endErr) = _schtasks(WindowsTaskUnit.EndArgs(serviceId));
        // /End fails for a task that is not running. Going ahead takes a query that succeeds and shows it
        // stopped: one still running, or one that cannot be read, keeps its registration and wrapper.
        if (endCode != 0) {
            var (stateCode, state, _) = _schtasks(WindowsTaskUnit.QueryArgs(serviceId));
            if (stateCode != 0 || WindowsTaskUnit.StatusFromQuery(stateCode, state) == ServiceState.Running) {
                error = $"schtasks /End failed (exit {endCode}): {endErr.Trim()}";
                return false;
            }
        }

        if (owned is { } pid && _killTree(pid) is { } killError) {
            error = killError;
            return false;
        }

        var (code, _, err) = _schtasks(WindowsTaskUnit.DeleteArgs(serviceId));
        // A failed delete counts as done only when a listing shows the task gone; one that cannot be read
        // keeps the wrapper, which the task still needs at the next sign-in.
        if (code != 0 && (TryListInstalled() is not { } listed || listed.Contains(serviceId))) {
            error = $"schtasks /Delete failed (exit {code}): {err.Trim()}";
            return false;
        }

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
    /// by pid once the loop that would restart it is gone.
    public bool Stop(string serviceId, out string? error) {
        var owned = TaskOwnedDaemon(serviceId);
        CheckSchtasks(WindowsTaskUnit.EndArgs(serviceId));
        error = owned is { } pid ? _killTree(pid) : null;
        return error is null;
    }

    /// Read before `/End`: once the task's console host is gone nothing ties the daemon to the task, and a
    /// daemon the user started by hand under the same name must survive a service stop.
    int? TaskOwnedDaemon(string serviceId) =>
        TaskOwnedPid(_daemonPid(serviceId), _processTable()) is { } pid && pid != Environment.ProcessId ? pid : null;

    static string? KillTree(int pid) {
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
