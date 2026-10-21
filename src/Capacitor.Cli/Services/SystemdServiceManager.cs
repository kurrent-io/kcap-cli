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
        var path = SystemdUnit.UnitPath(home, serviceId);
        if (!File.Exists(path)) return new ServiceStatus(ServiceState.NotInstalled, null);
        var (_, active, _)      = ServiceProcess.Run("systemctl", SystemdUnit.IsActiveArgs(serviceId));
        var (enabledExit, _, _) = ServiceProcess.Run("systemctl", SystemdUnit.IsEnabledArgs(serviceId));
        var bin = SystemdUnit.BinaryFromUnit(File.ReadAllText(path)); // quote-aware ExecStart parse
        return new ServiceStatus(SystemdUnit.StatusFrom(active, enabledExit), bin);
    }

    public string? UnitBinary(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        try {
            return File.Exists(path) ? SystemdUnit.BinaryFromUnit(File.ReadAllText(path)) : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public ServiceQuery Query(string serviceId) {
        var path = SystemdUnit.UnitPath(home, serviceId);
        var unitPresent = File.Exists(path);
        var bin = unitPresent ? SystemdUnit.BinaryFromUnit(File.ReadAllText(path)) : null;
        var (exit, output, _) = _runProcess("systemctl", SystemdUnit.ShowStateArgs(serviceId));
        return SystemdUnit.QueryFromShow(exit, output, unitPresent, bin);
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

    void Reload() {
        var (exit, _, err) = _runProcess("systemctl", SystemdUnit.DaemonReloadArgs());
        if (exit != 0)
            throw new InvalidOperationException($"systemctl --user daemon-reload exited {exit}{(string.IsNullOrWhiteSpace(err) ? "" : $": {err.Trim()}")}");
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

    /// <summary>A failed disable keeps the unit unless a query proves its daemon stopped, so a rollback never
    /// reports a removal that left the process behind.</summary>
    public bool Uninstall(string serviceId, out string? error) {
        if (Systemctl(SystemdUnit.DisableNowArgs(serviceId)) is { } disableError
         && Query(serviceId) is not { Probe: LabelProbe.Loaded or LabelProbe.Absent, State: not ServiceState.Running }) {
            error = $"{disableError}; the daemon may still be running, so its unit was kept";
            return false;
        }

        var path = SystemdUnit.UnitPath(home, serviceId);
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            error = $"could not remove {path}: {ex.Message}";
            return false;
        }

        Systemctl(SystemdUnit.DaemonReloadArgs());
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
