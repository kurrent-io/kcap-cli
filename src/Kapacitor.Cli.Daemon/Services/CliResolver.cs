namespace Kapacitor.Cli.Daemon.Services;

/// <summary>
/// Best-effort lookup that mirrors how a shell finds an executable: if the
/// configured path is absolute, check the file directly; otherwise walk
/// <c>PATH</c> (plus <c>PATHEXT</c> on Windows). Used by
/// <see cref="IHostedAgentLauncher.IsAvailable"/> at daemon startup (AI-652)
/// to decide which vendor launchers to advertise over <c>DaemonConnect</c>.
///
/// <para>This intentionally does NOT execute the binary — startup must stay
/// cheap. False positives (binary present but unusable for some other
/// reason) surface later as the existing <c>LaunchFailed</c> path.</para>
/// </summary>
internal static class CliResolver {
    /// <summary>
    /// Returns <c>true</c> when <paramref name="cliPath"/> resolves to an
    /// existing file — either directly (absolute path) or via <c>PATH</c>
    /// lookup (bare command).
    /// </summary>
    public static bool Exists(string cliPath) {
        if (string.IsNullOrWhiteSpace(cliPath)) return false;

        if (Path.IsPathFullyQualified(cliPath))
            return File.Exists(cliPath);

        // Bare command — walk PATH. Splitting on the platform separator is
        // sufficient; we don't need to handle quoted PATH entries because
        // POSIX disallows them and Windows tolerates raw paths in PATH.
        var sep   = OperatingSystem.IsWindows() ? ';' : ':';
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries)) {
            foreach (var ext in extensions) {
                var candidate = Path.Combine(dir, cliPath + ext);
                if (File.Exists(candidate)) return true;
            }
        }

        return false;
    }
}
