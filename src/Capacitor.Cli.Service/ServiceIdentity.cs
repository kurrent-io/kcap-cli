namespace Capacitor.Cli.Service;

/// <summary>The fixed names the service, its installer and the CLI agree on</summary>
public static class ServiceIdentity {
    public const string ServiceName = "kcap-service";
    public const string DisplayName = "kcap daemon supervisor";
    public const string Description = "Starts and restarts each signed-in user's kcap daemon, as that user";
    public const string PipeName    = "kcap-service";
    public const string ExecutableName = "kcap-service.exe";

    /// <summary>Per user, in the user's own hive</summary>
    public const string EnrollmentKey = @"Software\Kurrent\kcap\Daemon";

    /// <summary>Machine-wide, written by the service only</summary>
    public const string IndexKey = @"SOFTWARE\Kurrent\kcap\Service\Users";

    public static string InstallDirectory(string programFiles) => Path.Combine(programFiles, "kcap", "service");

    public static string LogDirectory(string programData) => Path.Combine(programData, "kcap", "service");

    /// <summary>The job a user's daemon and its agents run in; one name per user</summary>
    public static string JobName(string sid) => $@"Global\kcap-daemon-{sid}";
}
