using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

/// <summary>
/// The daemon supervised by <c>kcap-service</c>, the machine's Windows service. Installing writes the
/// user's enrollment to their own hive plus the captured environment beside the config, then asks the
/// service to take it; the service is what starts, restarts and stops the daemon. One daemon per user:
/// installing another name replaces the enrollment.
///
/// <para>A Scheduled Task left for the same name is retired once the service's daemon has taken over,
/// and restored if the service refuses, so the user is never left with no supervisor.</para>
/// </summary>
sealed class WindowsServiceManager(
        ConfigRoot config, IServicePipe pipe, IServiceEnrollmentStore enrollments, IServiceManager? tasks,
        Func<string, bool>? stopDaemon = null,
        UnitFileWriter? writeFile = null) : IServiceManager {
    static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    /// <summary>Above the service's own graceful-stop wait, so a stop it ends by force still answers</summary>
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(30);

    readonly UnitFileWriter _write = writeFile ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding));
    readonly Func<string, bool> _stopDaemon = stopDaemon ?? StopKeepingAgents;

    public string Describe() => "Windows service kcap-service";

    public string UnitDirectory => config.Directory;

    public static string EnvironmentFilePath(ConfigRoot config, string id) => config.Path($"daemon-service-{id}.env.json");

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new(EnvironmentFilePath(config, spec.ServiceId), JsonSerializer.Serialize(
            new Dictionary<string, string>(spec.Environment), ServicePipeJsonContext.Default.DictionaryStringString))];

    ServiceEnrollment? Enrolled(string serviceId) =>
        enrollments.Read() is { } e && string.Equals(e.Name, serviceId, StringComparison.Ordinal) ? e : null;

    public IReadOnlyList<string> ListInstalled() => enrollments.Read() is { } e ? [e.Name] : [];

    public ServiceStatus Status(string serviceId) {
        var query = Query(serviceId);
        if (query.Probe == LabelProbe.Unknown) throw new InvalidOperationException("kcap-service is not answering on its pipe");
        return new ServiceStatus(query.State, query.BinaryPath);
    }

    public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) {
        if (Enrolled(serviceId)?.EnvironmentFile is not { } file || !File.Exists(file)) return null;
        try {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return JsonSerializer.Deserialize(stream, ServicePipeJsonContext.Default.DictionaryStringString);
        } catch (JsonException) {
            return null;
        }
    }

    public ServiceQuery Query(string serviceId) {
        if (Enrolled(serviceId) is not { } enrollment) return new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);

        if (pipe.Send("status", Quick)?.Status is not { } status)
            return new ServiceQuery(LabelProbe.Unknown, true, ServiceState.Installed, enrollment.DaemonPath, null);

        return status.Running
            ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, enrollment.DaemonPath, status.Pid)
            : new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Installed, enrollment.DaemonPath, null);
    }

    public bool Install(ServiceSpec spec, bool startNow, out string? error) {
        var previous = enrollments.Read();
        var task = tasks?.TryListInstalled()?.Contains(spec.ServiceId) == true;

        // Retiring the task alone ends its daemon with schtasks /End, a kill that also ends its agents. Stopped
        // first, it exits 0, which ends the task's loop, and the service's daemon resumes the kept agents.
        if (task) _stopDaemon(spec.ServiceId);

        // The task's daemon holds the name; the service's launch would only be refused while it runs. A retire
        // can fail after it has already ended that daemon (a task the user may not delete), so the task is
        // started again rather than left registered with nothing running.
        if (task && !tasks!.Uninstall(spec.ServiceId, out var taskError)) {
            error = $"could not retire the Scheduled Task for '{spec.ServiceId}': {taskError}";
            if (!tasks.Start(spec.ServiceId, out var startError)) error += $"; starting it again also failed: {startError}";
            return false;
        }

        var file = GenerateFiles(spec)[0];
        _write(file.Path, file.Content);
        enrollments.Write(new ServiceEnrollment(
            spec.ServiceId, spec.DaemonBinaryPath,
            spec.Environment.TryGetValue("KCAP_PROFILE", out var profile) && profile.Length > 0 ? profile : null,
            MaxAgents(spec.ExtraArgs), file.Path));

        var enrolled = pipe.Send("enroll", Slow);
        if (enrolled is not { Ok: true }) {
            error = enrolled?.Error ?? "kcap-service did not answer";
            if (previous is null) enrollments.Delete();
            else enrollments.Write(previous);
            if (task && !tasks!.Install(spec, startNow, out var restoreError))
                error += $"; restoring the Scheduled Task also failed: {restoreError}";
            return false;
        }

        if (!startNow && pipe.Send("stop", Slow) is not { Ok: true }) {
            error = "kcap-service enrolled the daemon but did not stop it";
            return false;
        }

        error = null;
        return true;
    }

    static bool StopKeepingAgents(string name) {
        try {
            return DaemonShutdownClient.StopAndWaitAsync(
                DaemonStore.FromEnvironment(), name, new ShutdownRequestDto("keep", "final"), DaemonCommands.StopExitWait,
                TimeProvider.System, CancellationToken.None).GetAwaiter().GetResult();
        } catch (Exception ex) when (ex is SocketException or IOException) {
            return false;
        }
    }

    public void WriteAndBootstrap(ServiceSpec spec) {
        if (!Install(spec, startNow: true, out var error)) throw new InvalidOperationException(error);
    }

    public bool Uninstall(string serviceId, out string? error) {
        if (Enrolled(serviceId) is not { } enrollment) {
            error = null;
            return true;
        }

        var response = pipe.Send("unenroll", Slow);
        if (response is not { Ok: true } && response?.Status?.State is not ("stopped" or "no_session")) {
            error = response?.Error ?? "kcap-service did not answer";
            return false;
        }

        enrollments.Delete();
        if (enrollment.EnvironmentFile is { } file) File.Delete(file);
        error = null;
        return true;
    }

    public bool Start(string serviceId, out string? error) => Ask(serviceId, "start", Quick, out error);

    public bool Stop(string serviceId, out string? error) => Ask(serviceId, "stop", Slow, out error);

    bool Ask(string serviceId, string verb, TimeSpan timeout, out string? error) {
        if (Enrolled(serviceId) is null) {
            error = $"'{serviceId}' is not enrolled with kcap-service";
            return false;
        }
        var response = pipe.Send(verb, timeout);
        error = response is { Ok: true } ? null : response?.Error ?? "kcap-service did not answer";
        return error is null;
    }

    static int? MaxAgents(IReadOnlyList<string> extraArgs) {
        var at = extraArgs.ToList().IndexOf("--max-agents");
        return at >= 0 && at + 1 < extraArgs.Count && int.TryParse(extraArgs[at + 1], out var n) ? n : null;
    }
}
