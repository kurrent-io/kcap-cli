using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

sealed class SystemdServiceManager(
    UserHome home,
    UnitFileWriter? writeUnit = null,
    Func<string, string[], (int ExitCode, string StdOut, string StdErr)>? runProcess = null
) : IServiceManager {
    readonly UnitFileWriter _writeUnit = writeUnit ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));
    readonly Func<string, string[], (int ExitCode, string StdOut, string StdErr)> _runProcess = runProcess ?? ServiceProcess.Run;

    public string Describe() => "systemd --user unit";

    public string UnitDirectory => SystemdUnit.UserUnitDir(home);

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new GeneratedFile(SystemdUnit.UnitPath(home, spec.ServiceId), SystemdUnit.Unit(spec))];

    public IReadOnlyList<string> ListInstalled() {
        var dir = SystemdUnit.UserUnitDir(home);
        if (!Directory.Exists(dir)) return [];
        return [.. Directory.EnumerateFiles(dir, "kcap-daemon-*.service")
            .Select(f => SystemdUnit.IdFromUnitFileName(Path.GetFileName(f)))
            .Where(id => id is not null).Select(id => id!).Order()];
    }

    public ServiceStatus Status(string serviceId) {
        var query = Query(serviceId);
        return query.UnitPresent ? new ServiceStatus(query.State, query.BinaryPath) : new ServiceStatus(ServiceState.NotInstalled, null);
    }

    public string? UnitBinary(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        try {
            return File.Exists(path) ? SystemdUnit.BinaryFromUnit(File.ReadAllText(path)) : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        return File.Exists(path) ? SystemdUnit.EnvFromUnit(File.ReadAllText(path)) : null;
    }

    public ServiceQuery Query(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        var unitPresent = File.Exists(path);
        // Read from the file, so it stays known when systemd does not answer: `uninstall --under` decides
        // which units it owns from this.
        var bin = unitPresent ? SystemdUnit.BinaryFromUnit(File.ReadAllText(path)) : null;
        var (_, active, _)      = _runProcess("systemctl", SystemdUnit.IsActiveArgs(serviceId));
        // is-active prints a state whenever systemd answers. Nothing means the user bus is unreachable
        // (an SSH login without lingering, a container): reading that as Absent would free a live unit.
        if (string.IsNullOrWhiteSpace(active)) return new ServiceQuery(LabelProbe.Unknown, unitPresent, ServiceState.NotInstalled, bin, null);

        var (enabledExit, enabledOut, _) = _runProcess("systemctl", SystemdUnit.IsEnabledArgs(serviceId));
        var state = SystemdUnit.StatusFrom(active, enabledExit, enabledOut, unitPresent);
        var probe = state != ServiceState.NotInstalled ? LabelProbe.Loaded : LabelProbe.Absent;
        var jobPid = state == ServiceState.Running
            ? SystemdUnit.MainPid(_runProcess("systemctl", SystemdUnit.MainPidArgs(serviceId)).StdOut)
            : null;
        return new ServiceQuery(probe, unitPresent, state, bin, jobPid);
    }

    /// <summary>The unit-writing half of <see cref="Install"/>, split out so it is testable without
    /// invoking systemctl.</summary>
    internal void WriteUnitFiles(ServiceSpec spec) =>
        _writeUnit(SystemdUnit.UnitPath(home, spec.ServiceId), SystemdUnit.Unit(spec), null);

    /// <summary>
    /// Points an installed unit's binary at what <paramref name="stabilize"/> maps it to, and reloads
    /// systemd's copy of the unit. The running daemon is left alone: it picks the new binary up on its
    /// next start. True when the unit was rewritten or a reload it still needed was done.
    ///
    /// <para>A unit already on the stable path is reloaded when systemd reports it stale, so a reload that
    /// failed after the rewrite is retried by the next refresh. Throws when <c>daemon-reload</c> fails.</para>
    /// </summary>
    public bool Repoint(string serviceId, Func<string, string> stabilize) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        if (!File.Exists(path)) return false;

        var text   = File.ReadAllText(path);
        var binary = SystemdUnit.BinaryFromUnit(text);
        if (binary is null) return false;

        if (SystemdUnit.WithBinary(text, stabilize(binary)) is { } rewritten) {
            _writeUnit(path, rewritten, null);
            Reload();
            return true;
        }

        var (_, stale, _) = _runProcess("systemctl", SystemdUnit.NeedDaemonReloadArgs(serviceId));
        if (!SystemdUnit.NeedsDaemonReload(stale)) return false;

        Reload();
        return true;
    }

    /// <summary>Sets one variable in the unit and reloads systemd's copy; the daemon reads it on its next
    /// start. Returns the unit as it was, for <see cref="RestoreUnit"/>; null when the unit is absent or
    /// already carries the value. A line it cannot rewrite safely throws, and a refused reload puts the
    /// previous unit back before throwing.</summary>
    public string? SetEnvironment(string serviceId, string key, string value) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        if (!File.Exists(path)) return null;

        var previous = File.ReadAllText(path);
        if (SystemdUnit.EnvFromUnit(previous).TryGetValue(key, out var current) && current == value) return null;
        if (SystemdUnit.WithEnvironmentValue(previous, key, value) is not { } rewritten)
            throw new InvalidOperationException($"its {key} line is not one kcap wrote, so it was left unchanged");

        _writeUnit(path, rewritten, null);
        try {
            Reload();
        } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                          or IOException or UnauthorizedAccessException) {
            _writeUnit(path, previous, null);
            try {
                Systemctl(SystemdUnit.DaemonReloadArgs());
            } catch (Exception again) when (again is System.ComponentModel.Win32Exception or IOException) {
                // The reload that failed is the one to report.
            }
            throw;
        }

        return previous;
    }

    public void RestoreUnit(string serviceId, string unitText) {
        _writeUnit(SystemdUnit.UnitPath(home, serviceId), unitText, null);
        Reload();
    }

    void Reload() {
        if (Systemctl(SystemdUnit.DaemonReloadArgs()) is { } error) throw new InvalidOperationException(error);
    }

    /// <summary>A refused reload or enable puts the previous unit file back, so a failed install leaves
    /// the unit as it was. A refused restart does not: the unit is installed, only not started.</summary>
    public bool Install(ServiceSpec spec, bool startNow, out string? error) {
        var path     = SystemdUnit.UnitPath(home, spec.ServiceId);
        var previous = File.Exists(path) ? File.ReadAllText(path) : null;

        WriteUnitFiles(spec);
        error = Systemctl(SystemdUnit.DaemonReloadArgs()) ?? Systemctl(SystemdUnit.EnableArgs(spec.ServiceId));
        if (error is not null) {
            if (previous is null) File.Delete(path);
            else _writeUnit(path, previous, null);
            if (Systemctl(SystemdUnit.DaemonReloadArgs()) is { } reloadError)
                error += $"; the previous unit file was put back, but {reloadError}, so systemd may still hold the new "
                       + "definition until the next `systemctl --user daemon-reload`";
            return false;
        }

        error = startNow ? Systemctl(SystemdUnit.RestartArgs(spec.ServiceId)) : null;
        return error is null;
    }

    string? Systemctl(string[] args) {
        var (code, _, err) = _runProcess("systemctl", args);
        return code == 0 ? null : $"systemctl {string.Join(' ', args)} failed (exit {code}): {err.Trim()}";
    }

    public void WriteAndBootstrap(ServiceSpec spec) {
        if (!Install(spec, startNow: true, out var error)) throw new InvalidOperationException(error);
    }

    public bool Uninstall(string serviceId, out string? error) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        if (Systemctl(SystemdUnit.DisableNowArgs(serviceId)) is { } disableError
            && _runProcess("systemctl", SystemdUnit.IsActiveArgs(serviceId)).StdOut.Trim() is not ("inactive" or "failed")) {
            error = $"{disableError} — unit file retained: {path}";
            return false;
        }
        if (File.Exists(path)) File.Delete(path);
        _runProcess("systemctl", SystemdUnit.DaemonReloadArgs());
        error = null;
        return true;
    }

    public bool Start(string serviceId, out string? error) {
        error = Systemctl(SystemdUnit.StartArgs(serviceId));
        return error is null;
    }

    public bool Stop(string serviceId, out string? error) {
        error = Systemctl(SystemdUnit.StopArgs(serviceId));
        return error is null;
    }
}
