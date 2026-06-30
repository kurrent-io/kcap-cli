# Hosted Codex PR Review Support — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a hosted Codex agent review a PR with the same `kcap-review` MCP context the Claude path already gets.

**Architecture:** Make the shared `ReviewLaunchBuilder` vendor-aware and take an explicit kcap-CLI path; build a vendor-neutral MCP-server descriptor. The Claude launcher keeps writing a temp `--mcp-config` JSON; the Codex launcher injects the same server via ephemeral `-c mcp_servers.kcap-review.*` overrides and passes the rendered review prompt as Codex's initial prompt. Lift the orchestrator's Codex-review rejection.

**Tech Stack:** .NET 10 (NativeAOT), C#, TUnit on Microsoft Testing Platform, `System.Text.Json.Nodes`.

**Spec:** `docs/superpowers/specs/2026-06-29-ai-632-codex-pr-review-support-design.md`

## Global Constraints

- **NativeAOT-safe only.** No reflection-based serialization, no dynamic code. After code changes, verify zero IL3050/IL2026 warnings via `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (must print nothing).
- **JsonArray:** never use collection-expression `[a, b]` for `JsonArray` (lowers to `Add<T>()`, needs dynamic code). Build arrays with a loop using `JsonValue.Create(...)` or the `new JsonArray(node1, node2, …)` constructor.
- **MCP server name is `kcap-review`** (hyphen). Valid as a Claude JSON key and a TOML bare key.
- **Tests:** TUnit. Run a project as an executable: `dotnet run --project <test.csproj>`. Filter with `--treenode-filter '/*/*/<Class>/<Method>'` (glob), NOT `--filter`.
- **README sync:** user-facing CLI/behavior changes must update `README.md` in the same PR (line 413 currently says Codex review is unsupported).
- **Keep Claude review behavior byte-identical.** The Claude `--mcp-config`/`--system-prompt` argv and temp-file lifecycle must not change.

---

### Task 1: `ReviewLaunchBuilder` — vendor-aware, explicit CLI path, MCP descriptor

Refactor the shared builder so it (a) takes the kcap-CLI path explicitly instead of `Environment.ProcessPath` (which is `kcap-daemon` inside the daemon and breaks `mcp review`), (b) exposes a vendor-neutral MCP descriptor, and (c) only writes the Claude temp JSON. Fix all callers so the solution compiles with Claude behavior unchanged.

**Files:**
- Modify: `src/Capacitor.Cli.Core/Commands/ReviewLaunchBuilder.cs`
- Modify: `src/Capacitor.Cli/Commands/ReviewCommand.cs:48` (caller)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:341` (caller)
- Modify: `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs:71` (null-forgiving on now-nullable `McpConfigPath`)
- Test (create): `test/Capacitor.Cli.Tests.Unit/ReviewLaunchBuilderTests.cs`

**Interfaces:**
- Produces (consumed by Tasks 2 & 3):
  - `public record ReviewMcpServer(string Command, string[] Args, IReadOnlyDictionary<string, string> Env);` (nested in `ReviewLaunchBuilder`)
  - `public record ReviewLaunch(string? McpConfigPath, string SystemPrompt, ReviewMcpServer Mcp);` (nested in `ReviewLaunchBuilder`)
  - `public static Task<ReviewLaunch> BuildAsync(string vendor, string cliPath, string baseUrl, string owner, string repo, int prNumber)`
- Consumes: `EmbeddedResources.Load("prompt-review.txt")` (existing).

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/ReviewLaunchBuilderTests.cs`:

```csharp
using Capacitor.Cli.Core.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class ReviewLaunchBuilderTests {
    [Test]
    public async Task Claude_writes_mcp_config_file_and_populates_descriptor() {
        var launch = await ReviewLaunchBuilder.BuildAsync(
            vendor: "claude", cliPath: "/opt/kcap", baseUrl: "https://srv",
            owner: "acme", repo: "widgets", prNumber: 42);

        try {
            await Assert.That(launch.McpConfigPath).IsNotNull();
            await Assert.That(File.Exists(launch.McpConfigPath!)).IsTrue();
            var json = await File.ReadAllTextAsync(launch.McpConfigPath!);
            await Assert.That(json).Contains("kcap-review");
            await Assert.That(json).Contains("/opt/kcap");

            await Assert.That(launch.Mcp.Command).IsEqualTo("/opt/kcap");
            await Assert.That(launch.Mcp.Args).Contains("--owner");
            await Assert.That(launch.Mcp.Args).Contains("acme");
            await Assert.That(launch.Mcp.Args).Contains("42");
            await Assert.That(launch.Mcp.Env["KCAP_URL"]).IsEqualTo("https://srv");
            await Assert.That(launch.SystemPrompt).Contains("acme");
        } finally {
            if (launch.McpConfigPath is not null) File.Delete(launch.McpConfigPath);
        }
    }

    [Test]
    public async Task Codex_writes_no_file_and_populates_descriptor() {
        var launch = await ReviewLaunchBuilder.BuildAsync(
            vendor: "codex", cliPath: "/opt/kcap", baseUrl: "https://srv",
            owner: "acme", repo: "widgets", prNumber: 42);

        await Assert.That(launch.McpConfigPath).IsNull();
        await Assert.That(launch.Mcp.Command).IsEqualTo("/opt/kcap");
        await Assert.That(launch.Mcp.Args).Contains("review");
        await Assert.That(launch.Mcp.Args).Contains("widgets");
        await Assert.That(launch.Mcp.Env["KCAP_URL"]).IsEqualTo("https://srv");
        await Assert.That(launch.SystemPrompt).Contains("widgets");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail (compile error is the failure)**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/ReviewLaunchBuilderTests/*'`
Expected: build FAILS — `BuildAsync` has no 6-arg overload, `ReviewMcpServer`/`Mcp` don't exist.

- [ ] **Step 3: Rewrite `ReviewLaunchBuilder.cs`**

Replace the body of `src/Capacitor.Cli.Core/Commands/ReviewLaunchBuilder.cs` with:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Commands;

/// <summary>
/// Shared helper that builds a PR-review launch for a given vendor: the rendered
/// review system prompt (PR placeholders substituted) plus a vendor-neutral MCP
/// server descriptor pointing at <c>kcap mcp review</c>. For Claude it also writes
/// the temp <c>--mcp-config</c> JSON; Codex injects the same server via <c>-c</c>
/// overrides and needs no file. The kcap CLI path is passed in because inside the
/// daemon the running process is <c>kcap-daemon</c> (no <c>mcp review</c> subcommand).
/// </summary>
public static class ReviewLaunchBuilder {
    public record ReviewMcpServer(string Command, string[] Args, IReadOnlyDictionary<string, string> Env);

    public record ReviewLaunch(string? McpConfigPath, string SystemPrompt, ReviewMcpServer Mcp);

    public static async Task<ReviewLaunch> BuildAsync(
            string vendor, string cliPath, string baseUrl, string owner, string repo, int prNumber) {
        // Render the system prompt first. EmbeddedResources.Load can throw; building
        // the prompt before writing any temp file keeps the file's lifetime fully
        // inside the caller's try/finally so a throw never leaks a path-less file.
        var systemPrompt = EmbeddedResources.Load("prompt-review.txt")
            .Replace("{prNumber}", prNumber.ToString())
            .Replace("{owner}", owner)
            .Replace("{repo}", repo);

        var mcp = new ReviewMcpServer(
            Command: cliPath,
            Args: ["mcp", "review", "--owner", owner, "--repo", repo, "--pr", prNumber.ToString()],
            Env: new Dictionary<string, string> { ["KCAP_URL"] = baseUrl });

        string? configPath = null;

        if (vendor == "claude") {
            configPath = await WriteClaudeMcpConfigAsync(mcp);
        }

        return new ReviewLaunch(configPath, systemPrompt, mcp);
    }

    static async Task<string> WriteClaudeMcpConfigAsync(ReviewMcpServer mcp) {
        // Use the implicit string -> JsonValue conversion (cast to JsonNode?) rather
        // than JsonValue.Create / collection expressions, which lower to generic
        // Add<T> and trip NativeAOT (IL3050). This matches the existing pattern.
        var argsNode = new JsonArray();

        foreach (var a in mcp.Args) {
            argsNode.Add((JsonNode?)a);
        }

        var envNode = new JsonObject();

        foreach (var kv in mcp.Env) {
            envNode[kv.Key] = (JsonNode?)kv.Value;
        }

        var mcpConfig = new JsonObject {
            ["mcpServers"] = new JsonObject {
                ["kcap-review"] = new JsonObject {
                    ["command"] = mcp.Command,
                    ["args"]    = argsNode,
                    ["env"]     = envNode
                }
            }
        };

        var configPath = Path.Combine(Path.GetTempPath(), $"kcap-review-{Guid.NewGuid():N}.json");
        var json       = mcpConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, json);

        return configPath;
    }
}
```

- [ ] **Step 4: Fix the daemon caller**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, the `ReviewLaunch:` initializer (around line 340–342) currently reads:

```csharp
                ReviewLaunch: isReview && cmd.Review is { } reviewArgs
                    ? await ReviewLaunchBuilder.BuildAsync(_config.ServerUrl ?? "", reviewArgs.Owner, reviewArgs.Repo, reviewArgs.PrNumber)
                    : null
```

Change the `BuildAsync` call to pass vendor and the kcap-CLI path:

```csharp
                ReviewLaunch: isReview && cmd.Review is { } reviewArgs
                    ? await ReviewLaunchBuilder.BuildAsync(cmd.Vendor, _config.CapacitorPath, _config.ServerUrl ?? "", reviewArgs.Owner, reviewArgs.Repo, reviewArgs.PrNumber)
                    : null
```

- [ ] **Step 5: Fix the interactive CLI caller**

In `src/Capacitor.Cli/Commands/ReviewCommand.cs`, line 48 currently reads:

```csharp
        var launch = await ReviewLaunchBuilder.BuildAsync(baseUrl, owner, repo, prNumber);
```

Replace with (interactive `kcap review` is Claude-only; the running process *is* `kcap`):

```csharp
        var launch = await ReviewLaunchBuilder.BuildAsync(
            "claude", Environment.ProcessPath ?? "kcap", baseUrl, owner, repo, prNumber);
```

Then, on the line that adds the MCP config path (was line 59), the property is now `string?`; make it non-null-asserted since the Claude path always writes it:

```csharp
            psi.ArgumentList.Add(launch.McpConfigPath!);
```

- [ ] **Step 6: Fix the Claude launcher**

In `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs`, the review branch (around line 67–73) assigns and adds `launch.McpConfigPath`. `mcpConfigPath = launch.McpConfigPath;` is fine (both `string?`). On the `args.Add(launch.McpConfigPath)` line (was line 71), assert non-null because Claude reviews always write the file:

```csharp
            args.Add(launch.McpConfigPath!);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/ReviewLaunchBuilderTests/*'`
Expected: PASS (2 tests).

- [ ] **Step 8: Run the existing vendor tests to confirm no Claude regression**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/AgentOrchestratorVendorTests/*'`
Expected: PASS (all existing tests, including `Launch_review_kind_with_vendor_codex_emits_launch_failed`, still green — the gate is still in place at this point).

- [ ] **Step 9: Commit**

```bash
WT=/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/breezy-greeting-chipmunk
git -C "$WT" add src/Capacitor.Cli.Core/Commands/ReviewLaunchBuilder.cs \
  src/Capacitor.Cli/Commands/ReviewCommand.cs \
  src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs \
  src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs \
  test/Capacitor.Cli.Tests.Unit/ReviewLaunchBuilderTests.cs
git -C "$WT" commit -m "$(printf 'refactor(AI-632): vendor-aware ReviewLaunchBuilder with explicit CLI path\n\nThread the kcap CLI path (CapacitorPath in the daemon) through the\nbuilder instead of Environment.ProcessPath, which resolves to\nkcap-daemon in the daemon and has no mcp review subcommand. Expose a\nvendor-neutral MCP descriptor; write the temp JSON only for Claude.\n\nCo-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>')"
```

---

### Task 2: `CodexLauncher` review branch + TOML `-c` injection

Add a review branch to `CodexLauncher.BuildArgs` that injects the `kcap-review` MCP server via `-c` overrides and passes the rendered prompt as the initial prompt. No orchestrator change yet — the branch is unit-tested directly.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs` (add review branch + TOML helper)
- Test: `test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs` (add review tests + a review-context helper)

**Interfaces:**
- Consumes: `ReviewLaunchBuilder.ReviewLaunch` / `ReviewMcpServer` from Task 1; `LauncherContext.IsReview`, `LauncherContext.ReviewLaunch` (existing).
- Produces: argv where, for reviews, `McpConfigPath` is null and the args contain `-c mcp_servers.kcap-review.command=…`, `…args=[…]`, `…env={…}`, no `--system-prompt`, and a trailing `-- <prompt>`.

- [ ] **Step 1: Write the failing tests**

In `test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs`, add a review-context helper next to `NewCtxWith` and the tests below:

```csharp
    static LauncherContext NewReviewCtx(string prompt, string cliPath, string baseUrl) {
        var mcp = new ReviewLaunchBuilder.ReviewMcpServer(
            Command: cliPath,
            Args: ["mcp", "review", "--owner", "acme", "--repo", "widgets", "--pr", "42"],
            Env: new Dictionary<string, string> { ["KCAP_URL"] = baseUrl });

        return new(
            AgentId: "a-rev",
            SourceRepoPath: "/tmp/repo",
            Worktree: new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt: null,
            Model: "gpt-5.3-codex",
            Effort: null,
            Tools: null,
            IsReview: true,
            Review: new ReviewLaunchInfo("acme", "widgets", 42),
            ReviewLaunch: new ReviewLaunchBuilder.ReviewLaunch(McpConfigPath: null, SystemPrompt: prompt, Mcp: mcp));
    }

    [Test]
    public async Task BuildArgs_review_injects_kcap_review_mcp_server_via_config_overrides() {
        var result = NewLauncher().BuildArgs(NewReviewCtx("Review PR acme/widgets#42", "/opt/kcap", "https://srv"));
        var joined = string.Join(' ', result.Args);

        await Assert.That(joined).Contains("mcp_servers.kcap-review.command=\"/opt/kcap\"");
        await Assert.That(joined).Contains("mcp_servers.kcap-review.args=[\"mcp\",\"review\",\"--owner\",\"acme\",\"--repo\",\"widgets\",\"--pr\",\"42\"]");
        await Assert.That(joined).Contains("mcp_servers.kcap-review.env={KCAP_URL=\"https://srv\"}");
    }

    [Test]
    public async Task BuildArgs_review_passes_prompt_after_double_dash_and_no_system_prompt() {
        var result = NewLauncher().BuildArgs(NewReviewCtx("REVIEW PROMPT BODY", "/opt/kcap", "https://srv"));

        var dashIdx = Array.IndexOf(result.Args, "--");
        await Assert.That(dashIdx).IsGreaterThan(-1);
        await Assert.That(result.Args[dashIdx + 1]).IsEqualTo("REVIEW PROMPT BODY");
        await Assert.That(result.Args).DoesNotContain("--system-prompt");
    }

    [Test]
    public async Task BuildArgs_review_returns_null_mcp_config_path() {
        var result = NewLauncher().BuildArgs(NewReviewCtx("p", "/opt/kcap", "https://srv"));
        await Assert.That(result.McpConfigPath).IsNull();
    }

    [Test]
    public async Task BuildArgs_review_toml_escapes_quotes_and_backslashes_in_command() {
        var ctx = NewReviewCtx("p", "C:\\Program Files\\kcap\\kcap.exe", "https://srv");
        var joined = string.Join(' ', NewLauncher().BuildArgs(ctx).Args);
        await Assert.That(joined).Contains("mcp_servers.kcap-review.command=\"C:\\\\Program Files\\\\kcap\\\\kcap.exe\"");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/CodexLauncherTests/BuildArgs_review*'`
Expected: tests FAIL — the review branch doesn't exist, so the `-c mcp_servers.*` overrides are absent (and `--system-prompt` assertion is vacuously fine but the others fail).

- [ ] **Step 3: Add the review branch and TOML helper to `CodexLauncher.BuildArgs`**

In `src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs`, change `BuildArgs` so the review case is handled first, then the existing logic runs for non-review. Replace the current `BuildArgs` method (lines ~65–96) with:

```csharp
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

        if (!string.IsNullOrEmpty(ctx.Model)) {
            args.Add("-m");
            args.Add(ctx.Model);
        }

        args.Add("--no-alt-screen");
        args.Add("--");
        args.Add(launch.SystemPrompt);

        return new([.. args], McpConfigPath: null);
    }

    /// Encode a value as a TOML basic string: wrap in double quotes and escape
    /// backslashes and double quotes (covers Windows paths and arbitrary URLs).
    static string TomlString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
```

Add `using System.Linq;` at the top of the file if it is not already present (needed for `Select`).

- [ ] **Step 4: Run the review tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/CodexLauncherTests/BuildArgs_review*'`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full CodexLauncher suite to confirm no non-review regression**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/CodexLauncherTests/*'`
Expected: PASS (all tests).

- [ ] **Step 6: Commit**

```bash
WT=/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/breezy-greeting-chipmunk
git -C "$WT" add src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs \
  test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs
git -C "$WT" commit -m "$(printf 'feat(AI-632): CodexLauncher review branch with -c MCP injection\n\nInject the kcap-review MCP server via ephemeral -c mcp_servers.* TOML\noverrides and pass the rendered review prompt as Codex initial prompt.\nNo temp file, so nothing to clean up.\n\nCo-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>')"
```

---

### Task 3: Lift the orchestrator Codex-review rejection

Remove the hard gate so Codex review launches flow through the same validation and launcher path as Claude. Replace the rejection test with a gate-removal test (a fully offline happy-path orchestrator test is infeasible — see spec Testing section).

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs:239-243` (remove rejection)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorVendorTests.cs:234-267` (replace the rejection test)

**Interfaces:**
- Consumes: `CreateGitRepo()`, `BuildOrchestrator(...)`, `CaptureServerConnection`, `SpyHostedAgentLauncher`, `SpyPtyProcessFactory`, `orch.HandleLaunchAgentForTest(cmd)` (all existing in the test file).

- [ ] **Step 1: Replace the rejection test with a gate-removal test**

In `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorVendorTests.cs`, replace the entire `Launch_review_kind_with_vendor_codex_emits_launch_failed` test (lines ~234–267) with:

```csharp
    [Test]
    public async Task Launch_review_kind_with_vendor_codex_is_accepted_and_reaches_review_validation() {
        // A git repo with NO origin remote: a Codex review now passes the vendor
        // gate (which used to reject it) and fails later at origin validation —
        // the SAME point a Claude review would. Proves the gate is lifted.
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var codexSpy   = new SpyHostedAgentLauncher("codex", cliPath: "spy-codex");

            var launchers = new Dictionary<string, IHostedAgentLauncher> { ["codex"] = codexSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            var cmd = new LaunchAgentCommand(
                AgentId: "agent-r1",
                Prompt: null,
                Model: "gpt-5",
                Effort: null,
                RepoPath: repoPath,
                Tools: null,
                AttachmentIds: null,
                Vendor: "codex",
                Kind: LaunchKind.Review,
                Review: new ReviewLaunchInfo("acme", "widgets", 42)
            );

            await orch.HandleLaunchAgentForTest(cmd);

            await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
            // The old Codex-specific rejection is gone...
            await Assert.That(server.LaunchFailedCalls[0].Reason).DoesNotContain("PR review for Codex");
            // ...and it failed at the shared origin check instead.
            await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("origin");
            await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
        } finally {
            cleanup();
        }
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/AgentOrchestratorVendorTests/Launch_review_kind_with_vendor_codex_is_accepted_and_reaches_review_validation'`
Expected: FAIL — the rejection still fires, so `Reason` contains "PR review for Codex" (the `DoesNotContain` assertion fails).

- [ ] **Step 3: Remove the rejection gate**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, delete these lines (around 239–243):

```csharp
        if (isReview && cmd.Vendor == "codex") {
            await _server.LaunchFailedAsync(cmd.AgentId, "PR review for Codex is not yet supported");

            return;
        }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/AgentOrchestratorVendorTests/Launch_review_kind_with_vendor_codex_is_accepted_and_reaches_review_validation'`
Expected: PASS.

- [ ] **Step 5: Run the full vendor suite to confirm no regression**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter '/*/*/AgentOrchestratorVendorTests/*'`
Expected: PASS (all tests).

- [ ] **Step 6: Commit**

```bash
WT=/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/breezy-greeting-chipmunk
git -C "$WT" add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs \
  test/Capacitor.Cli.Tests.Unit/AgentOrchestratorVendorTests.cs
git -C "$WT" commit -m "$(printf 'feat(AI-632): accept LaunchKind.Review for vendor codex\n\nRemove the hard rejection so Codex reviews flow through the same\nvalidation and launcher path as Claude.\n\nCo-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>')"
```

---

### Task 4: README + full verification

Update the public docs and run the whole-repo gates (full test suites + AOT warning scan).

**Files:**
- Modify: `README.md:413`

- [ ] **Step 1: Update the README**

In `README.md`, line 413 currently reads:

```
PR review for hosted Codex agents is not yet supported (tracked in AI-632). The sandbox and approval-mode selectors in the launch dialog are also planned as a follow-up (AI-633).
```

Replace it with:

```
PR review is supported for hosted Codex agents as well as Claude — the same `kcap-review` MCP context is injected either way. The sandbox and approval-mode selectors in the launch dialog are planned as a follow-up (AI-633).
```

Then check the `## CLI commands` / `### PR review with full context` section (around line 251–259) and remove any Claude-only phrasing if it now misstates Codex support; if it only describes `kcap review` (the interactive Claude command), leave it.

- [ ] **Step 2: Run the full unit test suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all tests PASS.

- [ ] **Step 3: Run the integration test suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`
Expected: all tests PASS.

- [ ] **Step 4: Verify no AOT trimming warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: NO output (no IL3050/IL2026 warnings).

- [ ] **Step 5: Commit**

```bash
WT=/Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/breezy-greeting-chipmunk
git -C "$WT" add README.md
git -C "$WT" commit -m "$(printf 'docs(AI-632): README — hosted Codex PR review is now supported\n\nCo-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>')"
```

---

## Self-Review

**Spec coverage:**
- Vendor-aware `ReviewLaunchBuilder` + MCP descriptor + CLI-path fix → Task 1. ✓
- `CodexLauncher.BuildArgs` review branch (`-c` MCP injection, prompt as initial prompt, TOML encoding) → Task 2. ✓
- Lift `AgentOrchestrator` Codex-review rejection → Task 3. ✓
- Cleanup "free" (no temp file for Codex) → covered by Task 2 (`McpConfigPath: null`, no `Cleanup` change). ✓
- README line 413 → Task 4. ✓
- AOT-safety / no IL warnings → Task 4 Step 4 (and JsonArray loop in Task 1). ✓
- Testing strategy (narrow unit tests + gate-removal, no infeasible happy-path) → Tasks 1–3 tests. ✓
- Keep Claude behavior identical → Task 1 Steps 8, the temp-JSON shape preserved. ✓

**Placeholder scan:** No TBD/TODO; every code step shows complete code; every run step has an exact command and expected result.

**Type consistency:** `ReviewLaunchBuilder.ReviewLaunch(string? McpConfigPath, string SystemPrompt, ReviewMcpServer Mcp)` and `ReviewMcpServer(string Command, string[] Args, IReadOnlyDictionary<string,string> Env)` are used identically in Task 1 (definition + tests), Task 2 (`NewReviewCtx`, `BuildReviewArgs`), and consumed via `launch.Mcp` / `launch.SystemPrompt` / `launch.McpConfigPath`. `BuildAsync(string vendor, string cliPath, string baseUrl, string owner, string repo, int prNumber)` matches at both call sites and in tests. `TomlString` defined and used within `CodexLauncher`.
