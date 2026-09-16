namespace Capacitor.Cli.Core;

/// Reads the origin remote URL straight from .git/config — no git process, safe on a UI path.
/// Callers pass the MAIN repo root (GitRepository.ResolveMainRepoRoot), so worktree gitfiles
/// never reach this parser.
public static class GitRemoteReader {
    public static string? ReadOriginUrl(string mainRepoRoot) =>
        ReadOriginUrlFromConfig(Path.Combine(mainRepoRoot, ".git", "config"));

    /// <summary>The origin URL in a git config file named directly, for a checkout whose config is
    /// not at <c>&lt;root&gt;/.git/config</c> — a submodule keeps its own under the superproject's
    /// <c>.git/modules/</c>.</summary>
    public static string? ReadOriginUrlFromConfig(string path) {
        string[] lines;
        try {
            if (!File.Exists(path)) return null;
            lines = File.ReadAllLines(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return null;
        }

        var inOrigin = false;
        foreach (var raw in lines) {
            var line = raw.Trim();
            if (line.StartsWith('[')) {
                inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase)) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var value = line[(eq + 1)..].Trim();
            return value.Length > 0 ? value : null;
        }
        return null;
    }
}
