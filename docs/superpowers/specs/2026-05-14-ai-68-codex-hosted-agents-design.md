# AI-68 — Hosted Codex agents on macOS and Linux

**Status:** design approved, pending implementation plan
**Linear:** [AI-68](https://linear.app/kurrent/issue/AI-68)
**Follow-ups:** [AI-632](https://linear.app/kurrent/issue/AI-632) (PR review), [AI-633](https://linear.app/kurrent/issue/AI-633) (sandbox/approval selection)
**Out of scope (separate tracking):** [AI-72](https://linear.app/kurrent/issue/AI-72) (Windows)

## 1. Architecture overview

The CLI binary already carries the Codex-side pieces — `CodexCliRunner` (headless title / what's-done), `CodexPaths` (session discovery), `CodexHookCommand` (hook dispatch), and `--codex` plugin install. What's missing is **the daemon-side path**: hosted-agent launch is still Claude-only and the local permission bridge speaks only Claude's wire shape.

Vendor-aware structure inside `kapacitor.Daemon`:

```
ServerConnection.OnLaunchAgent(LaunchAgentCommand)
        │  cmd.Vendor ∈ {"claude", "codex"}
        ▼
AgentOrchestrator.HandleLaunchAgent
   │  (cap check, repo-allow check, worktree create, attachment download,
   │   common env, AgentInstance bookkeeping, PTY spawn, status/heartbeat,
   │   cleanup — all VENDOR-NEUTRAL)
   │
   │  IHostedAgentLauncher  ← selected by vendor
   ├──▶ ClaudeLauncher       (claude CLI, ~/.claude.json trust, .claude/ overlay,
   │                         project-dir symlink, .mcp.json writer)
   └──▶ CodexLauncher        (codex CLI, ~/.codex/config.toml trust via Tomlyn,
                             no MCP file overlay needed)

LocalPermissionBridge
   POST /{token}/claude/permission-request   → Claude wire shape
   POST /{token}/codex/permission-request    → Codex wire shape
```

Three knock-on changes outside the daemon:

1. `LaunchAgentCommand` + `AgentRunStarted` get a required `Vendor` field.
2. `DaemonConfig.ClaudePath` is joined by `CodexPath`. Defaults `"claude"` / `"codex"` on `PATH`.
3. `kapacitor codex-hook` `PermissionRequest` stops echoing the stub; when `KAPACITOR_DAEMON_URL` is set it bounces through `{KAPACITOR_DAEMON_URL}/codex/permission-request` and returns the bridge's decision.

## 2. Spike — Codex pre-trust mechanism (confirmed)

Verified against codex-cli 0.130.0 on macOS:

`~/.codex/config.toml` uses TOML with a per-directory table:

```toml
[projects."/absolute/path/to/dir"]
trust_level = "trusted"
```

MCP servers also live in the same file (`[mcp_servers.<name>]`). There is **no** separate `~/.codex.json`. Consequence: Codex needs **no `.mcp.json` overlay** like Claude does — the spawned process inherits the user's MCP servers directly from `~/.codex/config.toml`.

Codex's interactive launch surface (the daemon needs these):

- `codex` (no subcommand, interactive TUI) with optional `[PROMPT]` positional
- `-m, --model <MODEL>`
- `-s, --sandbox <read-only | workspace-write | danger-full-access>`
- `-a, --ask-for-approval <untrusted | on-request | never>` — `on-request` is what triggers `PermissionRequest` hooks
- `-C, --cd <DIR>`
- `-c model_reasoning_effort="<low|medium|high|xhigh>"` — Codex's "effort" knob
- `--no-alt-screen` — disables alternate-screen terminal mode, safer for PTY relay through multiplexers

## 3. Wire format changes

### 3.1 `LaunchAgentCommand` (Models.cs)

Add a required `Vendor`:

```csharp
public readonly record struct LaunchAgentCommand(
        string             AgentId,
        string?            Prompt,
        string             Model,
        string?            Effort,
        string             RepoPath,
        string[]?          Tools,
        string[]?          AttachmentIds,
        LaunchKind         Kind    = LaunchKind.Default,
        ReviewLaunchInfo?  Review  = null,
        string?            BaseRef = null,
        string             Vendor  = "claude"   // NEW — required on wire; the default exists for
                                                // C# call-site ergonomics. Server always sends
                                                // explicit vendor.
    );
```

Daemon validates at the top of `HandleLaunchAgent`:

```csharp
if (cmd.Vendor is not ("claude" or "codex")) {
    await _server.LaunchFailedAsync(cmd.AgentId, $"Unknown vendor: {cmd.Vendor}");
    return;
}
```

### 3.2 `AgentRunStarted` (Models.cs)

```csharp
record AgentRunStarted(
        string? Prompt,
        string? Model,
        string? Effort,
        string? RepoPath,
        string? WorktreePath,
        string  Vendor          // NEW — always written by daemon
    );
```

Server-side persistence stores it on the run record so the dashboard can render a vendor badge without joining back to the launch command.

### 3.3 `LocalPermissionBridge` URLs

Old: `POST /{token}/permission-request`.
New: `POST /{token}/{vendor}/permission-request`.

`BaseUrl` exposed to spawned agents (`http://127.0.0.1:{port}/{token}`) is unchanged — the CLI hook command appends `/{vendor}/permission-request` itself.

### 3.4 `DaemonConfig` (DaemonConfig.cs)

```csharp
public string ClaudePath { get; set; } = "claude";
public string CodexPath  { get; set; } = "codex";   // NEW
```

`KapacitorPath` is unchanged — what's-done generation is vendor-neutral.

## 4. `IHostedAgentLauncher` interface and impls

A single internal interface in `kapacitor.Daemon.Services`. The surface is the smallest possible — only the vendor-specific bits.

### 4.1 Interface

```csharp
internal interface IHostedAgentLauncher {
    /// <summary>Vendor token this launcher handles ("claude" or "codex").</summary>
    string Vendor { get; }

    /// <summary>
    /// Absolute path or bare command for the CLI. Pulled from DaemonConfig at construction.
    /// </summary>
    string CliPath { get; }

    /// <summary>
    /// Per-vendor preparation BEFORE the PTY is spawned. Implementations:
    ///   • Overlay vendor-specific settings dir from source repo into worktree
    ///   • Pre-trust the worktree path in the vendor's config file
    ///   • Write any vendor-specific config (MCP, etc.)
    ///   • Merge dialog-selected tools into vendor-specific permission shape
    /// Best-effort: implementations swallow filesystem errors and log; they do
    /// NOT throw, so launch never blocks on a settings-overlay glitch.
    /// </summary>
    void Prepare(LauncherContext ctx);

    /// <summary>Build the argv array passed to the CLI.</summary>
    LaunchArgs BuildArgs(LauncherContext ctx);

    /// <summary>
    /// Per-vendor cleanup AFTER the agent exits / is stopped. Claude removes the
    /// ~/.claude/projects symlink; Codex has nothing to undo because the trust
    /// entry is intentionally persistent across worktree re-use.
    /// </summary>
    void Cleanup(AgentInstance agent);
}

internal sealed record LauncherContext(
        string                  AgentId,
        string                  SourceRepoPath,
        WorktreeInfo            Worktree,
        string?                 Prompt,
        string                  Model,
        string?                 Effort,
        string[]?               Tools,
        bool                    IsReview,
        ReviewLaunchInfo?       Review,
        ReviewLaunchBuilder.Result? ReviewLaunch
    );

/// <summary>argv + optional cleanup-time temp file path (e.g. ReviewLaunch.McpConfigPath).</summary>
internal readonly record struct LaunchArgs(string[] Args, string? McpConfigPath);
```

### 4.2 `ClaudeLauncher` — extracted from current orchestrator code

Pulls in (verbatim, no behavior change) from `AgentOrchestrator.cs`:

- `OverlayDirectory`, `SymlinkClaudeProjectDir`, `RemoveClaudeProjectSymlink`
- `WriteMcpConfig`, `ReadMcpJsonServerNames`
- `TrustWorktreeInClaudeConfig` (with `TrustWriteLock`, `LoadJsonObject`, `WriteJsonAtomic`)
- `MergeToolPermissions`
- The Claude-args-building section of `HandleLaunchAgent` (lines 273–304 today)

`Vendor => "claude"`, `CliPath => _config.ClaudePath`, `Cleanup` removes the `~/.claude/projects/<worktree-hash>` symlink.

### 4.3 `CodexLauncher` — new

`Prepare(ctx)`:

- `OverlayDirectory(source/.codex, worktree/.codex)` — lifted to a shared static helper.
- `CodexConfigWriter.TrustWorktree(ctx.Worktree.Path, logger)` — adds `[projects."<abs-worktree>"] trust_level = "trusted"` to `~/.codex/config.toml`. Details in section 5.
- No MCP file write (Codex reads MCP from `~/.codex/config.toml`).
- No tool-permission merge in v1. If `Tools` is non-empty, log at Debug so we notice if the UI starts passing them for Codex.

`BuildArgs(ctx)`:

For default launches:

- `--cd <worktree-path>` — keeps Codex anchored even if env CWD drifts
- `--sandbox workspace-write`
- `--ask-for-approval on-request`
- `-m <model>` if `Model` is set
- `-c model_reasoning_effort="<effort>"` with the mapping: Claude's `max` → Codex's `xhigh`, `low|medium|high` pass through. Skipped when `Effort` is null or `"auto"`.
- `--no-alt-screen`
- `--` then `Prompt` positional, if non-empty.

Review launches: out of scope for v1 — orchestrator fails the launch with `"PR review for Codex is not yet supported"` before calling into the launcher. Tracked in [AI-632](https://linear.app/kurrent/issue/AI-632).

`Cleanup`: no-op. `[projects."<worktree>"]` trust entries stay in `~/.codex/config.toml` because worktree paths are unique per run, cumulative entries are harmless, and leaving them simplifies re-launch debugging. Periodic GC of orphan entries is out of scope.

### 4.4 Orchestrator changes

`AgentOrchestrator` becomes vendor-neutral:

- Constructor takes `IReadOnlyDictionary<string, IHostedAgentLauncher>` injected by the composition root.
- `HandleLaunchAgent`:
  - Validate `cmd.Vendor`.
  - Look up `_launchers[cmd.Vendor]`.
  - Do the common prep (worktree create, attachment download, vendor-agnostic env vars).
  - Call `launcher.Prepare(ctx)` (best-effort try/catch with vendor-aware log).
  - Call `launcher.BuildArgs(ctx)`.
  - Spawn PTY with `launcher.CliPath` + args + env.
- `CleanupAgentAsync` calls `launcher.Cleanup(agent)` instead of the inlined Claude-symlink call.
- Env vars (`KAPACITOR_RENDERED_AGENT`, `KAPACITOR_AGENT_ID`, `KAPACITOR_URL`, `KAPACITOR_DAEMON_URL`) stay in the orchestrator.

### 4.5 Composition root

In `Program.cs` (daemon entry point), the DI container registers:

```csharp
services.AddSingleton<IHostedAgentLauncher, ClaudeLauncher>();
services.AddSingleton<IHostedAgentLauncher, CodexLauncher>();
services.AddSingleton<IReadOnlyDictionary<string, IHostedAgentLauncher>>(sp =>
    sp.GetServices<IHostedAgentLauncher>().ToDictionary(l => l.Vendor));
```

## 5. `CodexConfigWriter` — Tomlyn-based pre-trust

Single new file `src/Kapacitor.Daemon/Services/CodexConfigWriter.cs`.

### 5.1 Tomlyn dependency

Add to `Directory.Packages.props`:

```xml
<PackageVersion Include="Tomlyn" Version="0.20.0" />
```

And to `src/Kapacitor.Daemon/Kapacitor.Daemon.csproj`:

```xml
<PackageReference Include="Tomlyn" />
```

(Exact version pinned at implementation time.)

After adding the dep, the implementation step **must** run `dotnet publish -c Release` and grep for `IL3050|IL2026`. This is non-negotiable.

### 5.2 Writer

The model is intentionally narrow — we read the root as `TomlTable` (Tomlyn's dynamic dict) and only mutate the `projects` subtable. Every other root key (`model`, `[mcp_servers.*]`, `[plugins."..."]`, `[marketplaces.*]`, ad-hoc user keys) is preserved through round-trip — but Tomlyn does **not** preserve user comments or original formatting. We accept that loss in exchange for AOT-safe parsing.

```csharp
internal static partial class CodexConfigWriter {
    static readonly Lock _writeLock = new();

    public static void TrustWorktree(string worktreePath, ILogger logger) {
        lock (_writeLock) {
            var configPath = Path.Combine(CodexPaths.Home, "config.toml");

            TomlTable root;
            if (File.Exists(configPath)) {
                try {
                    root = Toml.ToModel(File.ReadAllText(configPath));
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to parse {Path}; aborting pre-trust", configPath);
                    return;
                }
            } else {
                root = new TomlTable();
            }

            if (root["projects"] is not TomlTable projects) {
                projects         = new TomlTable();
                root["projects"] = projects;
            }

            if (projects[worktreePath] is not TomlTable entry) {
                entry                  = new TomlTable();
                projects[worktreePath] = entry;
            }

            var alreadyTrusted = entry["trust_level"] is string s &&
                                 string.Equals(s, "trusted", StringComparison.Ordinal);

            if (alreadyTrusted) return;

            entry["trust_level"] = "trusted";

            try {
                WriteTomlAtomic(configPath, root);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to write {Path}; pre-trust not persisted", configPath);
            }
        }
    }

    static void WriteTomlAtomic(string path, TomlTable root) {
        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, Toml.FromModel(root));

        try {
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }
}
```

### 5.3 Source-gen fallback

`Toml.ToModel` / `Toml.FromModel` operate on `TomlTable`, not user POCOs — so reflection should not be engaged. The implementation step verifies this via the AOT publish grep. If `IL3050` / `IL2026` warnings appear, the fallback is a strongly-typed model gated on a `[TomlSerializable]` partial `TomlSerializerContext`. Decision deferred to implementation.

### 5.4 Failure semantics

Mirrors the Claude pre-trust path: every error path logs at Warning and continues. The orchestrator wraps `launcher.Prepare(ctx)` in a try/catch so a config-file glitch cannot block launch. If `trust_level` is not set, Codex's first run hits its own trust prompt — visible in the PTY relay, recoverable by re-launch.

## 6. `LocalPermissionBridge` per-vendor routing

The bridge keeps its single-listener / single-token / SignalR-fanout design. Only the URL match and the response-shape construction become vendor-aware.

### 6.1 URL routing

Path expected: `/{token}/{vendor}/permission-request`. The handler asserts vendor ∈ {`claude`, `codex`} and 404s anything else (including the legacy unprefixed path, which catches incomplete migration).

### 6.2 Server SignalR call

The bridge invokes `server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct)`. v1 plan: add a `string vendor` parameter to the hub method and store it on the permission-request row, so the dashboard can render vendor-appropriate prompts without joining back to the agent run.

Fallback if the server-side change is too entangled for the same Linear issue: omit the vendor parameter — the server already knows vendor via `AgentRunStarted`. Decision noted; the daemon-side bridge can pass vendor regardless.

### 6.3 Vendor-shaped responses

Claude response (current):

```json
{ "hookSpecificOutput": {
    "hookEventName": "PermissionRequest",
    "decision": { "behavior": "allow", "applyPermissions": ..., "updatedInput": ... }
}}
```

Codex response (new): same `hookSpecificOutput`/`hookEventName`/`decision`, but `decision` carries only `behavior`. If the server's `PermissionDecision` includes `applyPermissions` or `updatedInput` the Codex branch silently drops them (Debug-log).

```csharp
static string BuildHookResponseJson(PermissionDecision decision, string vendor) =>
    vendor switch {
        "claude" => BuildClaudeResponse(decision),
        "codex"  => BuildCodexResponse(decision),
        _        => throw new InvalidOperationException($"Unsupported vendor: {vendor}")
    };
```

`BuildClaudeResponse` is the current builder verbatim. `BuildCodexResponse` strips to `behavior` only.

### 6.4 Request parsing for Codex

Extract `session_id` (required) with the same dashless normalization Claude uses. Forward the rest of the payload to the server's hub method as opaque JSON — the daemon does not need to interpret Codex's command/tool field names. The bridge implementation step should log one real Codex `PermissionRequest` payload to confirm field shapes, then proceed with opaque forwarding.

## 7. CLI Codex hook → daemon bridge bounce

`src/kapacitor/Commands/CodexHookCommand.cs` `HandlePermissionRequest` becomes two branches.

### 7.1 Detection

```csharp
static async Task<int> HandlePermissionRequest(string baseUrl, JsonNode node) {
    var daemonUrl = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");

    return daemonUrl is null
        ? await HandlePermissionRequestStub(baseUrl, node)
        : await HandlePermissionRequestViaBridge(daemonUrl, node);
}
```

### 7.2 Stub branch (unchanged)

User-launched Codex sessions:

- POST to `{baseUrl}/hooks/permission-request/codex` (informational).
- Print `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}` to stdout and return 0.

### 7.3 Bridge branch (new)

Daemon-launched (hosted) Codex sessions:

- POST `node.ToJsonString()` to `{daemonUrl}/codex/permission-request`.
- The HTTP call blocks until the user decides on the dashboard. Timeout is intentionally infinite on the CLI side; the daemon owns the lifetime.
- On success: write the bridge's response body verbatim to stdout, return 0.
- **Skip** the server-side informational POST in this branch. The daemon's SignalR `RequestPermission` call already records the request server-side.
- On error (connection refused / 500 / cancellation): fall back to the allow-stub so the agent does not hang. Log to stderr.

```csharp
static async Task<int> HandlePermissionRequestViaBridge(string daemonUrl, JsonNode node) {
    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    try {
        using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync($"{daemonUrl}/codex/permission-request", content);

        if (!resp.IsSuccessStatusCode) {
            Console.Error.WriteLine($"[kapacitor] codex-hook permission-request bridge: HTTP {(int)resp.StatusCode}");
            return EmitAllowStub();
        }

        var body = await resp.Content.ReadAsStringAsync();
        Console.Write(body);
        return 0;
    } catch (Exception ex) {
        Console.Error.WriteLine($"[kapacitor] codex-hook permission-request bridge error: {ex.Message}");
        return EmitAllowStub();
    }
}

static int EmitAllowStub() {
    var response = new JsonObject {
        ["hookSpecificOutput"] = new JsonObject {
            ["hookEventName"] = "PermissionRequest",
            ["decision"]      = new JsonObject { ["behavior"] = "allow" }
        }
    };
    Console.Write(response.ToJsonString());
    return 0;
}
```

## 8. Tests

### 8.1 `LaunchAgentCommandWireFormatTests` (extend)

- `Vendor_field_round_trips_through_signalr_jsonprotocol`
- `Vendor_default_is_claude_when_constructor_omits_value` (ergonomic default; not a wire-compat assertion)
- `AgentRunStarted_vendor_serialises_into_json_body`

### 8.2 `CodexConfigWriterTests` (new)

Uses a scoped `HOME` so the real `~/.codex/config.toml` is untouched.

- `Writes_initial_projects_table_when_config_toml_missing`
- `Adds_entry_to_existing_config_preserving_other_tables`
- `Updates_trust_level_if_present_but_not_trusted`
- `No_op_when_trust_level_already_trusted`
- `Atomic_rename_leaves_no_tmp_files`
- `Concurrent_writers_serialise_safely`
- `Malformed_existing_config_is_skipped_not_overwritten`

### 8.3 `LocalPermissionBridgeTests` (extend)

- `Claude_path_returns_claude_response_shape`
- `Codex_path_returns_codex_response_shape`
- `Legacy_path_without_vendor_returns_404`
- `Unknown_vendor_returns_404`
- `Codex_path_strips_apply_permissions_from_server_decision`

### 8.4 `CodexHookCommandTests` (extend)

- `PermissionRequest_with_daemon_url_set_posts_to_bridge_and_forwards_response_to_stdout`
- `PermissionRequest_with_daemon_url_falls_back_to_allow_on_500`
- `PermissionRequest_with_daemon_url_falls_back_to_allow_on_connection_refused`
- `PermissionRequest_without_daemon_url_still_uses_legacy_stub`
- `PermissionRequest_with_daemon_url_does_not_double_post_to_server_hooks_endpoint`

### 8.5 `AgentOrchestratorVendorTests` (new)

`IPtyProcessFactory` and `IHostedAgentLauncher` substituted with spies.

- `Launch_with_vendor_claude_calls_claude_launcher`
- `Launch_with_vendor_codex_calls_codex_launcher`
- `Launch_with_unknown_vendor_emits_launch_failed_and_does_not_spawn_pty`
- `Launch_review_kind_with_vendor_codex_emits_launch_failed`
- `Cleanup_calls_vendor_specific_cleanup_method`

### 8.6 `CodexLauncherTests` (new)

- `BuildArgs_includes_workspace_write_sandbox_and_on_request_approval`
- `BuildArgs_maps_effort_max_to_xhigh`
- `BuildArgs_passes_low_medium_high_through_unchanged`
- `BuildArgs_omits_effort_when_null_or_auto`
- `BuildArgs_appends_prompt_after_double_dash_when_present`
- `BuildArgs_emits_no_alt_screen_flag`
- `Prepare_overlays_codex_settings_dir_from_source_repo`
- `Prepare_invokes_codex_config_writer_with_worktree_path`
- `Prepare_logs_and_swallows_filesystem_errors`

### 8.7 Integration test placeholder

`test/kapacitor.Tests.Integration/` does not get a new Codex integration test in this PR — existing daemon integration tests cover the lifecycle path that the launchers plug into. The end-to-end smoke test on macOS + Linux with a real `codex` binary is a manual PR-time step (one of AI-68's acceptance checkboxes).

### 8.8 AOT verification gate

Implementation step must run:

```bash
dotnet publish src/kapacitor/kapacitor.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Zero matches required.

## 9. Docs

Per CLAUDE.md's "README sync on CLI changes" rule:

- `README.md`
  - `kapacitor agent start`: add a "Hosted Codex" subsection.
  - `kapacitor codex-hook`: note that `PermissionRequest` bounces through the local daemon when `KAPACITOR_DAEMON_URL` is set.
  - Daemon config section: document `ClaudePath` and `CodexPath`.
  - Quick-start: one-line mention that the daemon supports both vendors.
- `src/Kapacitor.Core/Resources/help-codex-hook.txt` — daemon-bridge behaviour under `PermissionRequest`.

No CLAUDE.md changes — existing "README sync" and AOT verification rules already cover the new work patterns.

## 10. File touch list

**Modify**

- `src/Kapacitor.Core/Models.cs` — `Vendor` on `LaunchAgentCommand` + `AgentRunStarted`; `JsonSerializerContext` entries
- `src/Kapacitor.Daemon/DaemonConfig.cs` — `CodexPath`
- `src/Kapacitor.Daemon/Services/AgentOrchestrator.cs` — extract per-vendor blocks; route via `IHostedAgentLauncher`; vendor validation
- `src/Kapacitor.Daemon/Services/LocalPermissionBridge.cs` — URL split, vendor-shaped response builders
- `src/kapacitor/Commands/CodexHookCommand.cs` — `PermissionRequest` bridge branch
- `src/kapacitor/Program.cs` — register `ClaudeLauncher` + `CodexLauncher` and the vendor-keyed dictionary
- `Directory.Packages.props` — add `Tomlyn` package version
- `src/Kapacitor.Daemon/Kapacitor.Daemon.csproj` — reference `Tomlyn`
- `README.md`
- `src/Kapacitor.Core/Resources/help-codex-hook.txt`
- `test/kapacitor.Tests.Unit/LaunchAgentCommandWireFormatTests.cs`
- `test/kapacitor.Tests.Unit/LocalPermissionBridgeTests.cs`
- `test/kapacitor.Tests.Unit/CodexHookCommandTests.cs`

**Create**

- `src/Kapacitor.Daemon/Services/IHostedAgentLauncher.cs`
- `src/Kapacitor.Daemon/Services/ClaudeLauncher.cs`
- `src/Kapacitor.Daemon/Services/CodexLauncher.cs`
- `src/Kapacitor.Daemon/Services/CodexConfigWriter.cs`
- `test/kapacitor.Tests.Unit/CodexConfigWriterTests.cs`
- `test/kapacitor.Tests.Unit/AgentOrchestratorVendorTests.cs`
- `test/kapacitor.Tests.Unit/CodexLauncherTests.cs`
- `docs/superpowers/specs/2026-05-14-ai-68-codex-hosted-agents-design.md` (this file)

**No change**

- `src/Kapacitor.Core/CodexCliRunner.cs` (already complete)
- `src/Kapacitor.Core/CodexPaths.cs` (already complete)
- `src/Kapacitor.Daemon/Pty/*` (vendor-neutral on macOS/Linux)
- `src/Kapacitor.Daemon/Services/EvalRunner.cs` (vendor-neutral via existing `CodexCliRunner` integration)
- Session→agent late binding (already vendor-neutral)

## 11. Out of scope

- **Windows hosted Codex** — tracked in [AI-72](https://linear.app/kurrent/issue/AI-72). The `IHostedAgentLauncher` abstraction is the seam for that future work.
- **PR-review-kind hosted Codex launches** — tracked in [AI-632](https://linear.app/kurrent/issue/AI-632). v1 fails fast with `"PR review for Codex is not yet supported"`.
- **Per-MCP propagation from source repo to `~/.codex/config.toml`** — out of scope. Worktrees inherit the user's global MCP servers from `~/.codex/config.toml`.
- **Codex sandbox / approval override via launch dialog** — tracked in [AI-633](https://linear.app/kurrent/issue/AI-633). v1 hardcodes `workspace-write` + `on-request`.
- **Cumulative trust entry GC** — `[projects."<worktree>"]` entries accumulate in `~/.codex/config.toml` over time. Harmless; out of scope.
- **Tomlyn fallback to typed source-gen context** — only invoked if `dotnet publish` flags AOT warnings.

## 12. Acceptance mapping (AI-68)

- [x] Pre-implementation spike: confirmed `[projects."<path>"] trust_level = "trusted"` in `~/.codex/config.toml` (section 2)
- [ ] `HostedAgentLaunch` accepts and routes `vendor: codex` (sections 3.1, 4.4)
- [ ] Daemon launches Codex in a fresh worktree with correct overlay + pre-trust (sections 4.3, 5)
- [ ] `CodexCliRunner` produces a working title — already done
- [ ] Eval pipeline works against a Codex-hosted session — already vendor-neutral
- [ ] `PermissionRequest` round-trip through `LocalPermissionBridge` (sections 6, 7)
- [ ] Terminal output streams to dashboard; user input from dashboard appears in Codex TUI — PTY relay is vendor-neutral
- [ ] Manual smoke test on macOS and Linux — PR-time checklist item
