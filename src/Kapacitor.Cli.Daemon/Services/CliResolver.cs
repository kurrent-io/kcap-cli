namespace Kapacitor.Cli.Daemon.Services;

/// <summary>
/// Best-effort lookup that mirrors how a shell finds an executable: if the
/// configured path is absolute, check the file directly; otherwise walk
/// <c>PATH</c> (plus <c>PATHEXT</c> on Windows). On Unix, requires at least
/// one execute bit (mirrors <c>AgentDetector.IsExecutable</c> in the CLI
/// project — keep the two in sync). Used by
/// <see cref="IHostedAgentLauncher.IsAvailable"/> at daemon startup (AI-652)
/// to decide which vendor launchers to advertise over <c>DaemonConnect</c>.
///
/// <para>This intentionally does NOT execute the binary — startup must stay
/// cheap. False positives (binary present, exec bit set, but unusable for
/// some other reason) surface later as the existing <c>LaunchFailed</c>
/// path.</para>
/// </summary>
internal static class CliResolver {
    /// <summary>
    /// Returns <c>true</c> when <paramref name="cliPath"/> resolves to an
    /// existing, executable file — either directly (absolute path) or via
    /// <c>PATH</c> lookup (bare command).
    /// </summary>
    public static bool Exists(string cliPath) {
        if (string.IsNullOrWhiteSpace(cliPath)) return false;

        if (Path.IsPathFullyQualified(cliPath))
            return IsExecutable(cliPath);

        // Bare command — walk PATH. Splitting on the platform separator is
        // sufficient; we don't need to handle quoted PATH entries because
        // POSIX disallows them and Windows tolerates raw paths in PATH.
        var pathEnv = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(pathEnv)) return false;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        return pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => extensions.Select(ext => Path.Combine(dir, cliPath + ext)).Any(IsExecutable));
    }

    /// <summary>
    /// File exists AND (on Unix) has at least one execute bit set. Kept in
    /// sync with <c>AgentDetector.IsExecutable</c>; if the heuristic moves
    /// (e.g. to <c>access(X_OK)</c> via P/Invoke), bump both copies.
    /// </summary>
    static bool IsExecutable(string path) {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true; // PATHEXT already filtered the candidates

        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        try {
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        } catch {
            // TOCTOU race (file removed between File.Exists and GetUnixFileMode),
            // permission denied, or other I/O failure — treat as not executable
            // so the daemon doesn't advertise a vendor it can't actually spawn.
            return false;
        }
    }
}
