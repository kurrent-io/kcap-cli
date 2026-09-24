namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Writes and removes kcap's Kiro Crew hook script. Crew accepts a hook only as an absolute path to
/// an executable <c>.sh</c> file with no arguments, and takes its event from the <c># event:</c>
/// header, so the script is a thin wrapper over <c>kcap hook --kiro</c>.
/// </summary>
public static class KiroCrewHookInstaller {
    /// <summary><see cref="Unowned"/>: a script of that name exists and kcap did not write it, so it is left alone.</summary>
    public enum Outcome { Unchanged, Written, Removed, Unowned, Unsupported, Failed }

    /// <summary>Identifies a script as kcap's, so kcap never overwrites or deletes a user's own hook.</summary>
    const string OwnershipLine = "# kcap: records Kiro Crew sessions in Kurrent Capacitor. Remove with: kcap plugin remove --kiro";

    /// <summary>Records that kcap installed the script, so a refresh can tell a Crew installed since from
    /// a script the user deleted. Crew imports only <c>*.sh</c>, so it ignores this file.</summary>
    const string InstalledMarker = ".kcap-crew-hook";

    /// <summary>
    /// The script body. Crew launches <c>kiro-cli</c> with its own PATH rather than the user's shell
    /// PATH, so <paramref name="kcapDir"/> (where <c>kcap</c> resolved at install) is the fallback. It
    /// is single-quoted, so the shell reads it as data.
    /// </summary>
    public static string Render(string? kcapDir) {
        var fallback = string.IsNullOrEmpty(kcapDir)
            ? ""
            : $"command -v kcap >/dev/null 2>&1 || PATH={ShellQuote(kcapDir)}:\"$PATH\"\n";

        return "#!/bin/sh\n"
             + "# event: agentSpawn\n"
             + OwnershipLine + "\n"
             + fallback
             + "exec kcap hook --kiro --event agentSpawn\n";
    }

    /// <summary>Whether kcap installed the script here and the user has since deleted it.</summary>
    public static bool WasRemoved(string scriptPath) =>
        File.Exists(MarkerFor(scriptPath)) && !File.Exists(scriptPath);

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
            if (File.Exists(scriptPath)) {
                if (!IsInstalled(scriptPath)) return Outcome.Unowned;

                if (File.ReadAllText(scriptPath) == body) {
                    EnsureExecutable(scriptPath);
                    File.WriteAllText(MarkerFor(scriptPath), "");

                    return Outcome.Unchanged;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            File.WriteAllText(scriptPath, body);
            EnsureExecutable(scriptPath);
            File.WriteAllText(MarkerFor(scriptPath), "");

            return Outcome.Written;
        } catch {
            return Outcome.Failed;
        }
    }

    public static Outcome Remove(string scriptPath) {
        try {
            if (File.Exists(MarkerFor(scriptPath))) File.Delete(MarkerFor(scriptPath));

            if (!File.Exists(scriptPath) || !IsInstalled(scriptPath)) return Outcome.Unchanged;

            File.Delete(scriptPath);

            return Outcome.Removed;
        } catch {
            return Outcome.Failed;
        }
    }

    static string MarkerFor(string scriptPath) => Path.Combine(Path.GetDirectoryName(scriptPath)!, InstalledMarker);

    static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    static void EnsureExecutable(string scriptPath) {
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        if (File.GetUnixFileMode(scriptPath) != mode) File.SetUnixFileMode(scriptPath, mode);
    }
}
