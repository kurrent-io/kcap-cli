using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// The local server supervised by <c>kcap-service</c>. The enrollment in the user's own hive names the server, its
/// environment file and its log; the service launches it with the user's token, relaunches it, and stops it with a
/// console break sent through this CLI. <see cref="LocalServerEnrollment.Stopped"/> is the persisted stop.
///
/// <para>Without an enrollment, a <c>kcap-local</c> task the user still has supervises the server, and every call but
/// <see cref="Install(LocalServerUnit)"/> goes to it: two supervisors must never share one root. Installing retires
/// that task; a service that refuses the enrollment leaves the server to a new task instead.</para>
/// </summary>
sealed class LocalServerWindowsService(
    UserHome home,
    TimeProvider time,
    IServicePipe pipe,
    ILocalServerEnrollmentStore enrollments,
    ILocalServerService task,
    UnitFileWriter? writeFile = null,
    Func<LocalRoot, string?>? findLive = null,
    string? cliPath = null,
    Action<string>? say = null
) : ILocalServerService {
    public const string ServiceName = "kcap-service";

    static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    static readonly TimeSpan Launch = TimeSpan.FromSeconds(30);

    /// <summary>Above the service's own graceful wait for the server, so a stop it ends by force still answers.</summary>
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(130);

    /// <summary>Once the service answers, time for Postgres to let go of its socket and pid file.</summary>
    static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(90);

    readonly UnitFileWriter _write = writeFile ?? ((path, content, encoding) => ServiceFiles.WriteOwnerOnly(path, content, encoding, durable: true, home: home));
    readonly Action<string> _say = say ?? (line => Console.Error.WriteLine(line));

    /// <summary>Beside the unit lock and the task's wrapper: the server is one per user, whatever the config root.</summary>
    public static string EnvironmentFilePath(UserHome home) => Path.Combine(home.Path, ".config", "kcap", "kcap-local.env.json");

    string EnvironmentFile => EnvironmentFilePath(home);

    bool Enrolled => enrollments.Read() is not null;

    bool TaskLeft(LocalServerUnit unit) => !Enrolled && File.Exists(task.UnitFile(unit));

    public string Describe =>
        !Enrolled && File.Exists(LocalServerTaskUnit.WrapperPath(home, LocalServerUnit.ProductionTaskName)) ? task.Describe : "Windows service";

    public string Name(LocalServerUnit unit) => TaskLeft(unit) ? task.Name(unit) : ServiceName;

    public string UnitFile(LocalServerUnit unit) => TaskLeft(unit) ? task.UnitFile(unit) : EnvironmentFile;

    public bool IsInstalled(LocalServerUnit unit) => Enrolled || (File.Exists(task.UnitFile(unit)) && task.IsInstalled(unit));

    public UnitOwner Owner(LocalServerUnit unit) {
        if (enrollments.Read() is { } enrollment) return new(new OwnerReading.Root(enrollment.Root), FileOwner());

        return File.Exists(task.UnitFile(unit)) ? task.Owner(unit) : new(OwnerReading.Absent.Instance, FileOwner());
    }

    OwnerReading FileOwner() {
        Dictionary<string, string>? environment;

        try {
            using var stream = new FileStream(EnvironmentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            environment = JsonSerializer.Deserialize(stream, ServicePipeJsonContext.Default.DictionaryStringString);
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return OwnerReading.Absent.Instance;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return new OwnerReading.Unreadable($"{EnvironmentFile} could not be read");
        }

        return environment is not null && environment.TryGetValue(LocalRoot.EnvVar, out var root)
            ? new OwnerReading.Root(root)
            : new OwnerReading.Unreadable($"{EnvironmentFile} names no {LocalRoot.EnvVar}");
    }

    public UnitActivity Activity(LocalServerUnit unit) {
        if (!Enrolled) return File.Exists(task.UnitFile(unit)) ? task.Activity(unit) : UnitActivity.Stopped.Instance;

        return pipe.Send(ServicePipeVerbs.ServerStatus, Quick)?.Status is { } status
            ? Read(status)
            : new UnitActivity.Unknown("kcap-service is not answering on its pipe");
    }

    /// <summary>The service's state words are shared with the daemon's and only ever added to; one this CLI does not
    /// know is unknown, never running.</summary>
    internal static UnitActivity Read(ServicePipeStatus status) =>
        status.State switch {
            "running" or "stopping" when status.Pid is { } pid => new UnitActivity.Running(pid) { Starts = status.Launches },
            "relaunching" => new UnitActivity.Restarting(status.LastExit) { Starts = status.Launches },
            "failed" => new UnitActivity.Exited(status.LastExit) { Starts = status.Launches },
            "running" => new UnitActivity.Unknown("kcap-service reports the local server running but names no process"),
            "stopped" or "stopping" or "session_ended" or "not_enrolled" or "no_session" => UnitActivity.Stopped.Instance,
            var other => new UnitActivity.Unknown(status.Error ?? $"kcap-service reports the local server as '{other}'"),
        };

    public void Install(LocalServerUnit unit) => Install(unit, start: true);

    /// <summary>With <paramref name="start"/> false the enrollment is written stopped, so a server moved here while
    /// stopped stays stopped.</summary>
    public void Install(LocalServerUnit unit, bool start) {
        unit.PrepareLogDirectory();

        var cli = cliPath ?? Environment.ProcessPath
            ?? throw new LocalLifecycleException("The kcap executable's path is unknown, so kcap-service could not be told how to stop the server.");

        if (File.Exists(task.UnitFile(unit))) task.UninstallAsync(unit, CancellationToken.None).GetAwaiter().GetResult();

        var previous = enrollments.Read();

        // The service reads the environment only at a launch, so a server it runs would keep the old one.
        if (previous is not null && Activity(unit) is not UnitActivity.Stopped) StopAsync(unit, CancellationToken.None).GetAwaiter().GetResult();

        _write(EnvironmentFile, JsonSerializer.Serialize(unit.Environment.ToDictionary(), ServicePipeJsonContext.Default.DictionaryStringString));
        enrollments.Write(new LocalServerEnrollment(unit.ServerBinary, unit.Root.Directory, cli, EnvironmentFile, unit.OutputLog, Stopped: !start));

        var enrolled = pipe.Send(ServicePipeVerbs.ServerEnroll, Slow);
        if (enrolled is { Ok: true }) return;

        // A service still holding the earlier enrollment would launch the server beside the task.
        if (previous is not null) pipe.Send(ServicePipeVerbs.ServerUnenroll, Slow);

        enrollments.Delete();
        DeleteEnvironmentFile();

        _say($"kcap-service did not take the local server ({enrolled?.Error ?? "it did not answer"}); "
           + $"it runs as the Scheduled Task {task.Name(unit)} instead, while you are signed in.");

        task.Install(unit);
        if (!start) task.StopAsync(unit, CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Start(LocalServerUnit unit) {
        if (!Enrolled) {
            if (!File.Exists(task.UnitFile(unit)))
                throw new LocalLifecycleException("The local server is not enrolled with kcap-service. Run kcap setup --local to install it.");

            task.Start(unit);
            return;
        }

        enrollments.SetStopped(false);

        var response = pipe.Send(ServicePipeVerbs.ServerStart, Launch);
        if (response is not { Ok: true })
            throw new LocalLifecycleException($"kcap-service did not start the local server: {response?.Error ?? "it did not answer"}.");
    }

    public async Task StopAsync(LocalServerUnit unit, CancellationToken ct) {
        if (!Enrolled) {
            if (File.Exists(task.UnitFile(unit))) await task.StopAsync(unit, ct);
            else await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);

            return;
        }

        enrollments.SetStopped(true);
        AskToStop(ServicePipeVerbs.ServerStop);

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);
    }

    public async Task UninstallAsync(LocalServerUnit unit, CancellationToken ct) {
        if (!Enrolled) {
            if (File.Exists(task.UnitFile(unit))) await task.UninstallAsync(unit, ct);
            else await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);

            DeleteEnvironmentFile();
            return;
        }

        enrollments.SetStopped(true);
        AskToStop(ServicePipeVerbs.ServerUnenroll);

        await LocalStackProbe.WaitUntilDownAsync(unit.Root, StopBudget, time, ct, findLive);

        enrollments.Delete();
        DeleteEnvironmentFile();
    }

    /// <summary>The service stops the server before it answers an unenroll; past the grace it carries on with nobody
    /// waiting, and the enrollment is already gone, so nothing launches the server again.</summary>
    public async Task UninstallWithinAsync(LocalServerUnit unit, TimeSpan grace, CancellationToken ct) {
        if (!Enrolled) {
            if (File.Exists(task.UnitFile(unit))) await task.UninstallWithinAsync(unit, grace, ct);

            DeleteEnvironmentFile();
            return;
        }

        enrollments.SetStopped(true);
        pipe.Send(ServicePipeVerbs.ServerUnenroll, grace);

        enrollments.Delete();
        DeleteEnvironmentFile();
    }

    /// <summary>A server the service does not run is already stopped; the stack probe that follows checks it.</summary>
    void AskToStop(string verb) {
        var response = pipe.Send(verb, Slow);
        if (response is { Ok: true } || response?.Status?.State is "stopped" or "session_ended" or "not_enrolled" or "no_session") return;

        throw new LocalLifecycleException(response is null
            ? "kcap-service did not answer, so the local server could not be stopped."
            : $"kcap-service could not stop the local server: {response.Error ?? response.Status?.State}.");
    }

    void DeleteEnvironmentFile() {
        if (File.Exists(EnvironmentFile)) File.Delete(EnvironmentFile);
    }
}
