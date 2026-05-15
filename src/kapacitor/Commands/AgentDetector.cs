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
}
