namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>The <c>statusLine.command</c> wiring writes. The account id travels in the command because
/// <c>CLAUDE_CODE_SUBPROCESS_ENV_SCRUB</c> can strip <c>CLAUDE_CONFIG_DIR</c> from the status line's environment.</summary>
public static class ClaudeStatusLineCommand {
    const string Plain = "kcap statusline --account ";
    const string Guard = "if command -v kcap >/dev/null 2>&1; then exec " + Plain;
    const string Else  = "; else ";
    const string End   = "; fi";

    /// <summary>With a POSIX shell, a missing kcap still runs the original. The original is one single-quoted
    /// argument to a nested <c>sh -c</c>, never spliced into the guard, so its comments, newlines and heredocs
    /// parse exactly as they did on their own. PowerShell gets the plain command, and a missing kcap blanks the line.</summary>
    public static string For(string accountId, string? original, bool posixShell) =>
        !posixShell ? Plain + accountId
                    : Guard + accountId + Else + (original is null ? ":" : "exec sh -c " + SingleQuoted(original)) + End;

    public static bool IsOurs(string? command) => AccountOf(command) is not null;

    internal static string? AccountOf(string? command) {
        if (command is null) return null;

        string id;
        if (command.StartsWith(Guard, StringComparison.Ordinal)) {
            var rest = command[Guard.Length..];
            var cut  = rest.IndexOf(Else, StringComparison.Ordinal);
            if (cut < 0 || !rest.EndsWith(End, StringComparison.Ordinal)) return null;
            id = rest[..cut];
        } else if (command.StartsWith(Plain, StringComparison.Ordinal)) {
            id = command[Plain.Length..];
        } else {
            return null;
        }

        return id.Length > 0 && !id.Any(char.IsWhiteSpace) ? id : null;
    }

    public static bool PosixShell(Func<string, string?> env) => PosixShell(env, OperatingSystem.IsWindows(), File.Exists);

    /// <summary>Claude runs a status line through Git Bash when it finds one, and through PowerShell otherwise.</summary>
    internal static bool PosixShell(Func<string, string?> env, bool windows, Func<string, bool> fileExists) {
        if (!windows) return true;

        if (env("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } configured && fileExists(configured)) return true;

        foreach (var dir in (env("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!fileExists(Path.Combine(dir, "git.exe"))) continue;

            return fileExists(Path.GetFullPath(Path.Combine(dir, "..", "bin", "bash.exe")));
        }

        return false;
    }

    static string SingleQuoted(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
