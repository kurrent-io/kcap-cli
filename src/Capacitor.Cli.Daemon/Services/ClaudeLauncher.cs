using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal sealed partial class ClaudeLauncher(
        DaemonConfig            config,
        ILogger<ClaudeLauncher> logger
    ) : IHostedAgentLauncher {
    public string Vendor             => "claude";
    public string CliPath            => config.ClaudePath;
    public bool   SupportsUnattended => true;

    public bool IsAvailable() => CliResolver.Exists(CliPath);

    static readonly Lock                  TrustWriteLock   = new();
    static readonly JsonSerializerOptions IndentedJsonOpts = new() { WriteIndented = true };

    // Strict + empty MCP config: --strict-mcp-config makes this authoritative (ignores
    // ~/.claude.json and project .mcp.json), and the empty map loads zero servers — so an
    // unattended review-flow reviewer cannot recursively invoke kcap-flows. Emitted as a
    // constant string (not a built JsonNode) to stay AOT-safe.
    const string EmptyMcpConfig = """{"mcpServers":{}}""";

    public void Prepare(LauncherContext ctx) {
        // A borrowed cwd is the user's own, already-trusted, already-configured repo — do
        // NOT overlay settings, write .mcp.json, edit settings.local.json, or write
        // ~/.claude.json trust into it (local in-place launch). Touch nothing.
        if (ctx.Work == WorkLocation.BorrowedCwd) return;

        // Overlay .claude/ local settings from source repo into worktree.
        try {
            var sourceClaudeDir = Path.Combine(ctx.SourceRepoPath, ".claude");
            var destClaudeDir   = Path.Combine(ctx.Worktree.Path, ".claude");

            if (Directory.Exists(sourceClaudeDir)) {
                FileSystemOverlay.OverlayDirectory(sourceClaudeDir, destClaudeDir);
            }

            SymlinkClaudeProjectDir(ctx.SourceRepoPath, ctx.Worktree.Path);
        } catch (Exception ex) {
            LogOverlayFailed(ex, ctx.AgentId);
        }

        try {
            WriteMcpConfig(ctx.SourceRepoPath, ctx.Worktree.Path);
        } catch (Exception ex) {
            LogMcpConfigFailed(ex, ctx.AgentId);
        }

        try {
            TrustWorktreeInClaudeConfig(ctx.Worktree.Path);
        } catch (Exception ex) {
            LogTrustWorktreeFailed(ex, ctx.AgentId);
        }

        try {
            if (ctx.Tools is { Length: > 0 }) {
                MergeToolPermissions(ctx.Worktree.Path, ctx.Tools);
            }
        } catch (Exception ex) {
            LogToolPermissionsFailed(ex, ctx.AgentId);
        }
    }

    public LaunchArgs BuildArgs(LauncherContext ctx) {
        var     args          = new List<string>();
        string? mcpConfigPath = null;

        if (ctx is { IsReview: true, ReviewLaunch: { } launch }) {
            mcpConfigPath = launch.McpConfigPath;

            args.Add("--mcp-config");
            args.Add(launch.McpConfigPath!);
            args.Add("--system-prompt");
            args.Add(launch.SystemPrompt);

            if (!string.IsNullOrEmpty(ctx.Model)) {
                args.Add("--model");
                args.Add(ctx.Model);
            }
        } else {
            // Review-flow reviewers (LaunchKind.ReviewFlow) run unattended: no permission
            // prompts (writes stay confined to the daemon-owned, throwaway worktree) and NO
            // MCP servers, so the reviewer can't recursively start a nested review flow.
            // Interactive (Default) agents keep prompts + their configured MCP servers.
            if (ctx.IsReviewFlow) {
                args.Add("--permission-mode");
                args.Add("bypassPermissions");
                args.Add("--strict-mcp-config");
                args.Add("--mcp-config");
                // AI-1139: the strict config now whitelists exactly the kcap-flow-result
                // submission server; the empty map remains the fallback when the daemon has
                // no server URL / kcap path configured.
                args.Add(BuildReviewFlowMcpConfig(ctx));
                // Disallow the built-in Agent (subagent) tool. Subagents do NOT inherit
                // --mcp-config (see ClaudeCliRunner), so a spawned subagent would re-read the
                // ambient user/project MCP config — which on a flows-enabled machine includes
                // the user-scoped kcap-flows server — and could recursively start a nested
                // flow, escaping the empty-MCP boundary above. The reviewer keeps Read/Grep/
                // Bash etc.; it just can't fan out subagents.
                args.Add("--disallowedTools");
                args.Add("Agent");
            }

            if (!string.IsNullOrEmpty(ctx.Effort)) {
                args.Add("--effort");
                args.Add(ctx.Effort);
            }

            if (!string.IsNullOrEmpty(ctx.Model)) {
                args.Add("--model");
                args.Add(ctx.Model);
            }

            if (!string.IsNullOrEmpty(ctx.Prompt)) {
                args.Add("--");
                args.Add(ctx.Prompt);
            }
        }

        return new LaunchArgs(args.ToArray(), mcpConfigPath);
    }

    /// <summary>AI-1139: strict whitelist for review-flow reviewers — exactly the
    /// kcap-flow-result submission server, or the empty map when the daemon has no server
    /// URL / kcap path (zero servers is the recursion-safe default). Built via JsonNode
    /// string casts — JsonValue.Create / collection expressions lower to generic Add&lt;T&gt;
    /// and trip NativeAOT (IL3050).
    /// AI-1126 D-c: on the non-empty path, also materializes the flow definition's
    /// <see cref="LauncherContext.McpAllowlist"/> into the same mcpServers object — each name
    /// resolved against the kcap-owned <see cref="KcapMcpRegistry"/> (never ambient user
    /// config), unknown names skipped, flow-starting servers stripped regardless of listing.
    /// Allowlist servers get KCAP_URL only — never KCAP_FLOW_AGENT_ID, which is exclusive to
    /// the flow-result submission channel.</summary>
    string BuildReviewFlowMcpConfig(LauncherContext ctx) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return EmptyMcpConfig;

        var argsNode = new JsonArray {
            (JsonNode?)"mcp",
            (JsonNode?)"flow-result"
        };

        var server = new JsonObject {
            ["command"] = config.CapacitorPath,
            ["args"]    = argsNode,
            ["env"] = new JsonObject {
                ["KCAP_URL"]           = config.ServerUrl,
                ["KCAP_FLOW_AGENT_ID"] = ctx.AgentId
            }
        };

        var mcpServers = new JsonObject { ["kcap-flow-result"] = server };

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

            var entryArgs = new JsonArray();
            foreach (var a in descriptor.Args) entryArgs.Add((JsonNode?)a);

            mcpServers[descriptor.Id] = new JsonObject {
                ["command"] = config.CapacitorPath,
                ["args"]    = entryArgs,
                ["env"] = new JsonObject {
                    ["KCAP_URL"] = config.ServerUrl
                }
            };
        }

        return new JsonObject { ["mcpServers"] = mcpServers }.ToJsonString();
    }

    /// Claude needs no mandatory daemon-level flags (cwd is set via forkpty chdir), so a
    /// local launch forwards the user's post-`--` args verbatim.
    public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs)
        => new([.. userArgs], McpConfigPath: null);

    public void Cleanup(AgentInstance agent) {
        try { RemoveClaudeProjectSymlink(agent.Worktree.Path); } catch (Exception ex) {
            LogCleanupSymlinkFailed(ex, agent.Id);
        }

        if (agent.McpConfigPath is not null) {
            try { File.Delete(agent.McpConfigPath); } catch (Exception ex) {
                LogCleanupMcpConfigFailed(ex, agent.Id);
            }
        }
    }

    // === Moved verbatim from AgentOrchestrator.cs ===

    /// <summary>
    /// Merges dialog-selected tool names into the worktree's .claude/settings.local.json
    /// permissions.allow array. Existing granular rules (e.g. "Bash(git:*)") are preserved;
    /// dialog selections add broad tool-level entries (e.g. "Bash").
    /// </summary>
    internal static void MergeToolPermissions(string worktreePath, string[] tools) {
        var settingsPath = Path.Combine(worktreePath, ".claude", "settings.local.json");

        JsonNode? root = null;

        if (File.Exists(settingsPath)) {
            try {
                root = JsonNode.Parse(File.ReadAllText(settingsPath));
            } catch {
                // Malformed JSON — start fresh
            }
        }

        if (root is not JsonObject rootObj) {
            rootObj = [];
        }

        if (rootObj["permissions"] is not JsonObject permissions) {
            permissions            = [];
            rootObj["permissions"] = permissions;
        }

        if (permissions["allow"] is not JsonArray allow) {
            allow                = [];
            permissions["allow"] = allow;
        }

        var existing = new HashSet<string>(
            allow.Select(n => (n as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
                .Where(s => s != null)!
        );

        foreach (var tool in tools) {
            if (!existing.Contains(tool)) {
                allow.Add((JsonNode)JsonValue.Create(tool)!);
                existing.Add(tool);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        File.WriteAllText(settingsPath, rootObj.ToJsonString(IndentedJsonOpts));
    }

    /// <summary>
    /// Creates a symlink at ~/.claude/projects/{worktree-path-hash} pointing to
    /// ~/.claude/projects/{source-path-hash} so project-level permissions, settings,
    /// and memory are shared with the hosted agent.
    /// </summary>
    static void SymlinkClaudeProjectDir(string sourceRepoPath, string worktreePath) {
        if (!Directory.Exists(ClaudePaths.Projects)) {
            return;
        }

        var sourceProjDir = ClaudePaths.ProjectDir(sourceRepoPath);

        if (!Directory.Exists(sourceProjDir)) {
            return;
        }

        var worktreeProjDir = ClaudePaths.ProjectDir(worktreePath);

        // Don't clobber an existing directory or symlink
        if (Path.Exists(worktreeProjDir)) {
            return;
        }

        Directory.CreateSymbolicLink(worktreeProjDir, sourceProjDir);
    }

    /// <summary>
    /// Removes the ~/.claude/projects/{worktree-path-hash} symlink if it exists.
    /// Only removes symlinks, never real directories.
    /// </summary>
    static void RemoveClaudeProjectSymlink(string worktreePath) {
        var info = new DirectoryInfo(ClaudePaths.ProjectDir(worktreePath));

        if (info is { Exists: true, LinkTarget: not null }) {
            info.Delete();
        }
    }

    /// <summary>
    /// Matches Claude Code's <c>projects[]</c> key normalisation: absolute + collapsed, and
    /// on Windows with forward slashes (Claude runs <c>path.normalize(p).replaceAll("\\","/")</c>).
    /// Writing the raw Windows path instead makes our pre-trust write invisible to Claude.
    /// </summary>
    internal static string NormalizeClaudeProjectKey(string path) {
        var full = Path.GetFullPath(path);

        return OperatingSystem.IsWindows() ? full.Replace('\\', '/') : full;
    }

    static void TrustWorktreeInClaudeConfig(string worktreePath) {
        // Serialize against concurrent agent launches. ~/.claude.json is shared
        // across the whole user and {worktree}/.claude/settings.local.json is
        // touched by MergeToolPermissions on the same path; interleaved reads and
        // writes could drop updates or write truncated JSON.
        lock (TrustWriteLock) {
            // Workspace trust (the "Do you trust the files in this folder?" dialog)
            // is stored globally in ~/.claude.json under projects[path]. On a fresh
            // machine the file may not exist yet — create a minimal object so trust
            // is always persisted.
            // The spawned `claude` inherits CLAUDE_CONFIG_DIR from the daemon's
            // environment, so it reads/writes the same file we resolve here —
            // $CLAUDE_CONFIG_DIR/.claude.json when set, else ~/.claude.json.
            var claudeJsonPath = ClaudePaths.UserConfigJson();
            var root           = LoadJsonObject(claudeJsonPath);

            if (root["projects"] is not JsonObject projects) {
                projects         = [];
                root["projects"] = projects;
            }

            // Claude Code keys projects[] by a normalised path, and on Windows that
            // normalisation converts backslashes to forward slashes (its `path.normalize`
            // then `.replaceAll("\\","/")`). If we write the raw backslash path here, Claude
            // looks up the forward-slash key, misses our entry, and shows the "Quick safety
            // check" trust dialog — the hosted agent then hangs at the prompt forever, never
            // starts a session, and the UI stays on "Waiting for session to start…" with an
            // empty terminal (Windows-only; POSIX paths already match). Write under the same
            // normalised key Claude reads.
            var trustKey = NormalizeClaudeProjectKey(worktreePath);

            if (projects[trustKey] is not JsonObject entry) {
                entry              = [];
                projects[trustKey] = entry;
            }

            var alreadyTrusted = entry["hasTrustDialogAccepted"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

            if (!alreadyTrusted) {
                entry["hasTrustDialogAccepted"] = true;
                WriteJsonAtomic(claudeJsonPath, root);
            }

            // MCP server approval (the "New MCP server found in .mcp.json" dialog)
            // is stored per-project in {worktree}/.claude/settings.local.json, NOT in
            // ~/.claude.json. Claude requires BOTH fields: the blanket flag AND each
            // server listed by name (the blanket flag alone still shows a per-server
            // discovery prompt on first encounter).
            var serverNames = ReadMcpJsonServerNames(worktreePath);

            if (serverNames.Count == 0) return;

            var settingsDir  = Path.Combine(worktreePath, ".claude");
            var settingsPath = Path.Combine(settingsDir, "settings.local.json");

            Directory.CreateDirectory(settingsDir);

            var settings = LoadJsonObject(settingsPath);
            var sDirty   = false;

            var allEnabled = settings["enableAllProjectMcpServers"] is JsonValue ev
             && ev.TryGetValue<bool>(out var eb)
             && eb;

            if (!allEnabled) {
                settings["enableAllProjectMcpServers"] = true;
                sDirty                                 = true;
            }

            var known = new HashSet<string>();

            if (settings["enabledMcpjsonServers"] is JsonArray arr) {
                foreach (var item in arr) {
                    if (item is JsonValue jv && jv.TryGetValue<string>(out var s)) {
                        known.Add(s);
                    }
                }
            }

            if (serverNames.Any(known.Add)) {
                // Rebuild the array via JSON parsing to avoid AOT issues with
                // JsonArray.Add(string) / JsonValue.Create<string>.
                var json = "[" + string.Join(",", known.Select(n => $"\"{JsonEncodedText.Encode(n)}\"")) + "]";
                settings["enabledMcpjsonServers"] = JsonNode.Parse(json);
                sDirty                            = true;
            }

            if (sDirty) {
                WriteJsonAtomic(settingsPath, settings);
            }
        }
    }

    /// <summary>
    /// Loads a JSON object from disk, tolerating missing files and non-object roots
    /// by returning a fresh <see cref="JsonObject"/>. Ensures pre-trust logic never
    /// throws on an unexpected config shape and reintroduces the launch hang.
    /// </summary>
    static JsonObject LoadJsonObject(string path) {
        if (!File.Exists(path)) return new JsonObject();

        try {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        } catch {
            return new JsonObject();
        }
    }

    /// <summary>
    /// Writes JSON to <paramref name="path"/> via a sibling temp file + atomic
    /// rename (POSIX <c>rename(2)</c> / Win32 <c>MoveFileEx</c> with replace).
    /// A crash or concurrent reader never sees a truncated file — either the
    /// previous contents or the new contents, never a partial write.
    /// </summary>
    internal static void WriteJsonAtomic(string path, JsonNode root) {
        // The temp file is written alongside the target, so the destination dir must
        // exist. With CLAUDE_CONFIG_DIR set, ~/.claude.json relocates to
        // $CLAUDE_CONFIG_DIR/.claude.json, whose dir may not exist yet on a fresh/
        // relocated config — create it so the trust write doesn't throw.
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, root.ToJsonString(IndentedJsonOpts));

        try {
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch {
                /* best-effort */
            }

            throw;
        }
    }

    static List<string> ReadMcpJsonServerNames(string worktreePath) {
        var mcpJsonPath = Path.Combine(worktreePath, ".mcp.json");

        if (!File.Exists(mcpJsonPath)) return [];

        try {
            var parsed  = JsonNode.Parse(File.ReadAllText(mcpJsonPath));
            var servers = parsed?["mcpServers"]?.AsObject();

            return servers is null ? [] : [..servers.Select(kv => kv.Key)];
        } catch {
            return [];
        }
    }

    internal static void WriteMcpConfig(string sourceRepoPath, string worktreePath) {
        var claudeJsonPath = ClaudePaths.UserConfigJson();

        if (!File.Exists(claudeJsonPath)) return;

        var root = JsonNode.Parse(File.ReadAllText(claudeJsonPath));

        // Claude keys projects[] by the normalised path (forward slashes on Windows) —
        // same reasoning as the trust write in TrustWorktreeInClaudeConfig. Fall back to
        // the raw path for entries written by older kcap builds or by hand.
        var sourceKey = NormalizeClaudeProjectKey(sourceRepoPath);

        var servers = root?["projects"]?[sourceKey]?["mcpServers"]?.AsObject()
         ?? root?["projects"]?[sourceRepoPath]?["mcpServers"]?.AsObject();

        if (servers is null || servers.Count == 0) return;

        var mcpJsonPath = Path.Combine(worktreePath, ".mcp.json");

        // Read existing .mcp.json if present (e.g. committed to git)
        JsonObject merged;

        if (File.Exists(mcpJsonPath)) {
            var existing = JsonNode.Parse(File.ReadAllText(mcpJsonPath));
            merged = existing?["mcpServers"]?.AsObject() ?? new JsonObject();
        } else {
            merged = new JsonObject();
        }

        // Add servers from ~/.claude.json (don't overwrite repo-committed ones)
        foreach (var (name, value) in servers) {
            if (!merged.ContainsKey(name) && value is not null) {
                var clone = value.DeepClone().AsObject();
                clone.Remove("env");
                merged[name] = clone;
            }
        }

        var wrapper = new JsonObject { ["mcpServers"] = merged };
        File.WriteAllText(mcpJsonPath, wrapper.ToJsonString(IndentedJsonOpts));
    }

    // === LoggerMessage source-generated methods ===

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to overlay .claude settings for agent {AgentId} (continuing)")]
    partial void LogOverlayFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write .mcp.json for agent {AgentId} (continuing)")]
    partial void LogMcpConfigFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to pre-trust worktree for agent {AgentId} (continuing)")]
    partial void LogTrustWorktreeFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to merge tool permissions for agent {AgentId} (continuing)")]
    partial void LogToolPermissionsFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove claude project symlink for agent {AgentId}")]
    partial void LogCleanupSymlinkFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove mcp config for agent {AgentId}")]
    partial void LogCleanupMcpConfigFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP allowlist entry '{Name}' is not a kcap-owned server — skipping (agent {AgentId})")]
    partial void LogAllowlistEntryUnknown(string name, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP allowlist entry '{Name}' can start flows — stripped (agent {AgentId})")]
    partial void LogAllowlistEntryStripped(string name, string agentId);
}
