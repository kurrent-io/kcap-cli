# AI-1124 Phase B: Vendor-Uniform Unattended Hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the daemon host a `LaunchKind.ReviewFlow` (unattended) sidecar on **Claude** — not just Codex — by making "unattended" a first-class launcher capability: Claude's `BuildArgs` gains an unattended branch (no permission prompts, no MCP servers), the capability is declared on `IHostedAgentLauncher`, and the orchestrator rejects an unattended launch for any vendor that can't honor it with a clear `LaunchFailed` message.

**Architecture:** Two seam-level changes in `kcap-cli`, each independently reviewable and each keeping the daemon unit suite green: (1) add `bool SupportsUnattended` to `IHostedAgentLauncher`, implement it on both launchers (both → `true`), and add the Claude unattended `BuildArgs` branch mirroring Codex's existing one; (2) a tiny pure `UnattendedLaunchPolicy` guard wired into `AgentOrchestrator.HandleLaunchAgent` that fails an unattended launch fast when the selected launcher can't run unattended. Server side (kcap-server, PR #909, merged) already sends `LaunchKind.ReviewFlow` + the definition's vendor/model in `LaunchAgentCommand`; no wire change. Issue: Linear **AI-1124** (Phase B of AI-1119).

**Tech Stack:** .NET 10 AOT CLI + daemon, TUnit on Microsoft Testing Platform.

## Global Constraints

- **No SignalR / wire-contract change.** `LaunchKind.ReviewFlow` (wire value 2) stays the unattended trigger; `LaunchAgentCommand`, `LaunchKind`, and hub method signatures are untouched. (Server compat: [[feedback_signalr_exact_signatures]].)
- **Codex behavior must not change.** `CodexLauncher.BuildArgs` for `IsReviewFlow` already emits `--ask-for-approval never` + `-c mcp_servers={}`; leave that exactly as-is. `CodexLauncher.SupportsUnattended => true`.
- **Claude unattended posture (from current Claude Code CLI docs, verified via claude-code-guide):** an unattended Claude sidecar in a daemon-owned, isolated worktree launches with `--permission-mode bypassPermissions` (no tool-permission prompts) and `--strict-mcp-config --mcp-config {"mcpServers":{}}` (loads ZERO MCP servers, so the reviewer can't recursively invoke `kcap-flows`). These are the exact flag spellings; `--strict-mcp-config` makes `--mcp-config` authoritative and ignores `~/.claude.json` / project `.mcp.json`. Known runtime caveat (document, do not code around): `bypassPermissions` refuses to start under root/sudo — the daemon must run the `claude` process as a non-root user (it normally does). This is out of scope to enforce here.
- **Only `LaunchKind.ReviewFlow` gets the unattended treatment.** `Default` (interactive) and `Review` (has its own `ReviewLaunch` path) Claude launches are unchanged. `ReviewFlow` is NOT `IsReview` — it flows through Claude's `else` branch in `BuildArgs`.
- **AOT-safe, no new packages.** No `JsonArray` collection expressions (emit the empty-MCP config as a constant string literal, not a built `JsonNode`). AOT warnings only surface on `dotnet publish -c Release` — run it in final verification.
- **Test framework:** TUnit, `OutputType Exe`, never add `Microsoft.NET.Test.Sdk`; all assertions `await`ed; filter with `--treenode-filter` (glob), not `--filter`.
- **Code style:** file-scoped namespaces, primary constructors, 4-space indent, aligned member columns; `[LoggerMessage]` source-gen for any new log (none expected). Match surrounding files.
- **Worktree:** all work in `/Users/alexey/dev/eventstore/kcap-cli-ai1124-unattended` on branch `alexeyzimarev/ai-1124-flows-phase-b-vendor-uniform-unattended-hosting`. Never edit the `kcap-server` `src/cli` submodule.
- **Running tests:** `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (append `--treenode-filter "/*/*/<Class>/*"` to filter). No Docker needed for the unit suite.

---

### Task 1: Unattended launcher capability + Claude unattended BuildArgs branch

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs` (add `SupportsUnattended` to the interface)
- Modify: `src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs` (implement `SupportsUnattended => true`)
- Modify: `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs` (implement `SupportsUnattended => true`; add the unattended branch in `BuildArgs`)
- Create: `test/Capacitor.Cli.Tests.Unit/ClaudeLauncherReviewFlowTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs` (add a `SupportsUnattended` assertion)

**Interfaces:**
- Consumes: `LauncherContext.IsReviewFlow` (already set by the orchestrator from `cmd.Kind == LaunchKind.ReviewFlow`).
- Produces (relied on by Task 2): `IHostedAgentLauncher.SupportsUnattended` (bool, get-only). Claude's `BuildArgs` for `IsReviewFlow` emits, in order: `--permission-mode bypassPermissions`, `--strict-mcp-config`, `--mcp-config {"mcpServers":{}}`, then the existing effort/model/prompt args.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/ClaudeLauncherReviewFlowTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudeLauncherReviewFlowTests {
    static ClaudeLauncher NewLauncher() =>
        new(new DaemonConfig { ClaudePath = "claude" }, NullLogger<ClaudeLauncher>.Instance);

    static LauncherContext NewCtx(bool isReviewFlow, string? prompt = "review this", string model = "sonnet") =>
        new(
            AgentId:       "a-1",
            SourceRepoPath:"/tmp/repo",
            Worktree:      new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt:        prompt,
            Model:         model,
            Effort:        null,
            Tools:         null,
            IsReview:      false,
            IsReviewFlow:  isReviewFlow,
            Review:        null,
            ReviewLaunch:  null
        );

    [Test]
    public async Task Review_flow_launch_bypasses_permissions() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--permission-mode");
        var i = Array.IndexOf(args, "--permission-mode");
        await Assert.That(args[i + 1]).IsEqualTo("bypassPermissions");
    }

    [Test]
    public async Task Review_flow_launch_loads_no_mcp_servers() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        await Assert.That(args).Contains("--mcp-config");

        // The --mcp-config value must parse to an empty mcpServers map so the reviewer
        // cannot recursively invoke kcap-flows.
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var parsed   = JsonNode.Parse(args[cfgIndex + 1])!.AsObject();
        await Assert.That(parsed["mcpServers"]!.AsObject().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Review_flow_launch_still_passes_model_and_prompt() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true, prompt: "the prompt", model: "opus")).Args;

        await Assert.That(args).Contains("--model");
        await Assert.That(args).Contains("opus");
        await Assert.That(args).Contains("--");
        await Assert.That(args[^1]).IsEqualTo("the prompt");
    }

    [Test]
    public async Task Non_review_flow_launch_is_unchanged_no_bypass_or_strict_mcp() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: false)).Args;

        await Assert.That(args).DoesNotContain("--permission-mode");
        await Assert.That(args).DoesNotContain("--strict-mcp-config");
        await Assert.That(args).DoesNotContain("--mcp-config");
    }

    [Test]
    public async Task Claude_launcher_supports_unattended() {
        await Assert.That(NewLauncher().SupportsUnattended).IsTrue();
    }
}
```

Add one assertion to `test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs` (reuse its existing `NewLauncher()` helper) — as a new `[Test]` method:

```csharp
    [Test]
    public async Task Codex_launcher_supports_unattended() {
        await Assert.That(NewLauncher().SupportsUnattended).IsTrue();
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeLauncherReviewFlowTests/*"`
Expected: build error — `IHostedAgentLauncher` has no `SupportsUnattended` (and, once that compiles, the four `BuildArgs` review-flow assertions fail because Claude doesn't emit the flags yet).

- [ ] **Step 3: Implement — interface capability**

In `src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs`, add to the interface (after `IsAvailable()`):

```csharp
    /// <summary>
    /// Whether this vendor's launcher can host a fully UNATTENDED agent
    /// (LaunchKind.ReviewFlow): one that runs to completion with no human in
    /// the loop — no tool-permission prompts — and cannot recursively invoke
    /// flow-starting MCP tools. The orchestrator refuses an unattended launch
    /// for a vendor that returns <c>false</c> (AI-1124). Both shipped launchers
    /// support it; a future vendor (e.g. Gemini, AI-899) may not.
    /// </summary>
    bool SupportsUnattended { get; }
```

- [ ] **Step 4: Implement — Codex capability (behavior unchanged)**

In `src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs`, add next to the other one-line members (after `CliPath`):

```csharp
    public bool SupportsUnattended => true;
```

(Codex's existing `IsReviewFlow` branch in `BuildArgs` — `--ask-for-approval never` + `-c mcp_servers={}` — is left exactly as-is.)

- [ ] **Step 5: Implement — Claude capability + unattended BuildArgs branch**

In `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs`, add the capability next to `CliPath`:

```csharp
    public bool SupportsUnattended => true;
```

Add the empty-MCP constant to the class (near the top, with the other `static readonly` fields):

```csharp
    // Strict + empty MCP config: --strict-mcp-config makes this authoritative (ignores
    // ~/.claude.json and project .mcp.json), and the empty map loads zero servers — so an
    // unattended review-flow reviewer cannot recursively invoke kcap-flows. Emitted as a
    // constant string (not a built JsonNode) to stay AOT-safe.
    const string EmptyMcpConfig = """{"mcpServers":{}}""";
```

In `BuildArgs`, extend the `else` branch (the non-review path, currently lines ~79-94). Prepend the unattended flags when `ctx.IsReviewFlow`, keeping the existing effort/model/prompt logic intact:

```csharp
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
                args.Add(EmptyMcpConfig);
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
```

- [ ] **Step 6: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ClaudeLauncherReviewFlowTests/*"` → all 5 PASS
Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CodexLauncherTests/*"` → all PASS (existing ReviewFlow tests unchanged + new SupportsUnattended assertion)

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/IHostedAgentLauncher.cs src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs test/Capacitor.Cli.Tests.Unit/ClaudeLauncherReviewFlowTests.cs test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs
git commit -m "feat(daemon): unattended launcher capability + Claude review-flow unattended args"
```

---

### Task 2: Orchestrator rejects unattended launch for a vendor that can't honor it

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/UnattendedLaunchPolicy.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (wire the guard into `HandleLaunchAgent`, right after launcher selection)
- Create: `test/Capacitor.Cli.Tests.Unit/UnattendedLaunchPolicyTests.cs`

**Interfaces:**
- Consumes: `IHostedAgentLauncher.SupportsUnattended` (Task 1), `IHostedAgentLauncher.Vendor`.
- Produces: `static string? UnattendedLaunchPolicy.RejectionReason(IHostedAgentLauncher launcher, bool isReviewFlow)` — returns a user-facing message when the launch must be rejected, else `null`.

Rationale (so the reviewer doesn't flag it as dead code): both shipped launchers return `SupportsUnattended => true`, so the reject branch is not hit by production vendors *today*. It is an explicit acceptance criterion of AI-1124 ("the daemon rejects unattended launches for vendors whose launcher can't honor the capability, with a clear error surfaced to the flow run") and the seam a future non-unattended vendor (Gemini, AI-899) plugs into — turning a silent forever-hang (agent waits on a permission prompt no one answers) into an immediate, legible `LaunchFailed`. Extracting the decision into a pure function is what makes it unit-testable without standing up the full orchestrator (`_server` / `_ptyFactory` / `_worktreeManager` / SignalR).

- [ ] **Step 1: Write the failing test**

Create `test/Capacitor.Cli.Tests.Unit/UnattendedLaunchPolicyTests.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

public class UnattendedLaunchPolicyTests {
    sealed class FakeLauncher(string vendor, bool supportsUnattended) : IHostedAgentLauncher {
        public string Vendor  => vendor;
        public string CliPath => vendor;
        public bool   SupportsUnattended => supportsUnattended;
        public bool   IsAvailable() => true;
        public void   Prepare(LauncherContext ctx) { }
        public LaunchArgs BuildArgs(LauncherContext ctx) => new([], null);
        public LaunchArgs BuildPassthrough(LauncherContext ctx, IReadOnlyList<string> userArgs) => new([], null);
        public void   Cleanup(AgentInstance agent) { }
    }

    [Test]
    public async Task Unattended_launch_on_unsupported_vendor_is_rejected() {
        var reason = UnattendedLaunchPolicy.RejectionReason(new FakeLauncher("gemini", supportsUnattended: false), isReviewFlow: true);

        await Assert.That(reason).IsNotNull();
        await Assert.That(reason!).Contains("gemini");
    }

    [Test]
    public async Task Unattended_launch_on_supported_vendor_is_allowed() {
        var reason = UnattendedLaunchPolicy.RejectionReason(new FakeLauncher("claude", supportsUnattended: true), isReviewFlow: true);

        await Assert.That(reason).IsNull();
    }

    [Test]
    public async Task Non_unattended_launch_is_always_allowed_even_if_vendor_lacks_support() {
        var reason = UnattendedLaunchPolicy.RejectionReason(new FakeLauncher("gemini", supportsUnattended: false), isReviewFlow: false);

        await Assert.That(reason).IsNull();
    }
}
```

(Note: `FakeLauncher` implements the full `IHostedAgentLauncher` including the `SupportsUnattended` member added in Task 1. If any interface member signature differs from the stub above at compile time, match the interface — the stub bodies are throwaway.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/UnattendedLaunchPolicyTests/*"`
Expected: build error — `UnattendedLaunchPolicy` does not exist.

- [ ] **Step 3: Implement the policy helper**

Create `src/Capacitor.Cli.Daemon/Services/UnattendedLaunchPolicy.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// AI-1124: gate for unattended (LaunchKind.ReviewFlow) launches. A vendor whose
/// launcher can't run without a human in the loop must be refused BEFORE a worktree
/// is created — otherwise the agent spawns and hangs forever on a permission prompt
/// no one will answer. Pure so it's unit-testable without the full orchestrator.
/// </summary>
internal static class UnattendedLaunchPolicy {
    /// <summary>User-facing rejection message when an unattended launch can't proceed;
    /// <c>null</c> when the launch may continue.</summary>
    public static string? RejectionReason(IHostedAgentLauncher launcher, bool isReviewFlow) =>
        isReviewFlow && !launcher.SupportsUnattended
            ? $"Vendor '{launcher.Vendor}' cannot host an unattended review-flow agent (its launcher has no unattended mode)."
            : null;
}
```

- [ ] **Step 4: Wire into the orchestrator**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`, in `HandleLaunchAgent`, immediately after the launcher-selection block (the `if (!_launchers.TryGetValue(cmd.Vendor, out var launcher)) { … return; }` at ~line 242-246) and **before** the worktree is created (~line 317), add:

```csharp
        // AI-1124: fail an unattended (review-flow) launch fast when the selected vendor's
        // launcher can't run unattended — before creating a worktree, so there's nothing to
        // clean up. Both shipped launchers support it; this guards future vendors.
        if (UnattendedLaunchPolicy.RejectionReason(launcher, isReviewFlow) is { } unattendedRejection) {
            await _server.LaunchFailedAsync(cmd.AgentId, unattendedRejection);
            return;
        }
```

(`isReviewFlow` is already in scope from `var isReviewFlow = cmd.Kind == LaunchKind.ReviewFlow;` at ~line 234. Verify the check sits after that line and after launcher selection; adjust placement if line numbers drifted.)

- [ ] **Step 5: Run the tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/UnattendedLaunchPolicyTests/*"` → 3 PASS

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/UnattendedLaunchPolicy.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/UnattendedLaunchPolicyTests.cs
git commit -m "feat(daemon): reject unattended launch for vendors without unattended support"
```

---

## Final verification (after both tasks)

- [ ] Full daemon unit suite green: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
- [ ] AOT publish clean (warnings only surface here, not on `build`): `dotnet publish -c Release src/Capacitor.Cli/Capacitor.Cli.csproj` → no new IL2026/IL3050 warnings from the changed files. (If publish is slow/heavy, at minimum `dotnet build -c Release` the daemon project.)
- [ ] Grep gate — Claude now has an unattended branch and both launchers advertise the capability:
  `grep -rn "SupportsUnattended" src/Capacitor.Cli.Daemon/Services/` → interface + both launchers
  `grep -rn "bypassPermissions\|strict-mcp-config" src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs` → present

## Out of scope (Phase B companion, separate — do NOT do here)

- **kcap-server model-fallback fix:** thread the flow definition's *requested* model into `WaitForResolvedModelAsync` / `FlowReviewerAgentGateway.GetResolvedModel` so a concrete-model definition whose daemon never reports a resolved model records the requested model on `FlowRoleAgentAssigned` instead of the `"default"` sentinel. Different repo (kcap-server); ships as a small separate PR after this one.
- **Submodule bump + npm release** of the CLI so the server picks up the daemon change — follow-up once this merges.
