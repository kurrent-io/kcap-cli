using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed partial class CodexLauncher(
        DaemonConfig           config,
        ILogger<CodexLauncher> logger
    ) : IHostedAgentLauncher {
    public string Vendor  => "codex";
    public string CliPath => config.CodexPath;

    public bool IsAvailable() => CliResolver.Exists(CliPath);

    static readonly string[] CriticalHookEvents = ["SessionStart", "Stop", "PermissionRequest"];

    public void Prepare(LauncherContext ctx) {
        // Step 1: overlay source/.codex into worktree FIRST so project-scope
        // hooks (kapacitor plugin install --codex --project) become visible to
        // the preflight in step 2. Best-effort.
        try {
            var sourceCodexDir = Path.Combine(ctx.SourceRepoPath, ".codex");

            if (Directory.Exists(sourceCodexDir)) {
                FileSystemOverlay.OverlayDirectory(sourceCodexDir, Path.Combine(ctx.Worktree.Path, ".codex"));
            }
        } catch (Exception ex) {
            LogOverlayFailed(ex, ctx.AgentId);
        }

        // Step 2: hook preflight (fail-fast). Either worktree-scope (after overlay)
        // OR user-scope is sufficient.
        var worktreeHooks = Path.Combine(ctx.Worktree.Path, ".codex", "hooks.json");

        if (!HooksInstalledIn(worktreeHooks) && !HooksInstalledIn(CodexPaths.UserHooksJson)) {
            throw new CodexHooksNotInstalledException(
                "Codex hooks not installed. Run `kapacitor plugin install --codex` " +
                "(user scope) or `kapacitor plugin install --codex --project` "      +
                "(project scope) and try again."
            );
        }

        // Step 3: pre-trust the worktree in ~/.codex/config.toml. Best-effort.
        try {
            CodexConfigWriter.TrustWorktree(ctx.Worktree.Path, logger);
        } catch (Exception ex) {
            LogTrustFailed(ex, ctx.AgentId);
        }

        if (ctx.Tools is { Length: > 0 }) {
            LogToolsIgnoredForCodex(ctx.AgentId, ctx.Tools.Length);
        }
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        var args = new List<string> {
            "--cd",
            ctx.Worktree.Path,
            "--sandbox",
            "workspace-write",
            "--ask-for-approval",
            "on-request"
        };

        if (!string.IsNullOrEmpty(ctx.Model)) {
            args.Add("-m");
            args.Add(ctx.Model);
        }

        var effort = ctx.Effort;

        if (!string.IsNullOrEmpty(effort) && !string.Equals(effort, "auto", StringComparison.OrdinalIgnoreCase)) {
            var mapped = string.Equals(effort, "max", StringComparison.OrdinalIgnoreCase) ? "xhigh" : effort;
            args.Add("-c");
            args.Add($"model_reasoning_effort=\"{mapped}\"");
        }

        args.Add("--no-alt-screen");

        if (!string.IsNullOrEmpty(ctx.Prompt)) {
            args.Add("--");
            args.Add(ctx.Prompt);
        }

        return new([.. args], McpConfigPath: null);
    }

    public void Cleanup(AgentInstance agent) {
        // No-op: ~/.codex/config.toml trust entries are intentionally persistent.
    }

    static bool HooksInstalledIn(string hooksPath) {
        if (!File.Exists(hooksPath)) return false;

        try {
            var root = JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject;

            return root is not null && CodexHooksParser.HasKapacitorHooksFor(root, CriticalHookEvents);
        } catch {
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to overlay .codex settings for agent {AgentId} (continuing)")]
    partial void LogOverlayFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to pre-trust worktree for agent {AgentId} (continuing)")]
    partial void LogTrustFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tools array of length {Count} ignored for vendor=codex (no allowlist concept) — agent {AgentId}")]
    partial void LogToolsIgnoredForCodex(string agentId, int count);
}
