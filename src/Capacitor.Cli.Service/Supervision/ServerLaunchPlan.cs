using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Supervision;

/// <summary>What one launch of a user's local edition server runs, worked out before anything touches Windows</summary>
public sealed record ServerLaunchPlan(string CommandLine, IReadOnlyDictionary<string, string> Environment, string WorkingDirectory, string LogPath) {
    /// <param name="captured">The environment the CLI wrote at enrollment, laid over the logon one</param>
    public static ServerLaunchPlan For(
            ServerEnrollment enrollment, IReadOnlyDictionary<string, string> userEnvironment,
            IReadOnlyDictionary<string, string>? captured = null) =>
        new($"\"{enrollment.ServerPath}\"", UserEnvironment.Overlay(userEnvironment, captured),
            Path.GetDirectoryName(enrollment.ServerPath)!, enrollment.LogFile);

    /// <summary>The user's own CLI sending the server Ctrl+Break, the stop it shuts down cleanly on</summary>
    public static string StopCommandLine(ServerEnrollment enrollment, int pid) =>
        $"\"{enrollment.CliPath}\" local console-break {pid}";

    public string EnvironmentBlock() => UserEnvironment.Block(Environment);
}
