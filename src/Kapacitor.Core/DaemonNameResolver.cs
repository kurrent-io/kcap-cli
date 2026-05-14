namespace kapacitor;

/// <summary>
/// Shared resolution of the agent daemon's name. Lives in Kapacitor.Core
/// so the CLI supervisor and the daemon binary agree on which name the
/// per-name lock / PID files belong to. The precedence below matches the
/// order historically used by <c>DaemonRunner.RunAsync</c>:
///
/// <list type="number">
/// <item><c>--name &lt;value&gt;</c> on the command line</item>
/// <item><c>profile.daemon.name</c> from the active profile (resolved
///     via <c>AppConfig.ResolveActiveProfile</c>)</item>
/// <item><c>KAPACITOR_DAEMON_NAME</c> environment variable (overrides
///     the profile)</item>
/// <item>OS username, lowercased</item>
/// <item>Machine name, lowercased</item>
/// <item>The literal string <c>"daemon"</c></item>
/// </list>
/// </summary>
public static class DaemonNameResolver {
    /// <summary>
    /// Parse the daemon name from <paramref name="args"/>. Pass the
    /// profile-supplied default (typically
    /// <c>AppConfig.ResolvedProfile?.Profile?.Daemon?.Name</c>) so callers
    /// don't need to thread AppConfig into Kapacitor.Core. Returns the
    /// resolved name; never null or empty.
    /// </summary>
    public static string Resolve(string[] args, string? profileName = null) {
        string? name = null;

        for (var i = 0; i < args.Length - 1; i++) {
            if (args[i] == "--name") {
                name = args[i + 1];

                break;
            }
        }

        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(profileName))
            name = profileName;

        // Env var overrides the profile (matches the historical
        // DaemonRunner.RunAsync ordering — added for shell scripts that
        // want to fan out daemons without rewriting the profile file).
        if (Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_NAME") is { Length: > 0 } envName)
            name = envName;

        if (string.IsNullOrEmpty(name)) {
            var userName = Environment.UserName;
            name = !string.IsNullOrEmpty(userName)
                ? userName.ToLowerInvariant()
                : !string.IsNullOrEmpty(Environment.MachineName)
                    ? Environment.MachineName.ToLowerInvariant()
                    : "daemon";
        }

        return name;
    }
}
