# Unattended reviewer auto-approve — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let a daemon-hosted, unattended review-flow reviewer auto-approve its kcap-owned MCP tool calls (no interactive prompt, no hang), without weakening permissions for interactive agents.

**Architecture:** The orchestrator mints a per-launch **CSPRNG reviewer token** (bound to the launch's read-only kcap allowlist) and gives the reviewer that token's URL as `KCAP_DAEMON_URL`. `LocalPermissionBridge` holds a live-token registry; requests on a reviewer token auto-approve tools from the bound allowlist (and deny out-of-allowlist server-qualified calls), while the shared token behaves exactly as today. Authorization is the secret token; the reviewer's Codex MCP-config lock bounds which servers it can even call.

**Tech Stack:** .NET 10, C#, TUnit, `System.Security.Cryptography.RandomNumberGenerator`, `System.Text.Json.Nodes`. Repo: kcap-cli (daemon). Full design: [../specs/2026-07-09-reviewer-auto-approve-design.md](../specs/2026-07-09-reviewer-auto-approve-design.md).

## Global Constraints

- **Daemon-originated trust:** the "unattended" signal is a CSPRNG secret only the reviewer process holds. Never a body flag / fixed path segment (all agents share the base token).
- **Scope:** `LaunchKind.ReviewFlow` only. `Default`/`Review` unchanged.
- **Server-granularity contract:** auto-approve is bounded to a **read-only** kcap server set (`ReviewFlowAutoApprovableServers` = `kcap-review`, `kcap-sessions`). `kcap-memory` (writes) / `kcap-flows` (flow-starting) are NOT auto-approvable. `submit_review_result` keeps its own unconditional carve-out (#255).
- **Fail-fast, not hang:** an invalid reviewer allowlist fails the launch (`LaunchFailedAsync`); an out-of-allowlist server-qualified call on a reviewer token is DENIED — neither falls through to an interactive prompt.
- **Fail-safe:** unknown/revoked token or malformed body → 404/400/deny. Never auto-allow on doubt.
- **Secrecy:** the reviewer token must never be logged/persisted/surfaced (transcript, run metadata, launch/debug logs). `KCAP_DAEMON_URL` is already in `PtyEnvScrub`.
- **No wire/protocol/server change.** With no reviewer tokens registered, behaviour is byte-identical to today.

## File Structure

- `src/Capacitor.Cli.Core/KcapMcpRegistry.cs` — **modify**: add `ReviewFlowAutoApprovableServers`, `ReviewFlowUnattendedSafeTools`, and `TryResolveReviewFlowAllowlist`.
- `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` — **modify**: live reviewer-token registry (`RegisterReviewerToken`/`RevokeReviewerToken`), request classification (token→context, body-first, reviewer-token approve/deny), CSPRNG helper, keep `IsFlowResultSubmission`.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` — **modify**: mint/validate/register the reviewer token for `ReviewFlow`, thread its URL into `RuntimeStartContext.DaemonBridgeUrl`, revoke on every teardown path.
- `src/Capacitor.Cli.Daemon/Services/AgentInstance.cs` (or its definition site) — **modify**: add `string? ReviewerBridgeToken`.
- Tests: `test/Capacitor.Cli.Tests.Unit/Services/LocalPermissionBridgeTests.cs` (extend), a new `KcapMcpRegistryReviewFlowTests.cs`, and the existing daemon orchestrator test file (extend). A guard test for the tool classification.

---

### Task 1: Registry — auto-approvable set, tool classification, allowlist resolver

**Files:**
- Modify: `src/Capacitor.Cli.Core/KcapMcpRegistry.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/KcapMcpRegistryReviewFlowTests.cs` (create)

**Interfaces (produced):**
```csharp
// The read-only kcap servers whose tools are safe to auto-approve for an unattended reviewer.
public static readonly IReadOnlySet<string> ReviewFlowAutoApprovableServers; // {"kcap-review","kcap-sessions"} (case-insensitive)

// Explicit, reviewed classification: server id -> its unattended-safe (read/result-submit) tool names.
// The guard test cross-checks each auto-approvable server's ACTUAL tools/list against this.
public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ReviewFlowUnattendedSafeTools;

// Resolve a flow reviewer allowlist to canonical, auto-approvable server ids.
// true  + servers (canonical, deduped) when EVERY entry resolves to a ReviewFlowAutoApprovableServers member;
// false + rejected (the offending name) when any entry is unknown, flow-starting, or not auto-approvable.
// null/empty input -> true + empty array (a reviewer with only the flow-result channel is valid).
public static bool TryResolveReviewFlowAllowlist(IReadOnlyList<string>? names, out string[] servers, out string? rejected);
```

- [ ] **Step 1: Write failing tests** in `KcapMcpRegistryReviewFlowTests.cs`:
```csharp
using Capacitor.Cli.Core;
namespace Capacitor.Cli.Tests.Unit;

public class KcapMcpRegistryReviewFlowTests {
    [Test] public async Task Resolve_accepts_read_only_servers_case_insensitively() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["Kcap-Review"], out var s, out var rej);
        await Assert.That(ok).IsTrue();
        await Assert.That(s).IsEquivalentTo(["kcap-review"]);
        await Assert.That(rej).IsNull();
    }
    [Test] public async Task Resolve_rejects_flow_starting_server() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-review","kcap-flows"], out _, out var rej);
        await Assert.That(ok).IsFalse();
        await Assert.That(rej).IsEqualTo("kcap-flows");
    }
    [Test] public async Task Resolve_rejects_write_server_kcap_memory() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-memory"], out _, out var rej);
        await Assert.That(ok).IsFalse();
        await Assert.That(rej).IsEqualTo("kcap-memory");
    }
    [Test] public async Task Resolve_rejects_unknown_server() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["not-a-server"], out _, out var rej);
        await Assert.That(ok).IsFalse();
        await Assert.That(rej).IsEqualTo("not-a-server");
    }
    [Test] public async Task Resolve_empty_is_ok_empty() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(null, out var s, out var rej);
        await Assert.That(ok).IsTrue();
        await Assert.That(s).IsEmpty();
        await Assert.That(rej).IsNull();
    }
    // Contract guard: every auto-approvable server has a classification entry, and it only names read/submit tools.
    [Test] public async Task Every_auto_approvable_server_is_classified() {
        foreach (var srv in KcapMcpRegistry.ReviewFlowAutoApprovableServers)
            await Assert.That(KcapMcpRegistry.ReviewFlowUnattendedSafeTools.ContainsKey(srv)).IsTrue();
    }
}
```
- [ ] **Step 2: Run — verify FAIL** (`TryResolveReviewFlowAllowlist` not defined).
  Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit -- --treenode-filter "/*/*/KcapMcpRegistryReviewFlowTests/*"`
- [ ] **Step 3: Implement** in `KcapMcpRegistry.cs`: add the set, the classification (tool names verified from `kcap mcp review`/`kcap mcp sessions` `tools/list`), and the resolver (using the existing `Resolve` + `StartsFlows` + membership in `ReviewFlowAutoApprovableServers`; first offending name → `rejected`).
- [ ] **Step 4: Run — verify PASS** (same filter).
- [ ] **Step 5: Commit** `feat(AI-1292): registry review-flow auto-approvable set + tool classification + resolver`.

---

### Task 2: LocalPermissionBridge — reviewer-token registry + classification

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LocalPermissionBridgeTests.cs` (extend)

**Interfaces (produced):**
```csharp
// Mint a live reviewer token bound to `allowlistServers` (canonical read-only ids); returns the
// full base URL (http://127.0.0.1:{port}/{reviewerToken}) the reviewer should use as KCAP_DAEMON_URL.
// Throws if the bridge isn't started. Token is CSPRNG (32 hex ≡ the shared token's shape).
public string RegisterReviewerToken(IReadOnlyList<string> allowlistServers);
public void   RevokeReviewerToken(string reviewerBridgeUrlOrToken);
```

**Design notes (implementation):**
- Replace the single `_token` field with a token registry: keep the shared token (interactive) + a
  `ConcurrentDictionary<string, string[]>` of reviewer token → bound allowlist servers. `HandleAsync`
  extracts the path token, then: (step 1) token must be the shared token OR a live reviewer token → else 404;
  (step 2) parse body, require non-empty `session_id` **and** `tool_name` → else 400; (step 3)
  `IsFlowResultSubmission(tool)` → allow (any live token, unchanged); (step 4) if the request is on a
  **reviewer** token: server-qualified `mcp__<server>__<tool>` whose `<server>` ∉ bound allowlist →
  **deny** (`new PermissionDecision("deny", null, null)`, log a diagnostic WITHOUT the token); otherwise
  (in-allowlist qualified, or bare Codex name) → allow; (step 5) shared token, other tool → server round-trip.
- `RegisterReviewerToken` mints a CSPRNG token via a new `NewToken()` helper (`RandomNumberGenerator.GetHexString(32)` or `Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()`); the shared token in `StartAsync` uses the same helper (replacing `Guid.NewGuid().ToString("N")`). Reject a mint that collides with a live token.
- Never log the token in any `LoggerMessage`.

- [ ] **Step 1: Write failing tests** (extend `LocalPermissionBridgeTests`, all `[Test, NotInParallel(nameof(LocalPermissionBridgeTests))]`). Helper to register + build a reviewer POST URL:
```csharp
static string ReviewerUrl(LocalPermissionBridge bridge, params string[] servers)
    => bridge.RegisterReviewerToken(servers); // returns http://127.0.0.1:{port}/{token}

[Test, NotInParallel(nameof(LocalPermissionBridgeTests))]
public async Task Reviewer_token_auto_approves_bound_read_tool_without_server_round_trip() {
    var (bridge, server) = CreateBridge((_,_,_,_,_) => Task.FromResult(new PermissionDecision("deny", null, null)));
    try {
        await bridge.StartAsync(CancellationToken.None);
        var url = ReviewerUrl(bridge, "kcap-review");
        using var client = CreateClient();
        var payload = new { session_id = "abc", tool_name = "get_pr_summary" };
        using var r = await client.PostAsync($"{url}/codex/permission-request", JsonContent.Create(payload));
        await Assert.That((int)r.StatusCode).IsEqualTo(200);
        await Assert.That(server.Calls.Count).IsEqualTo(0);           // no prompt
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        await Assert.That(doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()).IsEqualTo("allow");
    } finally { await bridge.DisposeAsync(); }
}
```
Add the rest (each asserting `server.Calls.Count` and/or `behavior`):
  - reviewer token + `submit_review_result` (bare) → allow, 0 server calls;
  - reviewer token + `mcp__kcap_review__get_pr_summary` (qualified, in allowlist) → allow, 0 calls;
  - reviewer token + `mcp__kcap_memory__save_memory` (qualified, server NOT in bound allowlist) → **deny**, 0 calls;
  - reviewer token + malformed JSON → 400; + missing/empty `tool_name` → 400; + missing/empty `session_id` → 400 (no server call);
  - **shared** token + `get_pr_summary` → prompts (1 server call) — no escalation;
  - **shared** token + `submit_review_result` → allow, 0 calls (#255 regression, existing tests still pass);
  - **revoked** reviewer token (register→revoke) + `get_pr_summary` → 404; + `submit_review_result` → 404;
  - concurrency: register token A + token B; revoke A; B + `get_pr_summary` → still allow;
  - secrecy: after `RegisterReviewerToken`, the token substring does not appear in captured `NullLogger`… (use a capturing `ILogger` — see Step 3) bridge log output.
- [ ] **Step 2: Run — verify FAIL** (`RegisterReviewerToken` not defined).
- [ ] **Step 3: Implement** the registry + classification + CSPRNG in `LocalPermissionBridge.cs` per the design notes; for the secrecy test use a small capturing logger (list of formatted messages) and assert none contains the token.
- [ ] **Step 4: Run — verify PASS** (`/*/*/LocalPermissionBridgeTests/*`), incl. all pre-existing tests.
- [ ] **Step 5: Commit** `feat(AI-1292): reviewer-token registry + auto-approve classification in LocalPermissionBridge`.

---

### Task 3: AgentOrchestrator — mint/validate/register + revoke

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (launch build 386–405; early-return cleanup 411–424; catch cleanup 469–507; normal teardown in `CleanupAgentAsync`)
- Modify: `AgentInstance` definition — add `public string? ReviewerBridgeToken { get; init; }`
- Test: the existing daemon orchestrator test file (e.g. `AgentOrchestratorRoundResyncTests.cs` sibling) — add reviewer-token cases

**Interfaces (consumed):** `KcapMcpRegistry.TryResolveReviewFlowAllowlist` (Task 1); `LocalPermissionBridge.RegisterReviewerToken` / `RevokeReviewerToken` (Task 2).

**Implementation (before building `runtimeCtx` at line 386):**
```csharp
var daemonBridgeUrl = _permissionBridge.BaseUrl;   // shared token by default
string? reviewerBridgeToken = null;
var effectiveAllowlist = cmd.McpAllowlist;

if (isReviewFlow) {
    if (!KcapMcpRegistry.TryResolveReviewFlowAllowlist(cmd.McpAllowlist, out var servers, out var rejected)) {
        await _server.LaunchFailedAsync(agentId,
            $"Review-flow reviewer allowlist contains a server that is not auto-approvable: '{rejected}'.");
        return;   // FAIL FAST — never a shared-token launch that would hang
    }
    var reviewerUrl = _permissionBridge.RegisterReviewerToken(servers);   // http://127.0.0.1:{port}/{token}
    daemonBridgeUrl     = reviewerUrl;
    reviewerBridgeToken = reviewerUrl;   // store the URL/token for revocation
    effectiveAllowlist  = servers;       // single source: same set the launcher uses for the MCP config
}
```
Then set `DaemonBridgeUrl: daemonBridgeUrl` (was `_permissionBridge.BaseUrl`) and `McpAllowlist: effectiveAllowlist` in `runtimeCtx`, and add `ReviewerBridgeToken = reviewerBridgeToken` to the `AgentInstance` initializer (line 433). Revoke in **all** teardown paths, after the process is gone:
  - the `CodexHooksNotInstalledException` early-return (411–424): `if (reviewerBridgeToken != null) _permissionBridge.RevokeReviewerToken(reviewerBridgeToken);`
  - the `catch` (469–507): same;
  - normal teardown in `CleanupAgentAsync`: `if (agent.ReviewerBridgeToken != null) _permissionBridge.RevokeReviewerToken(agent.ReviewerBridgeToken);` after the runtime has exited.

- [ ] **Step 1: Write failing tests** (extend the daemon orchestrator test suite; reuse its existing harness/fakes). Because `LocalPermissionBridge.BaseUrl` needs the bridge started, start the orchestrator's bridge in the fixture (or assert via a fake bridge seam if the suite has one — check the existing tests first). Assertions:
  - a `ReviewFlow` launch with allowlist `["kcap-review"]` → the reviewer runtime's `KCAP_DAEMON_URL` (captured via the fake runtime factory's `RuntimeStartContext.DaemonBridgeUrl`) is a reviewer-token URL **different** from `_permissionBridge.BaseUrl`, and a token is registered;
  - a `Default` launch → `DaemonBridgeUrl == _permissionBridge.BaseUrl` (shared), no token registered;
  - a `ReviewFlow` launch with allowlist `["kcap-memory"]` → `LaunchFailedAsync` called, **no** runtime spawned, **no** reviewer token registered;
  - after teardown, the reviewer token is revoked (a subsequent POST to its URL → 404 via the real bridge, or the fake records a revoke).
- [ ] **Step 2: Run — verify FAIL.**
- [ ] **Step 3: Implement** per the block above (+ `AgentInstance.ReviewerBridgeToken`).
- [ ] **Step 4: Run — verify PASS** (orchestrator filter).
- [ ] **Step 5: Commit** `feat(AI-1292): mint+register a reviewer bridge token for unattended review-flow launches`.

---

### Task 4: Secrecy + contract guard hardening

**Files:**
- Test: extend `LocalPermissionBridgeTests` / orchestrator tests / `KcapMcpRegistryReviewFlowTests`.

- [ ] **Step 1: Contract guard (tools/list cross-check).** Add a test (in `KcapMcpRegistryReviewFlowTests` or a daemon integration test) that, for each `ReviewFlowAutoApprovableServers` member, spawns `kcap mcp <review|sessions>` and does the `initialize`+`tools/list` handshake (pattern proven in the AI-1224 E2E), asserting every returned tool name ∈ `ReviewFlowUnattendedSafeTools[server]`. If the test project can't spawn the built binary, fall back to a static assertion + open a follow-up; note the decision in the test. Run + PASS.
- [ ] **Step 2: Secrecy surfaces.** Verify + assert the reviewer token/URL is absent from: orchestrator launch logs (capturing logger), `PtyEnvScrub` coverage (assert `KCAP_DAEMON_URL` scrubbed from the recorded env), and agent run/status metadata (the `AgentStatusChangedAsync` payload carries `SessionId`, not the bridge URL — assert). Run + PASS.
- [ ] **Step 3: Commit** `test(AI-1292): contract guard (tools/list) + reviewer-token secrecy assertions`.

---

### Task 5: Full suite + build

- [ ] **Step 1:** `git submodule update --init` if needed (worktree). Build: `dotnet build test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` → 0 errors.
- [ ] **Step 2:** Run the full daemon/unit suite (`dotnet run --project test/Capacitor.Cli.Tests.Unit`) → all green, no regressions in the pre-existing `LocalPermissionBridgeTests` (esp. the #255 flow-result + shared-token cases).
- [ ] **Step 3: Commit** any test-fixup; the branch is ready for PR + Codex code-review.

## Self-Review

- **Spec coverage:** per-reviewer CSPRNG token (T2), token+body classification order (T2), reviewer-token deny-not-defer (T2), submit_review_result carve-out preserved (T2), fail-fast invalid allowlist (T3), revoke on all teardown paths (T3), server-level contract + machine-checkable guard (T1+T4), secrecy surfaces (T4), lifecycle/concurrency (T2 concurrency + T3 revoke). ✓
- **Deviation noted:** the spec's "kcap-owned + non-flow-starting" launch validation is realized concretely as `ReviewFlowAutoApprovableServers` (read-only: `kcap-review`, `kcap-sessions`), excluding the write server `kcap-memory` — a tightening of constraint 3 ("unattended-safe servers only"), so a flow allowlisting a write/flow-starting server fails fast rather than auto-approving or hanging.
- **Types:** `TryResolveReviewFlowAllowlist(out string[] servers, out string? rejected)`, `RegisterReviewerToken(IReadOnlyList<string>) → string url`, `RevokeReviewerToken(string)`, `AgentInstance.ReviewerBridgeToken` — used consistently across T1–T3.
