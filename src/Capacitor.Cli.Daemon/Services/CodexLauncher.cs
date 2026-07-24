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
    public bool   SupportsUnattended => true;
    public bool   SupportsBorrowedReviewFlow => true;
    public string BorrowedReviewContainment => "native-tool-clamp";

    public bool IsAvailable() => CliResolver.Exists(CliPath);

    /// <summary>
    /// Enumerates the user-config MCP server names a review-flow reviewer would otherwise
    /// inherit. Default reads <c>~/.codex/config.toml</c> (CODEX_HOME honoured); injectable so
    /// <see cref="BuildArgs"/> stays deterministic in unit tests.
    /// </summary>
    internal Func<IReadOnlyList<string>> ReadInheritedMcpServerNames { get; init; } =
        static () => CodexConfigToml.ReadMcpServerNames();

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
            // A borrowed reviewer runs in the user's REAL checkout (not a daemon-owned,
            // throwaway worktree) — workspace-write would let it mutate that real repo.
            // read-only is already proven in the headless runner (CodexCliRunner), including
            // with MCP injection, so the flow-result MCP tool still works.
            ctx.Work == WorkLocation.BorrowedCwd ? "read-only" : "workspace-write",
            // Review-flow reviewers (LaunchKind.ReviewFlow) run unattended → never pause for
            // approval (writes stay confined by the workspace-write sandbox). Interactive rendered
            // agents keep the default on-request approval so the user stays in the loop.
            "--ask-for-approval",
            ctx.IsReviewFlow ? "never" : "on-request"
        };

        // Review-flow reviewers get exactly ONE MCP server: kcap-flow-result (+ any
        // allowlisted, non-flow-starting server) — it can only submit a result, never start a
        // flow. Codex's `-c` overrides deep-merge into ~/.codex/config.toml (no analog of
        // Claude's `--strict-mcp-config`), so we disable every inherited server via a
        // per-server `-c mcp_servers.<name>.enabled=false`, then force-enable exactly the
        // whitelisted names with `enabled=true` — otherwise a reviewer inherits every user MCP
        // server (including a hand-registered kcap-flows with start_review_flow, vanishing the
        // recursion guard), or — if the user's own config already disabled a whitelisted name —
        // starts without its result-submission channel.
        if (ctx.IsReviewFlow) {
            DisableInheritedMcpServers(args, ctx);
            AddFlowResultServer(args, ctx);
            AddAllowlistServers(args, ctx);
        }

        AddModelArg(args, ctx.Model);

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

    /// <summary>
    /// Real MCP isolation for a review-flow reviewer: emits a per-server
    /// <c>-c mcp_servers.&lt;name&gt;.enabled=false</c> for every server the reviewer would
    /// otherwise inherit from <c>~/.codex/config.toml</c>, so only the servers we explicitly
    /// whitelist afterwards actually load. Skips any name we are about to whitelist (so the
    /// flow-result / allowlist servers stay enabled) and any name that cannot be expressed as a
    /// Codex <c>-c</c> dotted key — a name containing <c>.</c> would be mis-split by Codex's key
    /// parser (verified: it errors), so such a name is logged and left (realistic kcap-flows /
    /// node_repl / computer-use names have no dots).
    /// </summary>
    void DisableInheritedMcpServers(List<string> args, LauncherContext ctx) {
        var whitelisted = WhitelistedServerNames(ctx);

        foreach (var name in ReadInheritedMcpServerNames()) {
            if (string.IsNullOrEmpty(name)) continue;
            if (whitelisted.Contains(name)) continue;

            if (name.Contains('.')) {
                LogInheritedServerUndisableable(name, ctx.AgentId);
                continue;
            }

            args.Add("-c");
            args.Add($"mcp_servers.{name}.enabled=false");
        }
    }

    /// <summary>The MCP server names <see cref="AddFlowResultServer"/> +
    /// <see cref="AddAllowlistServers"/> will enable — the disable pass must never disable one of
    /// these. Empty when the daemon has no server URL / kcap path (nothing is whitelisted, so the
    /// disable pass strips everything — the recursion-safe default).</summary>
    HashSet<string> WhitelistedServerNames(LauncherContext ctx) {
        var set = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return set;

        set.Add("kcap-flow-result");

        foreach (var name in ctx.McpAllowlist ?? []) {
            var descriptor = KcapMcpRegistry.Resolve(name);

            if (descriptor is null || descriptor.StartsFlows) continue;

            set.Add(descriptor.Id);
        }

        return set;
    }

    /// <summary>Registers the reviewer-side result-submission server. Skipped (zero
    /// servers — the recursion-safe default) when the daemon has no server URL or kcap path;
    /// the reviewer then falls back to the transcript marker per the prompt contract.</summary>
    void AddFlowResultServer(List<string> args, LauncherContext ctx) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return;

        const string name = "kcap-flow-result";

        // Force-enable: the disable pass above skips this name, but the user's OWN
        // ~/.codex/config.toml may already have it (or never had it) set to enabled=false from
        // a prior manual registration. `-c` deep-merges over that file, so skipping the disable
        // is not enough on its own — an explicit enabled=true override wins regardless.
        args.Add("-c");
        args.Add($"mcp_servers.{name}.enabled=true");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.command={TomlString(config.CapacitorPath)}");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.args=[{TomlString("mcp")},{TomlString("flow-result")}]");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.env={{KCAP_URL={TomlString(config.ServerUrl)},KCAP_FLOW_AGENT_ID={TomlString(ctx.AgentId)}}}");
    }

    /// <summary> D-c: materializes the flow definition's <see cref="LauncherContext.McpAllowlist"/>
    /// as additional dotted overrides, in the same clear-then-whitelist style as
    /// <see cref="AddFlowResultServer"/>. Each name resolves against the kcap-owned
    /// <see cref="KcapMcpRegistry"/> — never ambient user config — unknown names are skipped,
    /// and any flow-starting server is stripped regardless of listing (the recursion guard).
    /// Allowlist servers get KCAP_URL only — never KCAP_FLOW_AGENT_ID, which is exclusive to
    /// the flow-result submission channel. Skipped (same as the flow-result server) when the
    /// daemon has no server URL or kcap path configured.</summary>
    void AddAllowlistServers(List<string> args, LauncherContext ctx) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return;

        foreach (var name in ctx.McpAllowlist ?? []) {
            var descriptor = KcapMcpRegistry.Resolve(name);

            if (descriptor is null) {
                // name may be null here — a wire-deserialized allowlist element — so log it
                // defensively rather than let a null flow into the formatter unlabeled.
                LogAllowlistEntryUnknown(name ?? "(null)", ctx.AgentId);
                continue;
            }

            if (descriptor.StartsFlows) {
                LogAllowlistEntryStripped(name, ctx.AgentId);
                continue;
            }

            var id       = descriptor.Id;
            var argsList = string.Join(",", descriptor.Args.Select(TomlString));

            // Force-enable for the same reason as AddFlowResultServer: the user's own config
            // may already carry this name disabled, and `-c` only deep-merges over it.
            args.Add("-c");
            args.Add($"mcp_servers.{id}.enabled=true");
            args.Add("-c");
            args.Add($"mcp_servers.{id}.command={TomlString(config.CapacitorPath)}");
            args.Add("-c");
            args.Add($"mcp_servers.{id}.args=[{argsList}]");
            args.Add("-c");
            args.Add($"mcp_servers.{id}.env={{KCAP_URL={TomlString(config.ServerUrl)}}}");
        }
    }

    /// Append `-m <model>` unless the model is empty or the "default" no-override sentinel.
    /// "default" is the sentinel from the flow/agent dispatch; passing it as `-m default` is
    /// rejected by Codex on a ChatGPT account ("The 'default' model is not supported when using
    /// Codex with a ChatGPT account") and silently yields an empty turn. Omitting -m makes Codex
    /// use the model from ~/.codex/config.toml (mirrors the effort=="auto" case). Shared by both
    /// the default and review launch paths so the sentinel is honored in either.
    static void AddModelArg(List<string> args, string? model) {
        if (!string.IsNullOrEmpty(model) && !string.Equals(model, "default", StringComparison.OrdinalIgnoreCase)) {
            args.Add("-m");
            args.Add(model);
        }
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

        AddModelArg(args, ctx.Model);

        args.Add("--no-alt-screen");
        args.Add("--");
        args.Add(launch.SystemPrompt);

        return new([.. args], McpConfigPath: null);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP allowlist entry '{Name}' is not a kcap-owned server — skipping (agent {AgentId})")]
    partial void LogAllowlistEntryUnknown(string name, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP allowlist entry '{Name}' can start flows — stripped (agent {AgentId})")]
    partial void LogAllowlistEntryStripped(string name, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inherited MCP server '{Name}' has a dotted name and cannot be disabled via a Codex -c override — it stays available to reviewer agent {AgentId}")]
    partial void LogInheritedServerUndisableable(string name, string agentId);
}
