namespace kapacitor.Commands;

/// <summary>
/// Detects whether a coding-agent CLI is installed by probing every directory
/// on PATH for an executable file. Cross-platform: walks PATHEXT on Windows;
/// checks the executable bit on Unix. The pure internal overload accepts
/// fully-injected dependencies so unit tests don't touch the real environment.
/// </summary>
public static class AgentDetector {
    /// <summary>
    /// Pure, OS-agnostic core. Iterates the cartesian product of
    /// <paramref name="paths"/> × <paramref name="extensions"/>, returning
    /// true on the first <paramref name="isExecutable"/> hit.
    /// </summary>
    internal static bool IsInstalled(
        string binaryName,
        IEnumerable<string> paths,
        IEnumerable<string> extensions,
        Func<string, bool> isExecutable) {
        foreach (var dir in paths) {
            if (string.IsNullOrEmpty(dir)) continue;

            foreach (var ext in extensions) {
                var candidate = Path.Combine(dir, binaryName + ext);
                if (isExecutable(candidate)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Probes the current process's PATH for <paramref name="binaryName"/>.
    /// Returns false on a null/empty PATH. On Unix, requires at least one of
    /// the user/group/other execute bits; on Windows, walks PATHEXT
    /// (defaulting to .EXE/.CMD/.BAT) and accepts any file that exists.
    /// </summary>
    public static bool IsInstalled(string binaryName) {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return false;

        var paths      = pathEnv.Split(Path.PathSeparator);
        var extensions = OperatingSystem.IsWindows() ? GetWindowsExtensions() : [""];

        return IsInstalled(binaryName, paths, extensions, IsExecutable);
    }

    static string[] GetWindowsExtensions() {
        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var raw     = string.IsNullOrEmpty(pathExt) ? ".EXE;.CMD;.BAT" : pathExt;

        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    static bool IsExecutable(string path) {
        if (!File.Exists(path)) return false;
        if (OperatingSystem.IsWindows()) return true; // PATHEXT already filtered the candidates

        // Unix: any of UGO execute bits is enough — an intentional heuristic.
        // True access(X_OK) would require P/Invoke against the effective UID/GID.
        // The rare false positive (binary with execute bits but unrelated owner)
        // degrades to the same outcome as a runtime-broken binary.
        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        try {
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
        } catch {
            // TOCTOU race (file removed between File.Exists and GetUnixFileMode),
            // permission denied, or other I/O failure — treat as not executable
            // so detection doesn't abort the wizard.
            return false;
        }
    }
}
