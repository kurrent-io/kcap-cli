namespace Capacitor.Cli.Daemon;

public class DaemonConfig {
    public string   Name                { get; set; } = "";
    public string   ServerUrl           { get; set; } = "";
    public string[] AllowedRepoPaths    { get; set; } = [];
    public int      MaxConcurrentAgents { get; set; } = 5;

    /// <summary>
    /// Per-process GUID generated at startup, also written to the daemon's
    /// flock-file content. Sent over <c>DaemonConnect</c> so the server
    /// (AI-630) can tell "same daemon reconnecting" from "different daemon
    /// claiming the same name". Set in <c>DaemonRunner.RunAsync</c> once
    /// the lock has been acquired; <c>null</c> in tests that bypass lock
    /// acquisition.
    /// </summary>
    public string? InstanceId { get; set; }

    /// <summary>
    /// Daemon binary version (<c>AssemblyInformationalVersion</c>). Sent
    /// over <c>DaemonConnect</c> and surfaced on the server's
    /// <c>Daemon connected:</c> log line + <c>DaemonInfo</c>. Set in
    /// <c>DaemonRunner.RunAsync</c>.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Vendor tokens this daemon can actually spawn — populated in
    /// <c>DaemonRunner.RunAsync</c> by probing each registered
    /// <c>IHostedAgentLauncher.IsAvailable()</c>. Sent over
    /// <c>DaemonConnect</c> (AI-652) so the server's launch dialog only
    /// offers vendors this daemon has installed. <c>null</c> when the
    /// host hasn't been built yet or in tests that bypass the runner.
    /// </summary>
    public string[]? SupportedVendors { get; set; }

    public string WorktreeRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".capacitor",
        "worktrees"
    );

    public string ClaudePath { get; set; } = "claude";
    public string CodexPath  { get; set; } = "codex";

    /// <summary>
    /// Path or bare command for the Cursor CLI's ACP entry point, spawned as
    /// <c>{CursorPath} acp</c> by <c>AcpHostedAgentRuntimeFactory</c> (AI-684 Task 10). Overridable
    /// via <c>KCAP_CURSOR_PATH</c>, mirroring <see cref="ClaudePath"/>/<see cref="CodexPath"/>.
    /// </summary>
    public string CursorPath { get; set; } = "cursor-agent";

    /// <summary>
    /// Family-prefix default model for Cursor ACP sessions, e.g.
    /// <c>"claude-sonnet-4-5"</c>. Cursor's wire protocol requires the exact, parameterized
    /// <c>modelId</c> from <c>session/new</c>'s <c>availableModels</c> (e.g.
    /// <c>claude-sonnet-4-5[thinking=true,context=200k]</c>), so this bare family name is resolved
    /// against that list at launch time by <c>AcpModelResolver.Resolve</c> — not sent verbatim.
    /// Overridable via <c>KCAP_CURSOR_MODEL</c>, mirroring <see cref="CursorPath"/>. A per-launch
    /// model override (<c>RuntimeStartContext.Model</c>, when the launch specifies one) takes
    /// precedence over this daemon-wide default — see <c>AcpHostedAgentRuntimeFactory</c>.
    /// </summary>
    public string CursorModel { get; set; } = "claude-sonnet-4-5";

    /// <summary>
    /// Path to the kcap CLI binary. Used by the daemon to spawn auxiliary
    /// processes (e.g. <c>generate-whats-done</c>) when claude didn't fire its
    /// own session-end hook. Defaults to "kcap" — resolved via PATH, which
    /// works for npm installs that place both <c>kcap</c> and
    /// <c>kcap-daemon</c> in <c>node_modules/.bin</c>.
    /// </summary>
    public string CapacitorPath { get; set; } = "kcap";

    /// <summary>The argv the daemon was launched with, captured for self-respawn (detached restart).</summary>
    public IReadOnlyList<string> OriginalArgs { get; set; } = [];

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

        // Compare with forward slashes so an operator's "/*" wildcard and prefix matching work
        // regardless of the host's native separator (Windows canonical paths use '\'). No-op on POSIX.
        var path = repoPath.Replace('\\', '/');

        return AllowedRepoPaths.Any(raw => {
                var pattern = raw.Replace('\\', '/');

                if (pattern.EndsWith("/*")) {
                    var prefix = pattern[..^1]; // keep trailing slash: "/allowed/"

                    return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(path, pattern[..^2], StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase);
            }
        );
    }
}
