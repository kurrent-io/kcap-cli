using Capacitor.Cli.Service.Sessions;

namespace Capacitor.Cli.Service.Supervision;

/// <summary>What one launch of a user's daemon runs, worked out from their environment and enrollment
/// before anything touches Windows</summary>
public sealed record DaemonLaunchPlan(string CommandLine, IReadOnlyDictionary<string, string> Environment, string LogPath, string StderrPath) {
    /// <summary>Names the daemon as supervised; the daemon honours it only when it equals its own name</summary>
    public const string SupervisedVariable = "KCAP_DAEMON_SUPERVISED";

    /// <param name="captured">The shell environment the CLI captured at install, laid over the logon one</param>
    public static DaemonLaunchPlan For(
            Enrollment enrollment, string profileDirectory, IReadOnlyDictionary<string, string> userEnvironment,
            IReadOnlyDictionary<string, string>? captured = null) {
        var env = new Dictionary<string, string>(userEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in captured ?? new Dictionary<string, string>()) env[key] = value;
        env[SupervisedVariable] = enrollment.Name;
        if (enrollment.Profile is { } profile) env["KCAP_PROFILE"] = profile;

        var configDir = env.TryGetValue("KCAP_CONFIG_DIR", out var custom) && custom.Length > 0
            ? custom
            : Path.Combine(profileDirectory, ".config", "kcap");
        var log    = Path.Combine(configDir, $"daemon-{enrollment.Name}.log");
        var stderr = Path.Combine(configDir, $"daemon-{enrollment.Name}.stderr.log");

        var command = $"\"{enrollment.DaemonPath}\" --name \"{enrollment.Name}\" --log-file \"{log}\"";
        if (enrollment.MaxAgents is { } max) command += $" --max-agents {max}";

        return new DaemonLaunchPlan(command, env, log, stderr);
    }

    /// <summary>The user's own CLI asking their daemon to stop, bypassing the refusal it gives a managed daemon</summary>
    public static string StopCommandLine(Enrollment enrollment) =>
        $"\"{enrollment.CliPath}\" daemon stop --name \"{enrollment.Name}\" --direct";

    /// <summary>The double-NUL-terminated, sorted block CreateProcessAsUser takes</summary>
    public string EnvironmentBlock() =>
        string.Concat(Environment.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}\0")) + "\0";
}
