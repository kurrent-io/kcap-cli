namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Writes and removes kcap's Kiro Crew hook script. Crew accepts a hook only as an absolute path to
/// an executable <c>.sh</c> file with no arguments, and takes its event from the <c># event:</c>
/// header, so the script is a thin wrapper over <c>kcap hook --kiro</c>.
/// </summary>
public static class KiroCrewHookInstaller {
    public enum Outcome { Unchanged, Written, Removed, Unsupported, Failed }

    /// <summary>Identifies a script as kcap's, so removal never deletes a user's own hook.</summary>
    const string OwnershipLine = "# kcap: records Kiro Crew sessions in Kurrent Capacitor. Remove with: kcap plugin remove --kiro";

    /// <summary>
    /// The script body. Crew launches <c>kiro-cli</c> with its own PATH rather than the user's shell
    /// PATH, so <paramref name="kcapDir"/> (where <c>kcap</c> resolved at install) is the fallback.
    /// </summary>
    public static string Render(string? kcapDir) {
        var fallback = string.IsNullOrEmpty(kcapDir)
            ? ""
            : $"command -v kcap >/dev/null 2>&1 || PATH=\"{kcapDir.Replace("\"", "\\\"")}:$PATH\"\n";

        return "#!/bin/sh\n"
             + "# event: agentSpawn\n"
             + OwnershipLine + "\n"
             + fallback
             + "exec kcap hook --kiro --event agentSpawn\n";
    }

    public static bool IsInstalled(string scriptPath) {
        try {
            return File.Exists(scriptPath) && File.ReadAllText(scriptPath).Contains(OwnershipLine, StringComparison.Ordinal);
        } catch {
            return false;
        }
    }

    public static Outcome Install(string scriptPath, string? kcapDir) {
        // Crew runs hooks through /bin/sh and checks the execute bit; neither exists on Windows.
        if (OperatingSystem.IsWindows()) return Outcome.Unsupported;

        var body = Render(kcapDir);

        try {
            if (File.Exists(scriptPath) && File.ReadAllText(scriptPath) == body) {
                EnsureExecutable(scriptPath);

                return Outcome.Unchanged;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            File.WriteAllText(scriptPath, body);
            EnsureExecutable(scriptPath);

            return Outcome.Written;
        } catch {
            return Outcome.Failed;
        }
    }

    public static Outcome Remove(string scriptPath) {
        if (!File.Exists(scriptPath)) return Outcome.Unchanged;
        if (!IsInstalled(scriptPath)) return Outcome.Unchanged;

        try {
            File.Delete(scriptPath);

            return Outcome.Removed;
        } catch {
            return Outcome.Failed;
        }
    }

    static void EnsureExecutable(string scriptPath) {
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        if (File.GetUnixFileMode(scriptPath) != mode) File.SetUnixFileMode(scriptPath, mode);
    }
}
