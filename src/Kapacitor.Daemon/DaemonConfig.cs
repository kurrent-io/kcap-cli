namespace kapacitor.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = 5;

    public string WorktreeRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".capacitor",
        "worktrees"
    );

    public string ClaudePath { get; set; } = "claude";
    public string CodexPath  { get; set; } = "codex";

    /// <summary>
    /// Path to the kapacitor CLI binary. Used by the daemon to spawn auxiliary
    /// processes (e.g. <c>generate-whats-done</c>) when claude didn't fire its
    /// own session-end hook. Defaults to "kapacitor" — resolved via PATH, which
    /// works for npm installs that place both <c>kapacitor</c> and
    /// <c>kapacitor-daemon</c> in <c>node_modules/.bin</c>.
    /// </summary>
    public string KapacitorPath { get; set; } = "kapacitor";

    public List<string> Validate() {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ServerUrl)) {
            errors.Add("ServerUrl is required");
        } else if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) {
            errors.Add($"ServerUrl must be a valid http/https URL, got: {ServerUrl}");
        }

        if (MaxConcurrentAgents < 1) {
            errors.Add("MaxConcurrentAgents must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(WorktreeRoot)) {
            errors.Add("WorktreeRoot is required");
        }

        return errors;
    }

    public bool IsRepoAllowed(string repoPath) {
        if (AllowedRepoPaths.Length == 0) {
            return true;
        }

        return AllowedRepoPaths.Any(pattern => {
                if (pattern.EndsWith("/*")) {
                    var prefix = pattern[..^1]; // keep trailing slash: "/allowed/"

                    return repoPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(repoPath, pattern[..^2], StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(repoPath, pattern, StringComparison.OrdinalIgnoreCase);
            }
        );
    }
}
