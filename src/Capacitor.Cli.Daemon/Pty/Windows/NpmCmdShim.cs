using System.Text.RegularExpressions;

namespace Capacitor.Cli.Daemon.Pty.Windows;

/// Reads the target out of an npm `cmd-shim` so a launch can start it directly. Going through
/// `cmd.exe /c` ends the command line at the first newline and hands quotes, ampersands, pipes and `%VAR%` in
/// an argument to cmd's own parser — a multi-line launch prompt arrives cut to its first line.
public static partial class NpmCmdShim {
    /// <param name="Program">What to start: the shim's own `node.exe`, bare `node`, or a native target.</param>
    /// <param name="Script">The script `node` runs, or null when <paramref name="Program"/> is the target itself.</param>
    public sealed record Target(string Program, string? Script);

    // The one line of the shim that runs the target: a quoted `%dp0%\…` (or `%~dp0\…`) path followed
    // by `%*`. The `"%dp0%\node.exe"` probe earlier in the file is followed by another path, not `%*`.
    [GeneratedRegex("""
        "(%~?dp0%?)\\?([^"]+)"\s+%\*
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex TargetLine();

    public static Target? Parse(string shimText, string shimDir, Func<string, bool> fileExists) {
        var match = TargetLine().Match(shimText);
        if (!match.Success) return null;

        var target = Join(shimDir, match.Groups[2].Value.TrimStart('\\').Replace('/', '\\'));
        var ext = Path.GetExtension(target);

        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return new Target(target, null);
        if (!ext.Equals(".js", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".cjs", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase)) return null;

        var bundledNode = Join(shimDir, "node.exe");
        return new Target(fileExists(bundledNode) ? bundledNode : "node", target);
    }

    // Always a backslash: the shim's paths are Windows paths whichever OS parses them.
    static string Join(string dir, string relative) => dir.TrimEnd('\\', '/') + "\\" + relative;

    /// Null for anything that is not a readable npm shim — the caller keeps its cmd.exe fallback.
    public static Target? TryRead(string cmdPath) {
        try {
            using var stream = new FileStream(cmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var dir = Path.GetDirectoryName(Path.GetFullPath(cmdPath));
            return dir is null ? null : Parse(reader.ReadToEnd(), dir, File.Exists);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}
