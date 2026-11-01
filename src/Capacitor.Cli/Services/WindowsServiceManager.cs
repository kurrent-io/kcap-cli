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
/// and restored if the service refuses, so the user is never left with no supervisor. <see cref="MoveToTask"/>
/// is the same hand-over the other way.</para>
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
    (string Over, ServiceEnrollment Enrollment)? _displaced;

    public string Describe() => "Windows service kcap-service";

    public string UnitDirectory => config.Directory;

    public bool InstallEndsReplacedDaemon => true;

    public static string EnvironmentFilePath(ConfigRoot config, string id) => config.Path($"daemon-service-{id}.env.json");

    public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) =>
        [new(EnvironmentFilePath(config, spec.ServiceId), JsonSerializer.Serialize(
            new Dictionary<string, string>(spec.Environment), ServicePipeJsonContext.Default.DictionaryStringString))];

    ServiceEnrollment? Enrolled(string serviceId) =>
        enrollments.Read() is { } e && string.Equals(e.Name, serviceId, StringComparison.Ordinal) ? e : null;

    public IReadOnlyList<string> ListInstalled() => enrollments.Read() is { } e ? [e.Name] : [];

    /// <summary>This user's enrollment, under whatever name</summary>
    internal ServiceEnrollment? Enrollment => enrollments.Read();

    /// <summary>Whether the enrollment under <paramref name="serviceId"/> already pins this profile and agent cap,
    /// as an install with these values would write them</summary>
    /// <exception cref="ArgumentException">The agent cap is not one an install would accept</exception>
    internal bool EnrolledAs(string serviceId, string? profile, string? maxAgents) =>
        Enrolled(serviceId) is { } e
        && string.Equals(e.Profile, string.IsNullOrEmpty(profile) ? null : profile, StringComparison.Ordinal)
        && e.MaxAgents == MaxAgents(DaemonServiceCommands.ExtraArgs(maxAgents));

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

    /// <summary>Sets one variable in the enrollment's environment file, which kcap-service reads at each launch of the
    /// daemon. Returns the file as it was, for <see cref="RestoreEnvironment"/>; null when nothing is enrolled under
    /// the name or the file already carries the value.</summary>
    public string? SetEnvironment(string serviceId, string key, string value) {
        if (Enrolled(serviceId)?.EnvironmentFile is not { } file || ReadExisting(file) is not { } previous) return null;

        Dictionary<string, string> env;
        try {
            env = JsonSerializer.Deserialize(previous, ServicePipeJsonContext.Default.DictionaryStringString)
               ?? throw new InvalidOperationException($"{file} holds no environment");
        } catch (JsonException ex) {
            throw new InvalidOperationException($"{file} is not an environment kcap wrote ({ex.Message})");
        }

        if (env.TryGetValue(key, out var current) && current == value) return null;

        env[key] = value;
        _write(file, JsonSerializer.Serialize(env, ServicePipeJsonContext.Default.DictionaryStringString));

        return previous;
    }

    public void RestoreEnvironment(string serviceId, string previous) {
        if (Enrolled(serviceId)?.EnvironmentFile is { } file) _write(file, previous);
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
        var taskRan = task && TaskRunning(spec.ServiceId);

        // Retiring the task alone ends its daemon with schtasks /End, a kill that also ends its agents. Stopped
        // first, it exits 0, which ends the task's loop, and the service's daemon resumes the kept agents. One
        // that does not stop is left to its task, since retiring it would be that kill.
        if (task && !_stopDaemon(spec.ServiceId)) {
            error = $"the daemon '{spec.ServiceId}' did not stop gracefully, so its Scheduled Task was left running it";
            return false;
        }

        // The task's daemon holds the name; the service's launch would only be refused while it runs. A retire
        // can fail after it has already ended that daemon (a task the user may not delete), so the task is
        // started again rather than left registered with nothing running.
        if (task && !tasks!.Uninstall(spec.ServiceId, out var taskError)) {
            error = $"could not retire the Scheduled Task for '{spec.ServiceId}': {taskError}";
            if (taskRan && !tasks.Start(spec.ServiceId, out var startError)) error += $"; starting it again also failed: {startError}";
            return false;
        }

        var file = GenerateFiles(spec)[0];
        var previousEnvironment = ReadExisting(file.Path);
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
            // A same-name enrollment points at this same file, so it gets its own contents back too
            if (previousEnvironment is not null) _write(file.Path, previousEnvironment);
            else File.Delete(file.Path);
            if (task && !tasks!.Install(spec, taskRan, out var restoreError))
                error += $"; restoring the Scheduled Task also failed: {restoreError}";
            return false;
        }

        // One enrollment per user, so a new name displaces the old one; uninstalling the new name puts it back
        _displaced = previous is not null && !string.Equals(previous.Name, spec.ServiceId, StringComparison.Ordinal)
            ? (spec.ServiceId, previous)
            : null;

        if (!startNow && pipe.Send("stop", Slow) is not { Ok: true }) {
            // Enrolled either way; what is left to say is whether its daemon runs
            var running = pipe.Send("status", Quick)?.Status is not { Running: false };
            error = running
                ? $"kcap-service enrolled '{spec.ServiceId}' and its daemon is running: stopping it failed. Stop it with `kcap daemon service stop --name {spec.ServiceId}`"
                : null;
            return !running;
        }

        error = null;
        return true;
    }

    bool TaskRunning(string serviceId) {
        try {
            return tasks!.Query(serviceId).State == ServiceState.Running;
        } catch (InvalidOperationException) {
            return true;
        }
    }

    static string? ReadExisting(string path) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return null;
        }
    }

    /// <summary>True once no daemon runs under the name: it stopped keeping its agents, or none was running</summary>
    static bool StopKeepingAgents(string name) {
        var store = DaemonStore.FromEnvironment();
        try {
            var outcome = DaemonShutdownClient.StopAsync(
                store, name, new ShutdownRequestDto("keep", "final"), DaemonCommands.StopExitWait,
                TimeProvider.System, CancellationToken.None).GetAwaiter().GetResult();
            if (outcome == ShutdownOutcome.Exited) return true;
        } catch (Exception ex) when (ex is SocketException or IOException) { }
        return DaemonPidProbe.ValidatedPid(store, name) is null;
    }

    public void WriteAndBootstrap(ServiceSpec spec) {
        if (!Install(spec, startNow: true, out var error)) throw new InvalidOperationException(error);
    }

    public bool Uninstall(string serviceId, out string? error) {
        if (Enrolled(serviceId) is not { } enrollment) {
            // A name a rename displaced keeps only the environment file it was installed with
            try { File.Delete(EnvironmentFilePath(config, serviceId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            error = null;
            return true;
        }

        // Re-enrolling replaces the daemon in place: an unenroll first could leave the service with no user and
        // so remove it before the displaced enrollment is back
        if (_displaced is { } displaced && displaced.Over == serviceId
            && (displaced.Enrollment.EnvironmentFile is not { } kept || File.Exists(kept))) {
            _displaced = null;
            enrollments.Write(displaced.Enrollment);
            if (pipe.Send("enroll", Slow) is not { Ok: true }) {
                enrollments.Write(enrollment);
                error = $"kcap-service did not take back '{displaced.Enrollment.Name}'";
                return false;
            }
            if (enrollment.EnvironmentFile is { } own) File.Delete(own);
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

    /// <summary>
    /// Hands the enrolled daemon back to a Scheduled Task written from <paramref name="spec"/>: stopped keeping its
    /// agents, so the task's daemon resumes them, then unenrolled, then given its task, started only when the
    /// service was running it. A step that fails gives the daemon back to the service, so it always has exactly one
    /// supervisor.
    /// </summary>
    /// <param name="started">Whether the task was started</param>
    /// <param name="error">Why the move failed; after a move, why a task that should have started did not</param>
    public bool MoveToTask(ServiceSpec spec, out bool started, out string? error) {
        var id = spec.ServiceId;
        started = false;
        if (tasks is not { } taskManager || Enrolled(id) is not { } enrollment) {
            error = $"'{id}' is not enrolled with kcap-service";
            return false;
        }

        if (pipe.Send("status", Quick)?.Status is not { } status) {
            error = "kcap-service did not answer";
            return false;
        }

        // Unenrolling alone has the service stop it with its agents ended
        if (status.Running && !_stopDaemon(id)) {
            error = $"the daemon '{id}' did not stop gracefully, so kcap-service was left running it";
            return false;
        }

        var environment = enrollment.EnvironmentFile is { } file ? ReadExisting(file) : null;
        if (!Uninstall(id, out var unenrollError)) {
            error = unenrollError;
            if (status.Running && !Start(id, out var startError)) error += $"; starting it again also failed: {startError}";
            return false;
        }

        // Registered stopped, so a failed start below cannot read as a failed registration and put a second
        // supervisor on the name
        if (RegisterTask(taskManager, spec) is { } taskError) {
            error = $"could not install its Scheduled Task: {taskError}{Reenroll(enrollment, environment, status.Running)}";
            return false;
        }

        error = null;
        if (!status.Running) return true;
        try {
            started = taskManager.Start(id, out error);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            error = ex.Message;
        }
        return true;
    }

    static string? RegisterTask(IServiceManager taskManager, ServiceSpec spec) {
        try {
            return taskManager.Install(spec, startNow: false, out var error) ? null : error ?? "schtasks refused it";
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) {
            return ex.Message;
        }
    }

    /// <summary>Empty once the service has the daemon back; otherwise what went wrong, to append to the error</summary>
    string Reenroll(ServiceEnrollment enrollment, string? environment, bool running) {
        if (enrollment.EnvironmentFile is { } file && environment is not null) _write(file, environment);
        enrollments.Write(enrollment);
        if (pipe.Send("enroll", Slow) is not { Ok: true }) return "; enrolling it with kcap-service again also failed";
        if (!running) pipe.Send("stop", Slow);
        return "";
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
