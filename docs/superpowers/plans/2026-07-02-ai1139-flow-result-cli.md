# AI-1139 PR 2 (kcap-cli): `kcap mcp flow-result` + Launcher Injection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** CLI half of AI-1139 — a new `kcap mcp flow-result` stdio MCP server exposing one `submit_review_result` tool, injected into review-flow reviewer launches by both the Codex launcher (clear-then-whitelist `-c` overrides) and the Claude launcher (strict single-server config), so hosted reviewers deliver results via `POST /api/flows/reviewer/result` (already live server-side, kcap-server#920) instead of relying solely on the transcript marker.

**Architecture:** New `McpFlowResultServer` modeled on `McpFlowsServer` (stdio JSON-RPC loop, deferred authenticated client, AOT-safe JsonNode handling) with a testable internal core (`SubmitCoreAsync`) implementing validation, the retry policy for the launch race, and differentiated error texts. Both launchers' `ReviewFlow` branches inject exactly this one server; the daemon-local values (`DaemonConfig.CapacitorPath`, `DaemonConfig.ServerUrl`, `LauncherContext.AgentId`) supply the command and env — no SignalR/protocol changes.

**Tech Stack:** .NET 10, NativeAOT-compiled CLI (no reflection serialization — source-gen `McpJsonContext` + `JsonNode` casts only), TUnit on Microsoft Testing Platform (`dotnet run --project`, NEVER `dotnet test`), WireMock.Net for HTTP tests, `InternalsVisibleTo` already grants Tests.Unit access to internals of both Capacitor.Cli and Capacitor.Cli.Daemon.

## Global Constraints

- Worktree: `/Users/alexey/dev/eventstore/kcap-cli/.worktrees/ai-1139-flow-result`, branch `alexeyzimarev/ai-1139-flow-result-mcp` (already created off origin/main). Edit ONLY under this path — never the main checkout `/Users/alexey/dev/eventstore/kcap-cli`.
- Exact names from the reviewed spec: env var `KCAP_FLOW_AGENT_ID`; MCP server name `kcap-flow-result`; tool `submit_review_result` with params `round_token` (string, required), `kind` (string, required, `"findings"`|`"clean"`), `findings` (string, required when kind=findings); wire body keys `agent_id`, `round_token`, `kind`, `text`.
- Success tool text: `Result recorded for round {n}. You may end your reply now.`
- Retry policy: retryable server error codes are exactly `no_active_flow` (404) and `no_open_round` (409) — up to 5 attempts total, 3 s apart. `stale_round_token` (409) and everything else surface immediately.
- Error-text differentiation: `stale_round_token` → the server's message only, NO marker-fallback hint. All other errors → the server's message plus: `If you cannot submit via this tool, fall back to the marker: end your final message with a line starting FINDINGS: or NO FINDINGS.`
- Codex injection MUST be clear-then-whitelist: `-c mcp_servers={}` FIRST, then the dotted `-c mcp_servers.kcap-flow-result.*` overrides (dotted overrides alone MERGE into the user's config.toml table and would re-expose user-registered MCP servers — the recursion guard would silently vanish).
- Defensive default in BOTH launchers: when `config.ServerUrl` or `config.CapacitorPath` is null/whitespace, inject NOTHING (Codex: bare `mcp_servers={}` only; Claude: the existing empty `{"mcpServers":{}}`) — zero servers is the recursion-safe fallback.
- AOT safety: build JSON via `JsonObject`/`JsonArray` with `(JsonNode?)` string casts (never `JsonValue.Create`/collection expressions — IL3050); serialize DTOs only via `McpJsonContext`.
- Tests: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` (filter: `-- --treenode-filter "/*/*/ClassName/*"`).
- Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- PR title: `[AI-1139] kcap mcp flow-result + review-flow launcher injection`; body says "Part of AI-1139 — pairs with kurrent-io/kcap-server#920" and ends with the Claude Code attribution footer.

---

### Task 1: `McpFlowResultServer` + DTO + `kcap mcp flow-result` routing

**Files:**
- Create: `src/Capacitor.Cli/Commands/McpFlowResultServer.cs`
- Modify: `src/Capacitor.Cli/Commands/McpReviewServer.cs` (add `[JsonSerializable(typeof(SubmitReviewerResultDto))]` to the `McpJsonContext` block, ~line 408)
- Modify: `src/Capacitor.Cli/Program.cs:293-336` (usage lines + `case "flow-result"`)
- Test: `test/Capacitor.Cli.Tests.Unit/McpFlowResultServerTests.cs` (new)

**Interfaces:**
- Produces: `McpFlowResultServer.RunAsync(string baseUrl) → Task<int>` (exit 2 + stderr when `KCAP_FLOW_AGENT_ID` missing); `internal static Task<(string Text, bool IsError)> SubmitCoreAsync(HttpClient client, string apiRoot, string agentId, JsonObject? arguments, Func<TimeSpan, Task> delay)` — the testable core; `internal const string AgentIdEnvVar = "KCAP_FLOW_AGENT_ID"`; `record SubmitReviewerResultDto(...)`. Tasks 2-3 rely only on the command name `["mcp", "flow-result"]` and the env-var name.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Tests.Unit/McpFlowResultServerTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class McpFlowResultServerTests {
    static JsonObject Args(string? roundToken = "round-1", string? kind = "findings", string? findings = "1. issue") {
        var o = new JsonObject();
        if (roundToken is not null) o["round_token"] = (JsonNode?)roundToken;
        if (kind is not null) o["kind"] = (JsonNode?)kind;
        if (findings is not null) o["findings"] = (JsonNode?)findings;
        return o;
    }

    static Func<TimeSpan, Task> NoDelay(List<TimeSpan> recorded) => ts => { recorded.Add(ts); return Task.CompletedTask; };

    [Test]
    public async Task Happy_path_posts_snake_case_body_and_reports_round() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":2}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo("Result recorded for round 2. You may end your reply now.");

        var body = server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("\"agent_id\"");
        await Assert.That(body).Contains("\"round_token\"");
        await Assert.That(body).Contains("\"kind\"");
        await Assert.That(body).Contains("\"text\"");
        await Assert.That(body).Contains("agent-1");
    }

    [Test]
    public async Task Clean_kind_omits_null_text_from_the_wire() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var (_, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(kind: "clean", findings: null), NoDelay([]));

        await Assert.That(isError).IsFalse();
        // McpJsonContext ignores null when writing, so a clean submit must not carry a text key.
        await Assert.That(server.LogEntries.Single().RequestMessage.Body!).DoesNotContain("\"text\"");
    }

    [Test]
    public async Task Retryable_no_active_flow_retries_then_succeeds() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .InScenario("launch-race")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"no_active_flow","message":"not registered yet"}"""));
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .InScenario("launch-race")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("round 1");
        await Assert.That(delays).HasCount().EqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Retryable_error_exhausts_after_five_attempts_with_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"no_open_round","message":"no round awaiting a result"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("no round awaiting a result");
        await Assert.That(text).Contains("fall back to the marker");
        await Assert.That(delays).HasCount().EqualTo(4); // 5 attempts = 4 delays
        await Assert.That(server.LogEntries.Count()).IsEqualTo(5);
    }

    [Test]
    public async Task Stale_round_token_fails_immediately_without_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"stale_round_token","message":"That round is already closed and a newer round is open. Discard this result entirely — do NOT emit a FINDINGS:/NO FINDINGS marker for it — and respond only to the newest prompt you received."}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(roundToken: "round-0"), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Discard this result entirely");
        await Assert.That(text).DoesNotContain("fall back to the marker");
        await Assert.That(delays).HasCount().EqualTo(0);
        await Assert.That(server.LogEntries.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Validation_failures_do_not_hit_the_network() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var missingToken = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(roundToken: null), NoDelay([]));
        var badKind      = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(kind: "bogus"), NoDelay([]));
        var noFindings   = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(findings: null), NoDelay([]));

        await Assert.That(missingToken.IsError).IsTrue();
        await Assert.That(missingToken.Text).Contains("round_token");
        await Assert.That(badKind.IsError).IsTrue();
        await Assert.That(noFindings.IsError).IsTrue();
        await Assert.That(server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_exits_2_when_agent_env_is_missing() {
        var prior = Environment.GetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar);
        Environment.SetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar, null);
        try {
            var exit = await McpFlowResultServer.RunAsync("https://example.test");
            await Assert.That(exit).IsEqualTo(2);
        } finally {
            Environment.SetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar, prior);
        }
    }
}
```

- [ ] **Step 2: Run to verify compile failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpFlowResultServerTests/*"`
Expected: build FAILURE — `McpFlowResultServer` does not exist.

- [ ] **Step 3: Implement the server**

Create `src/Capacitor.Cli/Commands/McpFlowResultServer.cs`. Model the stdio loop, `BuildInitializeResponse`, `BuildToolsListResponse`, `BuildToolResult`, `BuildErrorResponse`, and `SendWithRefreshRetryAsync` on `McpFlowsServer.cs` (each MCP server carries file-local copies of these statics by convention — copy them from `McpFlowsServer`, keeping the same shapes; the envelope records `McpTool`/`McpInputSchema`/`McpSchemaProperty` etc. are shared, defined in `McpReviewServer.cs`). Core content:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// AI-1139: reviewer-side MCP server injected into hosted review-flow launches. Exposes a
/// single submit_review_result tool that POSTs to /api/flows/reviewer/result. Deliberately
/// a SEPARATE command from `kcap mcp flows` — a hard security boundary so no flag regression
/// can ever expose start_review_flow to an unattended reviewer.
/// </summary>
static class McpFlowResultServer {
    internal const string AgentIdEnvVar = "KCAP_FLOW_AGENT_ID";

    const int MaxAttempts = 5;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    const string FallbackHint =
        "If you cannot submit via this tool, fall back to the marker: end your final message with a line starting FINDINGS: or NO FINDINGS.";

    public static async Task<int> RunAsync(string baseUrl) {
        var agentId = Environment.GetEnvironmentVariable(AgentIdEnvVar);

        if (string.IsNullOrWhiteSpace(agentId)) {
            await Console.Error.WriteLineAsync(
                $"kcap mcp flow-result: {AgentIdEnvVar} is not set. This server is launched by the kcap daemon for hosted review-flow reviewers; it is not meant to be run manually.");
            return 2;
        }

        // ... stdio JSON-RPC loop copied from McpFlowsServer.RunAsync:
        //   - urlOk pre-check, deferred authenticated client (created on first tools/call)
        //   - initialize / tools/list / tools/call dispatch, guarded DispatchToolCallAsync
        //   - tools/call routes tool name "submit_review_result" to:
        //       SubmitCoreAsync(client, baseUrl.TrimEnd('/'), agentId, arguments, Task.Delay)
        //     wrapped by SendWithRefreshRetryAsync semantics inside SubmitCoreAsync's POST helper,
        //     and converts the (Text, IsError) tuple via BuildToolResult(id, text, isError).
    }

    /// <summary>Validation + POST + retry policy. Injectable delay so tests run instantly.
    /// Returns the tool text and error flag; never throws for expected failures.</summary>
    internal static async Task<(string Text, bool IsError)> SubmitCoreAsync(
            HttpClient           client,
            string               apiRoot,
            string               agentId,
            JsonObject?          arguments,
            Func<TimeSpan, Task> delay
        ) {
        var roundToken = arguments?["round_token"]?.GetValue<string>();
        var kind       = arguments?["kind"]?.GetValue<string>();
        var findings   = arguments?["findings"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(roundToken))
            return ("Error: round_token is required — copy it from the \"round token\" in your prompt.", true);
        if (kind is not ("findings" or "clean"))
            return ("Error: kind must be \"findings\" or \"clean\".", true);
        if (kind == "findings" && string.IsNullOrWhiteSpace(findings))
            return ("Error: findings text is required when kind is \"findings\".", true);

        var body = new SubmitReviewerResultDto(agentId, roundToken, kind, kind == "findings" ? findings : null);
        var url  = $"{apiRoot.TrimEnd('/')}/api/flows/reviewer/result";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            using var response = await client.PostAsync(url, JsonContent.Create(body, McpJsonContext.Default.SubmitReviewerResultDto));
            var responseBody   = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) {
                var node        = TryParse(responseBody);
                var roundNumber = node?["round_number"]?.GetValue<int>();
                return (roundNumber is { } n
                    ? $"Result recorded for round {n}. You may end your reply now."
                    : "Result recorded. You may end your reply now.", false);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ("Not logged in. Run 'kcap login' on the host shell.", true);

            var errorNode = TryParse(responseBody);
            var code      = errorNode?["error"]?.GetValue<string>();
            var message   = errorNode?["message"]?.GetValue<string>() ?? responseBody;

            if (code is "no_active_flow" or "no_open_round") {
                // Launch race: the server's flow-assignment/round events may not be projected
                // yet when a fast reviewer submits. Retry inside the tool call.
                if (attempt < MaxAttempts) {
                    await delay(RetryDelay);
                    continue;
                }
                return ($"Error: {message}\n{FallbackHint}", true);
            }

            if (code == "stale_round_token")
                // Deliberately NO fallback hint: routing a stale result through the marker
                // would bypass the round-token guard (spec-review round 2 finding).
                return ($"Error: {message}", true);

            return ($"Error: HTTP {(int)response.StatusCode} — {message}\n{FallbackHint}", true);
        }

        return ("Error: unreachable", true); // loop always returns

        static JsonObject? TryParse(string s) {
            try { return JsonNode.Parse(s)?.AsObject(); } catch { return null; }
        }
    }
}

/// <summary>CLI-side DTO for POST /api/flows/reviewer/result — mirrors the server's SubmitReviewerResultRequest.</summary>
record SubmitReviewerResultDto(
    [property: JsonPropertyName("agent_id")]    string  AgentId,
    [property: JsonPropertyName("round_token")] string  RoundToken,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("text")]        string? Text
);
```

The tool schema for `tools/list` (using the shared envelope records):

```csharp
    static McpTool[] BuildToolsList() => [
        new(
            Name: "submit_review_result",
            Description: "Submit your review result for the current round. Call once. kind=\"findings\" with your findings text, or kind=\"clean\" when there are no actionable findings. round_token comes from the \"round token\" line in your prompt.",
            InputSchema: new McpInputSchema(
                Type: "object",
                Properties: new Dictionary<string, McpSchemaProperty> {
                    ["round_token"] = new("string", "The round token from your prompt (correlates this result to the round)."),
                    ["kind"]        = new("string", "\"findings\" or \"clean\"."),
                    ["findings"]    = new("string", "Your findings text; required when kind is \"findings\".")
                },
                Required: ["round_token", "kind"]
            )
        )
    ];
```

Wire the SubmitCoreAsync POST through the same 401-refresh-retry idiom as `McpFlowsServer.SendWithRefreshRetryAsync` (wrap the `client.PostAsync` call). Register the DTO in `McpJsonContext` (McpReviewServer.cs): add `[JsonSerializable(typeof(SubmitReviewerResultDto))]` after the `SubmitReviewRoundDto` line.

In `Program.cs`: add `Console.Error.WriteLine("  kcap mcp flow-result   (launched by the daemon for hosted reviewers)");` to the two usage blocks that list mcp subcommands, and the case:

```csharp
            case "flow-result":
                return await McpFlowResultServer.RunAsync(baseUrl!);
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpFlowResultServerTests/*"`
Expected: all 7 PASS. Then build the CLI to prove AOT-facing code compiles: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` — 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/McpFlowResultServer.cs src/Capacitor.Cli/Commands/McpReviewServer.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/McpFlowResultServerTests.cs
git commit -m "feat(mcp): kcap mcp flow-result — reviewer-side submit_review_result server (AI-1139)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: CodexLauncher clear-then-whitelist injection

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs` (the `ctx.IsReviewFlow` branch in `BuildArgs`, ~lines 85-93)
- Test: `test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs` (extend)

**Interfaces:**
- Consumes: command `["mcp", "flow-result"]` + env var `KCAP_FLOW_AGENT_ID` from Task 1; `config.CapacitorPath` / `config.ServerUrl` (DaemonConfig, already injected); `ctx.AgentId`; the existing `TomlString` helper in CodexLauncher.

- [ ] **Step 1: Write the failing tests**

Add to `test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs` (reuse the file's existing launcher/ctx construction helpers; if it builds `DaemonConfig` inline, set `CodexPath` plus `CapacitorPath = "/opt/kcap"` and `ServerUrl = "https://t.example"` for these tests):

```csharp
    [Test]
    public async Task Review_flow_clears_then_whitelists_only_the_flow_result_server() {
        var args = /* BuildArgs for an IsReviewFlow ctx with AgentId "agent-xyz", config CapacitorPath="/opt/kcap", ServerUrl="https://t.example" */;

        var clearIdx = Array.IndexOf(args, "mcp_servers={}");
        await Assert.That(clearIdx).IsGreaterThanOrEqualTo(0);

        var dotted = args.Where(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal)).ToArray();
        await Assert.That(dotted.Length).IsEqualTo(3);
        await Assert.That(dotted.All(a => a.StartsWith("mcp_servers.kcap-flow-result.", StringComparison.Ordinal))).IsTrue();

        // Clear-then-whitelist ORDER: the bare table reset must come BEFORE every dotted override,
        // otherwise the dotted overrides merge into the user's config.toml table.
        foreach (var d in dotted) {
            await Assert.That(Array.IndexOf(args, d)).IsGreaterThan(clearIdx);
        }

        var command = dotted.Single(a => a.Contains(".command="));
        await Assert.That(command).Contains("/opt/kcap");
        var argsOverride = dotted.Single(a => a.Contains(".args="));
        await Assert.That(argsOverride).Contains("mcp");
        await Assert.That(argsOverride).Contains("flow-result");
        var env = dotted.Single(a => a.Contains(".env="));
        await Assert.That(env).Contains("KCAP_URL");
        await Assert.That(env).Contains("https://t.example");
        await Assert.That(env).Contains("KCAP_FLOW_AGENT_ID");
        await Assert.That(env).Contains("agent-xyz");
    }

    [Test]
    public async Task Review_flow_without_server_url_injects_no_servers() {
        var args = /* BuildArgs for an IsReviewFlow ctx, config with ServerUrl = "" */;

        await Assert.That(args).Contains("mcp_servers={}");
        await Assert.That(args.Any(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal))).IsFalse();
    }
```

(The two `/* ... */` arrangements must reuse the file's existing helper for constructing the launcher and a ReviewFlow `LauncherContext` — copy from the existing `mcp_servers={}` test in the same file and adjust the `DaemonConfig`/`AgentId` values.)

- [ ] **Step 2: Run to verify the new tests fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexLauncherTests/*"`
Expected: the two new tests FAIL (no dotted overrides emitted); existing tests pass.

- [ ] **Step 3: Implement**

In `CodexLauncher.BuildArgs`, replace the `ctx.IsReviewFlow` block:

```csharp
        // Review-flow reviewers get exactly ONE MCP server: kcap-flow-result (AI-1139), which can
        // only submit a result — never start a flow. Clear-then-whitelist, in this order:
        // the bare `mcp_servers={}` FIRST replaces the entire [mcp_servers] table from
        // ~/.codex/config.toml; the dotted overrides then insert into the now-empty table.
        // Dotted overrides alone MERGE into the user's table and would re-expose whatever MCP
        // servers the user has registered (including a hand-registered kcap-flows with
        // start_review_flow — the recursion guard would silently vanish).
        if (ctx.IsReviewFlow) {
            args.Add("-c");
            args.Add("mcp_servers={}");
            AddFlowResultServer(args, ctx);
        }
```

and add to the class:

```csharp
    /// <summary>AI-1139: registers the reviewer-side result-submission server. Skipped (zero
    /// servers — the recursion-safe default) when the daemon has no server URL or kcap path;
    /// the reviewer then falls back to the transcript marker per the prompt contract.</summary>
    void AddFlowResultServer(List<string> args, LauncherContext ctx) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return;

        const string name = "kcap-flow-result";

        args.Add("-c");
        args.Add($"mcp_servers.{name}.command={TomlString(config.CapacitorPath)}");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.args=[{TomlString("mcp")},{TomlString("flow-result")}]");
        args.Add("-c");
        args.Add($"mcp_servers.{name}.env={{KCAP_URL={TomlString(config.ServerUrl)},KCAP_FLOW_AGENT_ID={TomlString(ctx.AgentId)}}}");
    }
```

(Exact TOML mechanics mirror `BuildReviewArgs` in the same file — same `TomlString` quoting, same `env={KEY=...}` inline-table shape.)

- [ ] **Step 4: Run the Codex launcher tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CodexLauncherTests/*"`
Expected: ALL pass, including the pre-existing `mcp_servers={}` test (the clear is still emitted).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/CodexLauncher.cs test/Capacitor.Cli.Tests.Unit/Codex/CodexLauncherTests.cs
git commit -m "feat(daemon): Codex review-flow launches inject kcap-flow-result (clear-then-whitelist) (AI-1139)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: ClaudeLauncher strict single-server config

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs` (the `ctx.IsReviewFlow` branch in `BuildArgs`, ~lines 91-105, and a new helper)
- Test: `test/Capacitor.Cli.Tests.Unit/ClaudeLauncherReviewFlowTests.cs` (update + extend)

**Interfaces:**
- Consumes: same names as Task 2. The `--strict-mcp-config` + `--disallowedTools Agent` flags stay exactly as they are.

- [ ] **Step 1: Update/add the failing tests**

In `ClaudeLauncherReviewFlowTests.cs`: change `NewLauncher()` to accept optional config values:

```csharp
    static ClaudeLauncher NewLauncher(string? serverUrl = null, string capacitorPath = "kcap") =>
        new(new DaemonConfig { ClaudePath = "claude", ServerUrl = serverUrl ?? "", CapacitorPath = capacitorPath }, NullLogger<ClaudeLauncher>.Instance);
```

Replace `Review_flow_launch_loads_no_mcp_servers` with:

```csharp
    [Test]
    public async Task Review_flow_launch_without_server_url_loads_no_mcp_servers() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var parsed   = JsonNode.Parse(args[cfgIndex + 1])!.AsObject();
        await Assert.That(parsed["mcpServers"]!.AsObject().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Review_flow_launch_loads_exactly_the_flow_result_server() {
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var servers  = JsonNode.Parse(args[cfgIndex + 1])!.AsObject()["mcpServers"]!.AsObject();

        await Assert.That(servers.Count).IsEqualTo(1);
        var flowResult = servers["kcap-flow-result"]!.AsObject();
        await Assert.That(flowResult["command"]!.GetValue<string>()).IsEqualTo("/opt/kcap");
        await Assert.That(flowResult["args"]![0]!.GetValue<string>()).IsEqualTo("mcp");
        await Assert.That(flowResult["args"]![1]!.GetValue<string>()).IsEqualTo("flow-result");
        await Assert.That(flowResult["env"]!["KCAP_URL"]!.GetValue<string>()).IsEqualTo("https://t.example");
        await Assert.That(flowResult["env"]!["KCAP_FLOW_AGENT_ID"]!.GetValue<string>()).IsEqualTo("a-1");
    }
```

(`"a-1"` is the `AgentId` the file's `NewCtx` helper already uses.) The other existing tests (`bypasses_permissions`, `disallows_the_agent_subagent_tool`, `still_passes_model_and_prompt`, `Non_review_flow...`) stay untouched and must keep passing.

- [ ] **Step 2: Run to verify the new test fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeLauncherReviewFlowTests/*"`
Expected: `Review_flow_launch_loads_exactly_the_flow_result_server` FAILS (config still the empty map); the without-server-url test passes already.

- [ ] **Step 3: Implement**

In `ClaudeLauncher.BuildArgs`, change the ReviewFlow branch line `args.Add(EmptyMcpConfig);` to `args.Add(BuildReviewFlowMcpConfig(ctx));`, update the branch's comment to say the strict config now whitelists exactly the flow-result submission server (AI-1139; the empty map remains the fallback), and add:

```csharp
    /// <summary>AI-1139: strict whitelist for review-flow reviewers — exactly the
    /// kcap-flow-result submission server, or the empty map when the daemon has no server
    /// URL / kcap path (zero servers is the recursion-safe default). Built via JsonNode
    /// string casts — JsonValue.Create / collection expressions lower to generic Add&lt;T&gt;
    /// and trip NativeAOT (IL3050).</summary>
    string BuildReviewFlowMcpConfig(LauncherContext ctx) {
        if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.CapacitorPath)) return EmptyMcpConfig;

        var argsNode = new JsonArray();
        argsNode.Add((JsonNode?)"mcp");
        argsNode.Add((JsonNode?)"flow-result");

        var server = new JsonObject {
            ["command"] = (JsonNode?)config.CapacitorPath,
            ["args"]    = argsNode,
            ["env"]     = new JsonObject {
                ["KCAP_URL"]            = (JsonNode?)config.ServerUrl,
                ["KCAP_FLOW_AGENT_ID"] = (JsonNode?)ctx.AgentId
            }
        };

        return new JsonObject { ["mcpServers"] = new JsonObject { ["kcap-flow-result"] = server } }.ToJsonString();
    }
```

(`using System.Text.Json.Nodes;` if not already present.) The `--disallowedTools Agent` guard stays — subagents still don't inherit `--mcp-config`.

- [ ] **Step 4: Run the Claude launcher tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeLauncherReviewFlowTests/*"`
Expected: ALL pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/ClaudeLauncher.cs test/Capacitor.Cli.Tests.Unit/ClaudeLauncherReviewFlowTests.cs
git commit -m "feat(daemon): Claude review-flow launches whitelist kcap-flow-result via strict MCP config (AI-1139)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Full suites + AOT publish smoke + PR prep

**Files:** none (verification only; PR created by the controller after final review)

- [ ] **Step 1: Run both test suites**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

Expected: both pass. (If the Integration suite fails for environmental reasons plainly unrelated to this diff — e.g. missing external binaries — report the exact failure rather than chasing it.)

- [ ] **Step 2: AOT publish smoke test**

The CLI ships NativeAOT; JsonNode/source-gen usage must survive trimming analysis:

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release -r osx-arm64 2>&1 | grep -E "IL[0-9]+|error|Warning" | head -20
```

Expected: no NEW IL2xxx/IL3xxx warnings attributable to `McpFlowResultServer.cs`, `SubmitReviewerResultDto`, or the launcher changes (pre-existing warnings elsewhere are out of scope — compare against the file names).

- [ ] **Step 3: Report**

No commit. Report suite results + AOT output summary; the controller runs the final whole-branch review and creates the PR.

---

## Self-review notes

- Spec (PR 2 section) coverage: stdio server + tool schema + env startup guard (Task 1), retry policy + differentiated error texts incl. the stale-token no-fallback rule (Task 1), snake_case raw-wire test (Task 1), Codex clear-then-whitelist with order-asserting test (Task 2), Claude strict single-server config (Task 3), missing-env + no-other-servers assertions (Tasks 1-3), suites (Task 4).
- Two `/* ... */` arrangement stubs in Task 2 Step 1 are deliberate harness-reuse instructions (the CodexLauncherTests file's local helpers can't be inlined here); the assertion sets are fully specified.
- The spec's "success → tool text 'Result recorded for round {n}…'" and the exact retryable/terminal code split are encoded as Global Constraints and asserted in tests.
