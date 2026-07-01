using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.LocalIpc;
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
        // A borrowed cwd is the user's own repo: skip the repo-mutating steps (overlay,
        // ~/.codex trust write). Only the read-only hooks preflight runs for it.
        var owned = ctx.Work == WorkLocation.OwnedWorktree;

        if (owned) {
            // Step 1: overlay source/.codex into worktree FIRST so project-scope
            // hooks (kcap plugin install --codex --project) become visible to
            // the preflight in step 2. Best-effort.
            try {
                var sourceCodexDir = Path.Combine(ctx.SourceRepoPath, ".codex");

                if (Directory.Exists(sourceCodexDir)) {
                    FileSystemOverlay.OverlayDirectory(sourceCodexDir, Path.Combine(ctx.Worktree.Path, ".codex"));
                }
            } catch (Exception ex) {
                LogOverlayFailed(ex, ctx.AgentId);
            }
        }

        // Step 2: hook preflight (fail-fast, read-only). Either worktree/cwd-scope OR
        // user-scope is sufficient. Runs for borrowed cwd too — it only reads.
        var worktreeHooks = Path.Combine(ctx.Worktree.Path, ".codex", "hooks.json");

        if (!HooksInstalledIn(worktreeHooks) && !HooksInstalledIn(CodexPaths.UserHooksJson)) {
            throw new CodexHooksNotInstalledException(
                "Codex hooks not installed. Run `kcap plugin install --codex` " +
                "(user scope) or `kcap plugin install --codex --project` "      +
                "(project scope) and try again."
            );
        }

        if (owned) {
            // Step 3: pre-trust the worktree in ~/.codex/config.toml. Best-effort.
            try {
                CodexConfigWriter.TrustWorktree(ctx.Worktree.Path, logger);
            } catch (Exception ex) {
                LogTrustFailed(ex, ctx.AgentId);
            }
        }

        if (ctx.Tools is { Length: > 0 }) {
            LogToolsIgnoredForCodex(ctx.AgentId, ctx.Tools.Length);
        }
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        if (ctx is { IsReview: true, ReviewLaunch: { } launch }) {
            return BuildReviewArgs(ctx, launch);
        }

        var args = new List<string> {
            "--cd",
            ctx.Worktree.Path,
            "--sandbox",
            "workspace-write",
            "--ask-for-approval",
            "on-request"
        };

        AppendModel(args, ctx.Model);

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

    /// Review launch: inject the same kcap-review MCP server Claude gets, but via
    /// ephemeral `-c` overrides (no ~/.codex/config.toml mutation, nothing to clean
    /// up), and pass the rendered review prompt as Codex's initial prompt (Codex has
    /// no --system-prompt equivalent).
    static LaunchArgs BuildReviewArgs(LauncherContext ctx, ReviewLaunchBuilder.ReviewLaunch launch) {
        const string serverName = "kcap-review";
        var          mcp        = launch.Mcp;

        var args = new List<string> {
            "--cd",
            ctx.Worktree.Path,
            "--sandbox",
            "workspace-write",
            "--ask-for-approval",
            "on-request"
        };

        var argsList = string.Join(",", mcp.Args.Select(TomlString));
        var envList  = string.Join(",", mcp.Env.Select(kv => $"{kv.Key}={TomlString(kv.Value)}"));

        args.Add("-c");
        args.Add($"mcp_servers.{serverName}.command={TomlString(mcp.Command)}");
        args.Add("-c");
        args.Add($"mcp_servers.{serverName}.args=[{argsList}]");
        args.Add("-c");
        args.Add($"mcp_servers.{serverName}.env={{{envList}}}");

        AppendModel(args, ctx.Model);

        args.Add("--no-alt-screen");
        args.Add("--");
        args.Add(launch.SystemPrompt);

        return new([.. args], McpConfigPath: null);
    }

    /// Append `-m <model>` unless the model is empty or the vendor-neutral sentinel
    /// "default". The server sends "default" to mean "use the vendor's own default
    /// model"; Claude accepts `--model default` but Codex has no such model and rejects
    /// `-m default` on ChatGPT/subscription accounts (HTTP 400, AI-1114). Omitting -m
    /// lets Codex fall back to its configured/account default — mirroring how the
    /// "auto" effort sentinel is dropped so the CLI picks its own default.
    static void AppendModel(List<string> args, string? model) {
        if (string.IsNullOrEmpty(model) || string.Equals(model, "default", StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        args.Add("-m");
        args.Add(model);
    }

    /// Encode a value as a TOML basic string: wrap in double quotes and escape
    /// backslashes, double quotes, and control characters. TOML basic strings forbid
    /// raw control chars, so an unescaped tab/newline/CR would yield invalid TOML and
    /// fail the Codex `-c` config parse. Covers Windows paths and arbitrary URLs.
    static string TomlString(string value) {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (var c in value) {
            switch (c) {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\b': sb.Append("\\b");  break;
                case '\t': sb.Append("\\t");  break;
                case '\n': sb.Append("\\n");  break;
                case '\f': sb.Append("\\f");  break;
                case '\r': sb.Append("\\r");  break;
                default:
                    // Remaining C0 controls (and DEL) have no short escape — emit \uXXXX.
                    if (c < ' ' || c == (char)0x7f) {
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    } else {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');

        return sb.ToString();
    }

    /// Local launch: emit the mandatory daemon-level flags Codex always needs, then append
    /// the user's verbatim post-`--` args. A user duplicate of a mandatory flag is rejected
    /// outright (relying on Codex's arg precedence to make ours win is fragile).
    public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) {
        string[] mandatory = ["--cd", "--no-alt-screen"];

        foreach (var m in mandatory) {
            if (userArgs.Contains(m)) {
                throw new ArgumentException($"{m} is set by kcap and cannot be overridden in `run-agent codex -- …`");
            }
        }

        // --cd sets the working dir; --no-alt-screen keeps the mirror/replay on the primary
        // screen. sandbox/approval defaults match the hosted path but stay user-overridable.
        var args = new List<string> {
            "--cd", ctx.Worktree.Path,
            "--sandbox", "workspace-write",
            "--ask-for-approval", "on-request",
            "--no-alt-screen"
        };
        args.AddRange(userArgs);

        return new([.. args], McpConfigPath: null);
    }

    public void Cleanup(AgentInstance agent) {
        // No-op: ~/.codex/config.toml trust entries are intentionally persistent.
    }

    static bool HooksInstalledIn(string hooksPath) {
        if (!File.Exists(hooksPath)) return false;

        try {
            var root = JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject;

            return root is not null && CodexHooksParser.HasCapacitorHooksFor(root, CriticalHookEvents);
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
