# Catalogue Flow Guidance — CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agents learn which catalogue flows to offer, and how to drive them, from the server. The CLI's review-specific offering logic goes away.

**Architecture:**
- `list_flow_definitions` renders the server's new `offer` and `when_to_use` fields.
- A new read-only `get_flow_definition` tool returns a flow's driver guide.
- A third session-start lane, beside memory and guidelines, injects the proactive flows into every harness's session context, Claude included.
- `agent-flows` becomes the one skill for driving any flow. `suggest-review-flow` and the review-offer paragraph in the agent instructions are removed.

**Tech Stack:** .NET 10 NativeAOT, `System.Text.Json.Nodes` (no DTOs, so nothing for AOT to trim), TUnit, WireMock.Net.

**Spec:** `docs/superpowers/specs/2026-10-03-catalogue-flow-guidance-design.md`. The server plan (`docs/superpowers/plans/2026-10-03-catalogue-flow-guidance-server.md`) ships first; this plan works against both old and new servers.

## Global Constraints

- Server JSON fields: `offer` (`proactive` | `on_request`), `when_to_use` (≤ 300 characters), `driver_guide` (markdown).
- By-id route: `GET /api/flows/definitions/{id}`. A new server answers an unknown id with a 404 problem body carrying `detail`; an old server answers a routing 404 with no such body.
- Lane caps: at most 10 flows rendered with their `when_to_use`, and a fragment of at most 2048 characters. Any further flows are listed by id only.
- The lane fails open: an error, timeout or old server injects nothing and never fails the hook.
- The lane is skipped when `kcap-flows` is not registered for the harness (`McpServerNudgeAvailability.IsRegisteredFor`).
- Server text is untrusted. Render it through `NextWorkUntrustedText.Render` before it enters session context.
- `review-flows`, the `*_review_*` aliases, `ServerCannotListDefinitions` and the closing hint in `FormatFlowDefinitions` stay; removing them is a later sub-project.
- README and `help-*.txt` change in the same PR as the CLI surface (CLAUDE.md rule).
- Comments are scarce: no ticket ids, no change narration. Commit subject: one imperative clause, ≤ 80 characters, no issue reference unless the user supplies one.
- After the change, `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release` shows no IL2026/IL3050.

## Review Focus

1. **Old server, every surface.** `/api/flows/definitions` without `offer` fields, and `/definitions/{id}` returning a routing 404, must produce no lane block and a clear "does not publish guides" notice, never an error. Pinned in Tasks 1, 2 and 3.
2. **A hostile or sloppy `when_to_use`** (newlines, `<next-work-data>`-like tags, 5 KB of text) must not break out of the injected block or blow the 2 KB cap. Pinned in Task 3.
3. **Every other lane disabled but `kcap-flows` registered.** The flows block must still be injected. Three short-circuits currently skip everything when memory and guidelines are both off. Pinned in Tasks 3 and 4.
4. **`kcap-flows` registered, memory lane on, flows endpoint 5xx.** The memory fragment must still be emitted byte-for-byte as before. Pinned in Task 3 (composite) and Task 4 (Claude).
5. **An upgraded user keeps `~/.agents/skills/kcap-suggest-review-flow`** unless the installer prunes it. A stale copy would keep offering review twice. Pinned in Task 5.

---

### Task 1: Render offer and when_to_use in `list_flow_definitions`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/McpFlowsServer.cs:1552-1591` (`FormatFlowDefinitions`) and the `list_flow_definitions` description (~2573)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/McpFlowsServerFlowDefinitionsTests.cs`

**Interfaces:**
- Produces: an unchanged signature, `McpFlowsServer.FormatFlowDefinitions(string body) : string?`.
  - It renders `  when to use (offer proactively): <text>` for proactive entries.
  - It renders `  when to use (on request): <text>` when an on-request entry has text.
  - It renders nothing extra when the fields are absent.

- [ ] **Step 1: Write the failing tests.** Append to `McpFlowsServerFlowDefinitionsTests`.

```csharp
    [Test]
    public async Task A_proactive_definition_says_when_to_offer_it() {
        var text = McpFlowsServer.FormatFlowDefinitions("""
            {"definitions":[{"id":"code-review","version":4,"is_single_participant":true,
              "participants":[{"role":"reviewer","model":"default"}],
              "offer":"proactive","when_to_use":"After a change\nis complete."}]}
            """)!;

        await Assert.That(text).Contains("  when to use (offer proactively): After a change is complete.");
    }

    [Test]
    public async Task An_on_request_definition_shows_its_when_to_use_without_inviting_an_offer() {
        var text = McpFlowsServer.FormatFlowDefinitions("""
            {"definitions":[{"id":"triage","participants":[{"role":"reviewer","model":"default"}],
              "offer":"on_request","when_to_use":"When asked to triage."}]}
            """)!;

        await Assert.That(text).Contains("  when to use (on request): When asked to triage.");
        await Assert.That(text).DoesNotContain("offer proactively");
    }

    [Test]
    public async Task A_server_without_guidance_fields_renders_as_before() {
        var text = McpFlowsServer.FormatFlowDefinitions(TwoDefinitions)!;

        await Assert.That(text).DoesNotContain("when to use");
    }

    [Test]
    public async Task The_listing_points_to_get_flow_definition() {
        var tool = McpFlowsServer.BuildToolsList().Single(t => t.Name == "list_flow_definitions");

        await Assert.That(tool.Description).Contains("get_flow_definition");
    }
```

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpFlowsServerFlowDefinitionsTests/*"`
Expected: the four new tests FAIL; the existing ones pass.

- [ ] **Step 3: Implement.** In `FormatFlowDefinitions`, directly after the `description` line, insert:

```csharp
            if (Str(definition, "when_to_use") is { Length: > 0 } whenToUse)
                sb.AppendLine(Str(definition, "offer") == "proactive"
                    ? $"  when to use (offer proactively): {whenToUse.ReplaceLineEndings(" ").Trim()}"
                    : $"  when to use (on request): {whenToUse.ReplaceLineEndings(" ").Trim()}");
```

In the `list_flow_definitions` tool description, after the sentence ending "…authored vendor (unset means the request or your saved preference decides) and model. ", insert:

```
"A definition may also say when to use it, and whether you may offer it unprompted (offer proactively) or only run it when asked (on request). Before starting one, call get_flow_definition for its guide. " +
```

- [ ] **Step 4: Run the tests and confirm they pass.** Same command as Step 2. Expected: all pass.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli/Commands/McpFlowsServer.cs test/Capacitor.Cli.Tests.Unit/Commands/McpFlowsServerFlowDefinitionsTests.cs
git commit -m "Show when to use each flow in list_flow_definitions"
```

---

### Task 2: `get_flow_definition` tool

**Files:**
- Modify: `src/Capacitor.Cli/Commands/McpFlowsServer.cs` (new dispatch branch after the `list_flow_definitions` branch at ~449-471; `FormatFlowDefinitionDetail` and `ServerPublishesNoGuides` beside `FormatFlowDefinitions`; a tool entry after `list_flow_definitions` in `BuildToolsList`)
- Create: `test/Capacitor.Cli.Tests.Unit/Commands/McpFlowsServerFlowDefinitionDetailTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/McpToolAnnotationsTests.cs:47-53`
- Modify: `test/Capacitor.Cli.Tests.Integration/McpFlowsServerTests.cs:308` (tool count 12 → 13, plus a name check)

**Interfaces:**
- Consumes: `GetBoundedAsync(HttpClient, string url, FlowRetryClock)`, `AuthRejectionNotice.ForPersistentUnauthorizedAsync`, `FormatFlowStartError(int, string, bool)` and `McpToolAnnotations.Read`, all existing.
- Produces:
  - MCP tool `get_flow_definition` with one required string argument, `definition_id`.
  - `internal static string? FormatFlowDefinitionDetail(string body)`
  - `internal const string ServerPublishesNoGuides`

- [ ] **Step 1: Write the failing tests.** Create `McpFlowsServerFlowDefinitionDetailTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerFlowDefinitionDetailTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    static JsonObject ToolCall(JsonObject arguments) => new() {
        ["params"] = new JsonObject { ["name"] = "get_flow_definition", ["arguments"] = arguments }
    };

    static (string Text, bool IsError) Result(string response) {
        var result = JsonNode.Parse(response)!["result"]!;

        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    static WireMockServer Serving(string id, int status, string body, string contentType = "application/json") {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/definitions/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));

        return server;
    }

    async Task<(string Text, bool IsError)> CallAsync(WireMockServer server, string id) {
        using var client = new HttpClient();

        return Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(new JsonObject { ["definition_id"] = id }), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null));
    }

    const string CodeReview = """
        {"id":"code-review","version":4,"description":"Review code changes.","is_single_participant":true,
         "participants":[{"role":"reviewer","vendor":null,"model":"default"}],
         "offer":"proactive","when_to_use":"After a change.","driver_guide":"## What to submit\nThe commit range."}
        """;

    [Test]
    public async Task Renders_the_guide_under_the_definition() {
        using var server = Serving("code-review", 200, CodeReview);

        var (text, isError) = await CallAsync(server, "code-review");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("code-review (v4) — single participant");
        await Assert.That(text).Contains("  reviewer: vendor unset");
        await Assert.That(text).Contains("when to use (offer proactively): After a change.");
        await Assert.That(text).Contains("Driver guide:\n## What to submit\nThe commit range.");
    }

    [Test]
    public async Task A_definition_without_a_guide_says_so() {
        using var server = Serving("plain", 200, """{"id":"plain","participants":[{"role":"reviewer","model":"default"}],"offer":"on_request"}""");

        var (text, isError) = await CallAsync(server, "plain");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("This definition publishes no driver guide");
    }

    [Test]
    public async Task An_unknown_id_is_an_error_naming_the_listing() {
        using var server = Serving("nope", 404, """{"status":404,"detail":"Flow definition 'nope' is not available."}""", "application/problem+json");

        var (text, isError) = await CallAsync(server, "nope");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Flow definition 'nope' is not available.");
        await Assert.That(text).Contains("list_flow_definitions");
    }

    [Test]
    public async Task An_older_server_without_the_route_is_reported_not_errored() {
        using var server = Serving("code-review", 404, "", "text/plain");

        var (text, isError) = await CallAsync(server, "code-review");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo(McpFlowsServer.ServerPublishesNoGuides);
    }

    [Test]
    public async Task A_catalog_that_has_not_projected_is_a_retryable_refusal() {
        using var server = Serving("code-review", 409, """{"error":"server_catching_up","message":"Flows are temporarily unavailable."}""");

        var (text, isError) = await CallAsync(server, "code-review");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains(McpFlowsServer.ServerCatchingUpGuidance);
    }

    [Test]
    public async Task A_missing_definition_id_is_rejected_without_a_request() {
        using var server = Serving("x", 200, CodeReview);
        using var client = new HttpClient();

        var (text, isError) = Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(new JsonObject()), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("definition_id is required");
        await Assert.That(server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task An_id_is_escaped_into_one_path_segment() {
        using var server = Serving("a%2Fb", 404, "");
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(new JsonObject { ["definition_id"] = "a/b" }), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null);

        await Assert.That(server.LogEntries.Single().RequestMessage.AbsolutePath).IsEqualTo("/api/flows/definitions/a%2Fb");
    }

    [Test]
    public async Task The_tool_is_read_only_and_requires_definition_id() {
        var tool = McpFlowsServer.BuildToolsList().Single(t => t.Name == "get_flow_definition");

        await Assert.That(tool.Annotations).IsEqualTo(McpToolAnnotations.Read);
        await Assert.That(tool.InputSchema.Required).IsEquivalentTo(new[] { "definition_id" });
        await Assert.That(tool.Description).Contains("before start_flow");
    }
}
```

If `WireMockServer` un-escapes `%2F` when it matches paths, drop the `Serving` in `An_id_is_escaped_into_one_path_segment`, start a bare `WireMockServer.Start()`, and keep the `AbsolutePath` assertion on the log entry. If the log normalises the escape away, assert on `RequestMessage.Url` containing `a%2Fb` instead.

In `McpToolAnnotationsTests.Reads_are_read_only_and_writes_say_what_they_do`, after the `list_flow_definitions` line, add:

```csharp
        await Assert.That(Tool("kcap-flows", "get_flow_definition").Annotations.ReadOnlyHint).IsTrue();
```

In `McpFlowsServerTests.cs:308`, change `IsEqualTo(12)` to `IsEqualTo(13)` and add:

```csharp
            await Assert.That(names.Contains("get_flow_definition")).IsTrue();
```

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpFlowsServerFlowDefinitionDetailTests/*"`
Expected: compile errors, because `ServerPublishesNoGuides` is missing.

- [ ] **Step 3: Implement.**

Add the dispatch branch directly after the `list_flow_definitions` branch:

```csharp
            if (toolName is "get_flow_definition") {
                var definitionId = arguments?["definition_id"] is JsonValue dv && dv.TryGetValue(out string? s) ? s?.Trim() : null;
                if (string.IsNullOrEmpty(definitionId))
                    return BuildToolResult(id, "Error: definition_id is required — pass an id from list_flow_definitions.", isError: true);

                var lookup = await GetBoundedAsync(client, $"{apiRoot}/api/flows/definitions/{Uri.EscapeDataString(definitionId)}", clock);
                if (lookup.Response is null)
                    return BuildToolResult(id, $"Error: reading flow definition '{definitionId}' {lookup.How}; nothing was started — retry the call.", isError: true);

                using var detailResp = lookup.Response;
                var detailBody       = await detailResp.Content.ReadAsStringAsync();

                if (detailResp.StatusCode == HttpStatusCode.Unauthorized)
                    return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(store, profiles.Name, apiRoot, time), isError: true);

                // A server that serves the route answers an unknown id with a problem body; a routing 404 has none.
                if (detailResp.StatusCode == HttpStatusCode.NotFound)
                    return ProblemDetail(detailBody) is { } detail
                        ? BuildToolResult(id, $"Error: {detail} Call list_flow_definitions for the ids this server can start.", isError: true)
                        : BuildToolResult(id, ServerPublishesNoGuides);

                if (!detailResp.IsSuccessStatusCode)
                    return BuildToolResult(id, FormatFlowStartError((int)detailResp.StatusCode, detailBody, wasDynamicStart: false), isError: true);

                return FormatFlowDefinitionDetail(detailBody) is { } rendered
                    ? BuildToolResult(id, rendered)
                    : BuildToolResult(id, $"Error: unreadable flow definition from GET /api/flows/definitions/{definitionId}.", isError: true);
            }
```

Use whatever name the surrounding branches use for the tool arguments object. Read the `start_flow` branch, which reads `definition_id`, and reuse its accessor rather than the inline `arguments?[…]` above if one exists.

Add beside `FormatFlowDefinitions`:

```csharp
    internal const string ServerPublishesNoGuides =
        "This server does not publish flow guides (GET /api/flows/definitions/{id} is not available). " +
        "Use the definition's description from list_flow_definitions and the agent-flows skill's rules.";

    /// <summary>Renders GET /api/flows/definitions/{id}: the listing entry, then the authored driver guide verbatim.
    /// Null when the body is not a definition.</summary>
    internal static string? FormatFlowDefinitionDetail(string body) {
        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch (JsonException) { return null; }

        if (root is not JsonObject definition || definition["id"] is not JsonValue) return null;

        var listing = FormatFlowDefinitions(new JsonObject { ["definitions"] = new JsonArray(definition.DeepClone()) }.ToJsonString());
        if (listing is null) return null;

        // Keep the entry lines only: drop the "flow_definitions (1):" header and the closing hint.
        var lines = listing.Split('\n');
        var entry = string.Join('\n', lines[1..^1]).TrimEnd();

        var guide = definition["driver_guide"] is JsonValue g && g.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? $"Driver guide:\n{text.Trim()}"
            : "This definition publishes no driver guide: follow its description and the agent-flows skill's rules.";

        return $"{entry}\n\n{guide}";
    }

    static string? ProblemDetail(string body) {
        try {
            return JsonNode.Parse(body) is JsonObject o && o["detail"] is JsonValue v && v.TryGetValue(out string? d) && !string.IsNullOrWhiteSpace(d) ? d : null;
        } catch (JsonException) {
            return null;
        }
    }
```

`new JsonArray(definition.DeepClone())` uses the `params JsonNode?[]` constructor, which is AOT-safe. A collection expression would not be (see CLAUDE.md).

Add the tool entry after `list_flow_definitions` in `BuildToolsList`:

```csharp
        new(
            "get_flow_definition",
            "Read one flow definition's guide before start_flow: what context to submit, how to iterate on its results and when to close. " +
            "Read-only and side-effect-free: this does NOT start anything. " +
            "Returns the definition's listing entry (participants, when to use it) followed by its authored driver guide. " +
            "An unknown, disabled or deleted id is an error — call list_flow_definitions for the ids this server can start. " +
            "A server that does not publish guides says so; then rely on the definition's description.",
            new(
                "object",
                new() {
                    ["definition_id"] = new("string", "Definition id from list_flow_definitions, e.g. 'code-review'.")
                },
                ["definition_id"]
            ),
            McpToolAnnotations.Read
        ),
```

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the Step 2 command, then the `McpToolAnnotationsTests` and `FlowsDriverSchemaConformanceTests` filters. Then run the integration project:
`dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- --treenode-filter "/*/*/McpFlowsServerTests/*"`
Expected: all pass.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli/Commands/McpFlowsServer.cs test/Capacitor.Cli.Tests.Unit/Commands/ test/Capacitor.Cli.Tests.Integration/McpFlowsServerTests.cs
git commit -m "Add get_flow_definition to read a flow's driver guide"
```

---

### Task 3: Session-start flows lane

**Files:**
- Create: `src/Capacitor.Cli/SessionStartMemory/SessionStartFlowsLane.cs`
- Modify: `src/Capacitor.Cli/SessionStartMemory/SessionStartMemoryContracts.cs:47-58` (`FlowsDisabled`, `AllLanesDisabled`)
- Modify: `src/Capacitor.Cli/SessionStartMemory/SessionStartCompositeContextProvider.cs` (third lane; generalised `Combine`/`Compose`)
- Modify: `src/Capacitor.Cli/SessionStartMemory/SessionStartMemoryOrchestrator.cs:29`
- Modify: `src/Capacitor.Cli/SessionStartMemory/SessionStartMemoryHookSupport.cs:30-42` (construct the lane; add `FlowsLaneDisabled`)
- Create: `test/Capacitor.Cli.Tests.Unit/SessionStartMemory/FlowsLaneTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/SessionStartMemory/GuidelinesLaneAndCompositeTests.cs:106-119` (composite factory gains the lane)

**Interfaces:**
- Consumes:
  - `SessionStartContextFetch.FetchAsync(HttpClient, string url, TimeProvider, CancellationToken) : SessionStartFetchOutcome(HttpStatusCode Status, byte[]? Body, TimeSpan? RetryAfter)`
  - `NextWorkUntrustedText.Render(string?, int)` (`Capacitor.Cli.Core.WorkItems`)
  - `McpServerNudgeAvailability.IsRegisteredFor(HarnessId, HarnessRegistry, string, string?)`
  - `KcapMcpServers.FlowsServerName`
- Produces:
  - `SessionStartMemoryContextRequest.FlowsDisabled` (bool, default `true`)
  - `SessionStartMemoryContextRequest.AllLanesDisabled` (bool)
  - `SessionStartFlowsLane(Func<CancellationToken, Task<HttpClient>> client, TimeProvider time)`, with:
    - `FetchAsync(SessionStartMemoryContextRequest, CancellationToken) : Task<SessionStartMemoryContextResult>`
    - `static BuildFragment(JsonNode?) : string?`
    - consts `Header`, `Footer`, `MaxDescribed = 10`, `MaxFragmentChars = 2048`
  - `SessionStartCompositeContextProvider(resolver, memory, guidelines, flows, time, diagnostic?)`
  - `SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId, HarnessRegistry, string? codexConfigPath = null) : bool`

- [ ] **Step 1: Write the failing tests.** Create `FlowsLaneTests.cs`. Reuse the private `Handler` and `FixedScope` stubs from `GuidelinesLaneAndCompositeTests` (lines 233-251) by moving them to a shared `internal` file in the same folder named `SessionStartStubs.cs`; that move is part of this task.

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.SessionStartMemory;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class FlowsLaneTests {
    static Func<CancellationToken, Task<HttpClient>> Lazy(HttpClient client) => _ => Task.FromResult(client);

    static SessionStartMemoryContextRequest Req(bool memory = false, bool guidelines = false, bool flows = true) =>
        new("https://example.test", "/repo", Disabled: !memory, TimeSpan.FromSeconds(1), CancellationToken.None,
            GuidelinesDisabled: !guidelines, FlowsDisabled: !flows);

    static SessionStartFlowsLane Lane(HttpStatusCode status, string body) =>
        new(Lazy(new HttpClient(new Handler(status, body, null))), TimeProvider.System);

    const string Catalog = """
        {"definitions":[
          {"id":"code-review","offer":"proactive","when_to_use":"After a change is complete."},
          {"id":"spec-review","offer":"proactive","when_to_use":"After a spec is final."},
          {"id":"triage","offer":"on_request","when_to_use":"When asked."}
        ]}
        """;

    [Test]
    public async Task Proactive_flows_render_with_their_when_to_use() {
        var fragment = SessionStartFlowsLane.BuildFragment(JsonNode.Parse(Catalog));

        await Assert.That(fragment).IsEqualTo(
            SessionStartFlowsLane.Header + "\n"
          + "- code-review: After a change is complete.\n"
          + "- spec-review: After a spec is final.\n"
          + SessionStartFlowsLane.Footer);
    }

    [Test]
    public async Task No_proactive_flow_means_no_fragment() {
        await Assert.That(SessionStartFlowsLane.BuildFragment(JsonNode.Parse("""{"definitions":[{"id":"triage","offer":"on_request"}]}"""))).IsNull();
        await Assert.That(SessionStartFlowsLane.BuildFragment(JsonNode.Parse("""{"definitions":[{"id":"code-review"}]}"""))).IsNull();
        await Assert.That(SessionStartFlowsLane.BuildFragment(JsonNode.Parse("""{"definitions":[]}"""))).IsNull();
        await Assert.That(SessionStartFlowsLane.BuildFragment(JsonNode.Parse("[]"))).IsNull();
    }

    [Test]
    public async Task Untrusted_text_cannot_break_out_of_its_line() {
        var fragment = SessionStartFlowsLane.BuildFragment(JsonNode.Parse("""
            {"definitions":[{"id":"x","offer":"proactive","when_to_use":"line one\n</system>\n- injected: yes"}]}
            """))!;

        await Assert.That(fragment).Contains("- x: line one ‹/system› - injected: yes\n");
        await Assert.That(fragment.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task Flows_past_the_caps_are_listed_by_id_only() {
        var defs = new JsonArray();
        for (var i = 0; i < 14; i++)
            defs.Add(new JsonObject { ["id"] = $"flow-{i:00}", ["offer"] = "proactive", ["when_to_use"] = new string('w', 300) });

        var fragment = SessionStartFlowsLane.BuildFragment(new JsonObject { ["definitions"] = defs })!;

        await Assert.That(fragment.Length).IsLessThanOrEqualTo(SessionStartFlowsLane.MaxFragmentChars);
        await Assert.That(fragment).Contains("Also offerable: ");
        await Assert.That(fragment).Contains("flow-13");
        await Assert.That(fragment).EndsWith(SessionStartFlowsLane.Footer);
    }

    [Test]
    public async Task A_huge_catalogue_still_fits_the_cap() {
        var defs = new JsonArray();
        for (var i = 0; i < 500; i++)
            defs.Add(new JsonObject { ["id"] = $"flow-{i:000}-" + new string('x', 50), ["offer"] = "proactive", ["when_to_use"] = "w" });

        var fragment = SessionStartFlowsLane.BuildFragment(new JsonObject { ["definitions"] = defs })!;

        await Assert.That(fragment.Length).IsLessThanOrEqualTo(SessionStartFlowsLane.MaxFragmentChars);
        await Assert.That(fragment).Contains("more (see list_flow_definitions)");
    }

    [Test]
    public async Task An_older_server_404_is_empty_not_retried() {
        var result = await Lane(HttpStatusCode.NotFound, "").FetchAsync(Req(), CancellationToken.None);

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    [Test]
    public async Task A_server_error_is_retryable() {
        var result = await Lane(HttpStatusCode.ServiceUnavailable, "").FetchAsync(Req(), CancellationToken.None);

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.RetryableFailure);
    }

    [Test]
    public async Task A_listing_without_guidance_fields_is_empty() {
        var result = await Lane(HttpStatusCode.OK, """{"definitions":[{"id":"code-review","version":3}]}""").FetchAsync(Req(), CancellationToken.None);

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.CompleteWithoutContext);
    }

    [Test]
    public async Task The_lane_reads_the_definitions_listing() {
        var handler = new Handler(HttpStatusCode.OK, Catalog, null);
        var lane    = new SessionStartFlowsLane(Lazy(new HttpClient(handler)), TimeProvider.System);

        var result = await lane.FetchAsync(Req(), CancellationToken.None);

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(handler.Uri!.AbsolutePath).IsEqualTo("/api/flows/definitions");
    }

    [Test]
    public async Task With_every_other_lane_off_the_composite_still_injects_flows() {
        var composite = Composite(memory: (HttpStatusCode.OK, "[]"), guidelines: (HttpStatusCode.NoContent, ""), flows: (HttpStatusCode.OK, Catalog));

        var result = await composite.GetAsync(Req(memory: false, guidelines: false, flows: true));

        await Assert.That(result.Disposition).IsEqualTo(SessionStartMemoryDisposition.Ready);
        await Assert.That(result.Fragment!).StartsWith(MemoryIndexEmitter.FragmentMarker + "\n" + SessionStartFlowsLane.Header);
    }

    [Test]
    public async Task A_failing_flows_lane_leaves_the_memory_fragment_untouched() {
        const string memoryBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";
        var withFlows    = await Composite(memory: (HttpStatusCode.OK, memoryBody), guidelines: (HttpStatusCode.NoContent, ""), flows: (HttpStatusCode.InternalServerError, ""))
            .GetAsync(Req(memory: true, flows: true));
        var withoutFlows = await Composite(memory: (HttpStatusCode.OK, memoryBody), guidelines: (HttpStatusCode.NoContent, ""), flows: (HttpStatusCode.OK, Catalog))
            .GetAsync(Req(memory: true, flows: false));

        await Assert.That(withFlows.Fragment).IsEqualTo(withoutFlows.Fragment);
    }

    [Test]
    public async Task Memory_comes_first_then_flows() {
        const string memoryBody = """[{"memory_id":"m1","slug":"s","audience":"org","description":"d","kind":"preference"}]""";

        var result = await Composite(memory: (HttpStatusCode.OK, memoryBody), guidelines: (HttpStatusCode.NoContent, ""), flows: (HttpStatusCode.OK, Catalog))
            .GetAsync(Req(memory: true, flows: true));

        var fragment = result.Fragment!;
        await Assert.That(fragment.IndexOf(MemoryIndexEmitter.FragmentMarker, StringComparison.Ordinal)).IsEqualTo(0);
        await Assert.That(fragment.IndexOf(SessionStartFlowsLane.Header, StringComparison.Ordinal)).IsGreaterThan(0);
    }

    static SessionStartCompositeContextProvider Composite(
            (HttpStatusCode, string) memory, (HttpStatusCode, string) guidelines, (HttpStatusCode, string) flows) {
        var scope = new FixedScope(new SessionStartMemoryScope("repo", "machine"));
        var time  = new FakeTimeProvider();

        return new SessionStartCompositeContextProvider(scope,
            new SessionStartMemoryContextProvider(scope, Lazy(new HttpClient(new Handler(memory.Item1, memory.Item2, null))), time),
            new SessionStartGuidelinesLane(Lazy(new HttpClient(new Handler(guidelines.Item1, guidelines.Item2, null))), time),
            new SessionStartFlowsLane(Lazy(new HttpClient(new Handler(flows.Item1, flows.Item2, null))), time),
            time);
    }
}
```

The `Req` positional and named arguments must match the record's parameter names (`BaseUrl, Cwd, Disabled, Budget, CancellationToken, GuidelinesDisabled, FlowsDisabled`). Adjust the `Disabled:` label if the compiler disagrees. The `Composite` helper mirrors the factory at `GuidelinesLaneAndCompositeTests.cs:106-119`; copy any extra arguments that factory passes.

In `GuidelinesLaneAndCompositeTests`, change its composite factory to pass a fourth lane, `new SessionStartFlowsLane(Lazy(new HttpClient(new Handler(HttpStatusCode.NotFound, "", null))), time)`. Its requests leave `FlowsDisabled` at the default `true`, so the lane never runs and those tests keep their expectations.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FlowsLaneTests/*"`
Expected: compile errors, because `SessionStartFlowsLane` and `FlowsDisabled` are missing.

- [ ] **Step 3: Implement.**

In `SessionStartMemoryContracts.cs`, extend the request and replace its long parameter comment with a one-liner:

```csharp
/// <summary>Each lane has its own opt-out. The guidelines and flows flags default to off so a memory-only
/// construction stays memory-only.</summary>
internal sealed record SessionStartMemoryContextRequest(
    string BaseUrl,
    string? Cwd,
    bool Disabled,
    TimeSpan Budget,
    CancellationToken CancellationToken,
    bool GuidelinesDisabled = true,
    bool FlowsDisabled = true) {
    public bool AllLanesDisabled => Disabled && GuidelinesDisabled && FlowsDisabled;
}
```

`SessionStartFlowsLane.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.SessionStartMemory;

/// <summary>The flows lane of the composite SessionStart context: the catalogue flows an operator marked
/// <c>offer: proactive</c>, each with when to offer it. Marker-less, like the guidelines lane.</summary>
internal sealed class SessionStartFlowsLane(Func<CancellationToken, Task<HttpClient>> client, TimeProvider time) {
    public const int MaxDescribed     = 10;
    public const int MaxFragmentChars = 2048;
    const int WhenToUseCap = 300;
    const int IdCap        = 64;

    public const string Header = "Flows you may offer (ask first; never start unprompted):";
    public const string Footer = "Before starting one, call get_flow_definition(<id>) and follow its guide.";

    public async Task<SessionStartMemoryContextResult> FetchAsync(SessionStartMemoryContextRequest request, CancellationToken ct) {
        var outcome = await SessionStartContextFetch.FetchAsync(
            await client(ct), request.BaseUrl.TrimEnd('/') + "/api/flows/definitions", time, ct);

        // A server older than the listing has nothing to offer, now or on a retry.
        if (outcome.Status == HttpStatusCode.NotFound) return SessionStartMemoryContextResult.Empty;
        if (outcome.Body is null)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.RetryableFailure, RetryAfter: outcome.RetryAfter);

        return BuildFragment(JsonNode.Parse(outcome.Body)) is { } fragment
            ? new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, fragment)
            : SessionStartMemoryContextResult.Empty;
    }

    public static string? BuildFragment(JsonNode? root) {
        if (root is not JsonObject { } obj || obj["definitions"] is not JsonArray definitions) return null;

        var offered = definitions.OfType<JsonObject>()
            .Where(d => Str(d, "offer") == "proactive")
            .Select(d => (Id: NextWorkUntrustedText.Render(Str(d, "id"), IdCap), When: NextWorkUntrustedText.Render(Str(d, "when_to_use"), WhenToUseCap)))
            .Where(f => f.Id.Length > 0)
            .ToList();
        if (offered.Count == 0) return null;

        // Described in order until the count or the room runs out; everything after falls back to its id, so the
        // server's id order survives. The overflow line has its own fixed reserve, so the block never passes the cap.
        var room      = MaxFragmentChars - Header.Length - Footer.Length - 2 - OverflowReserve;
        var described = new StringBuilder();
        var idsOnly   = new List<string>();
        var count     = 0;

        foreach (var (id, when) in offered) {
            var line = when.Length > 0 ? $"- {id}: {when}\n" : $"- {id}\n";
            if (idsOnly.Count == 0 && count < MaxDescribed && described.Length + line.Length <= room) {
                described.Append(line);
                count++;
            } else {
                idsOnly.Add(id);
            }
        }

        var sb = new StringBuilder();
        sb.Append(Header).Append('\n').Append(described);
        if (idsOnly.Count > 0) sb.Append(Overflow(idsOnly)).Append('\n');
        sb.Append(Footer);

        return sb.ToString();

        static string Overflow(List<string> ids) {
            var line = new StringBuilder("Also offerable: ");
            var shown = 0;
            foreach (var id in ids) {
                var piece = shown == 0 ? id : ", " + id;
                if (line.Length + piece.Length > OverflowReserve - OverflowTail.Length) break;
                line.Append(piece);
                shown++;
            }

            if (shown < ids.Count) line.Append($" and {ids.Count - shown} more").Append(OverflowTail);

            return line.ToString();
        }

        static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    }

    const int    OverflowReserve = 400;
    const string OverflowTail    = " (see list_flow_definitions)";
}
```

`OverflowReserve` is held back from the described lines even when nothing overflows. That keeps the arithmetic one rule, and at typical `when_to_use` lengths ten flows still fit in the remaining ~1500 characters.

In `SessionStartCompositeContextProvider.cs`:
- Add `SessionStartFlowsLane flows` as the fourth constructor parameter, after `guidelines`.
- Rewrite the summary in one or two lines: one fragment from the memory, guidelines and flows lanes, the memory marker first.
- Replace `GetAsync` through `MaxRetryAfter` with:

```csharp
    public async Task<SessionStartMemoryContextResult> GetAsync(SessionStartMemoryContextRequest request) {
        if (request.AllLanesDisabled) return SessionStartMemoryContextResult.Empty;
        if (request.Budget <= TimeSpan.Zero) return SessionStartMemoryContextResult.Retry;

        using var expiry = new CancellationTokenSource(request.Budget, time);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, expiry.Token);

        SessionStartMemoryScope scope;
        try {
            scope = await scopeResolver.ResolveAsync(request.Cwd, request.Budget, cts.Token);
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart scope resolution skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }

        // Started before any await so the lanes share the budget in parallel.
        var memoryTask     = request.Disabled           ? null : RunLaneAsync(() => memory.FetchWithScopeAsync(scope, request, cts.Token));
        var guidelinesTask = request.GuidelinesDisabled ? null : RunLaneAsync(() => guidelines.FetchWithScopeAsync(scope, request, cts.Token));
        var flowsTask      = request.FlowsDisabled      ? null : RunLaneAsync(() => flows.FetchAsync(request, cts.Token));

        var memoryResult = memoryTask is null ? null : await memoryTask;
        SessionStartMemoryContextResult?[] others = [
            guidelinesTask is null ? null : await guidelinesTask,
            flowsTask      is null ? null : await flowsTask
        ];

        return Combine(memoryResult, others);
    }

    async Task<SessionStartMemoryContextResult> RunLaneAsync(Func<Task<SessionStartMemoryContextResult>> lane) {
        try {
            return await lane();
        } catch (Exception ex) when (IsFailOpen(ex)) {
            diagnostic?.Invoke($"SessionStart context lane skipped: {ex.Message}");
            return SessionStartMemoryContextResult.Retry;
        }
    }

    static SessionStartMemoryContextResult Combine(SessionStartMemoryContextResult? memoryResult, SessionStartMemoryContextResult?[] others) {
        var memoryFragment = Ready(memoryResult);
        var otherFragments = others.Select(Ready).OfType<string>().ToList();

        if (memoryFragment is not null || otherFragments.Count > 0)
            return new SessionStartMemoryContextResult(SessionStartMemoryDisposition.Ready, Compose(memoryFragment, otherFragments));

        SessionStartMemoryContextResult?[] all = [memoryResult, ..others];
        var retries = all.Where(r => r?.Disposition == SessionStartMemoryDisposition.RetryableFailure).ToList();
        if (retries.Count == 0) return SessionStartMemoryContextResult.Empty;

        return new SessionStartMemoryContextResult(
            SessionStartMemoryDisposition.RetryableFailure, RetryAfter: retries.Max(r => r!.RetryAfter));

        static string? Ready(SessionStartMemoryContextResult? r) =>
            r is { Disposition: SessionStartMemoryDisposition.Ready, Fragment: { } f } ? f : null;
    }

    /// <summary>Marker first: Pi and OpenCode capture stdout only when it opens with it, and only the memory
    /// fragment carries it.</summary>
    static string Compose(string? memoryFragment, IReadOnlyList<string> others) {
        var rest = string.Join("\n\n", others);
        if (memoryFragment is null) return MemoryIndexEmitter.FragmentMarker + "\n" + rest;

        return others.Count == 0 ? memoryFragment : memoryFragment + "\n\n" + rest;
    }
```

`retries.Max(r => r!.RetryAfter)` over `TimeSpan?` returns the largest non-null value, or null when every value is null, which is what `MaxRetryAfter` did; delete `MaxRetryAfter`. Keep `IsFailOpen` unchanged.

In `SessionStartMemoryOrchestrator.cs:29`, replace the line and its three-line comment with:

```csharp
        // Every lane off spends no lease, so a lane switched on mid-session can still inject on a later callback.
        if (request.AllLanesDisabled) return null;
```

In `SessionStartMemoryHookSupport.cs`:

```csharp
        var memory     = new SessionStartMemoryContextProvider(resolver, client, time);
        var guidelines = new SessionStartGuidelinesLane(client, time);
        var flows      = new SessionStartFlowsLane(client, time);
        return new SessionStartCompositeContextProvider(resolver, memory, guidelines, flows, time);
    }

    /// <summary>Offering a flow the agent cannot start is worse than not offering it.</summary>
    public static bool FlowsLaneDisabled(HarnessId harness, HarnessRegistry harnesses, string? codexConfigPath = null) =>
        !McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, KcapMcpServers.FlowsServerName, codexConfigPath);
```

Add any `using` directives the compiler asks for (`Capacitor.Cli.Core.Mcp` for `KcapMcpServers`; `HarnessId` and `HarnessRegistry` live in Core).

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the Step 2 command. Then run `--treenode-filter "/*/*/GuidelinesLaneAndCompositeTests/*"`, and the same filter for `SessionStartMemoryOrchestratorTests` if that class exists. Expected: all pass.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli/SessionStartMemory/ test/Capacitor.Cli.Tests.Unit/SessionStartMemory/
git commit -m "Offer proactive catalogue flows in session-start context"
```

---

### Task 4: Wire the flows lane into every harness hook

**Files:**
- Modify (non-Claude hooks, each at its `StartMemoryIndexTask` and its caller):
  - `src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs` (~142-171, ~377-388)
  - `CursorHookCommand.cs` (~583-617)
  - `CopilotHookCommand.cs` (~117-124)
  - `GeminiHookCommand.cs` (~199-204)
  - `KiroHookCommand.cs` (~109-116)
  - `OpenCodeHookCommand.cs` (~265-270)
  - `AntigravityHookCommand.cs` (~309-314)
  - `PiHookCommand.cs` (~331-336)
- Modify: `src/Capacitor.Cli/Commands/Harness/ClaudeHookCommand.cs` (~719-727, ~1177-1200)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/Harness/ClaudeHookCommandTests.cs` (Fixture plus two tests)
- Test: `test/Capacitor.Cli.Tests.Unit/SessionStartMemory/FlowsLaneTests.cs` (gate helper)

**Interfaces:**
- Consumes: `SessionStartMemoryHookSupport.CompositeProvider`, `FlowsLaneDisabled`, `SessionStartMemoryContextRequest.FlowsDisabled` / `AllLanesDisabled` (Task 3).

- [ ] **Step 1: Write the failing tests.**

In `ClaudeHookCommandTests.Fixture`, add:

```csharp
        public string FlowDefinitionsBody { get; set; } = """{"definitions":[]}""";
        public int FlowDefinitionsRequestCount => _memoryServer.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows/definitions");
```

At the end of `StubMemoryServer()`, add:

```csharp
            _memoryServer.Given(Request.Create().WithPath("/api/flows/definitions").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json").WithBody(FlowDefinitionsBody));
```

Add the tests beside `session_start_with_only_a_ready_memory_index_emits_just_the_memory_fragment`:

```csharp
    const string ProactiveCatalog = """{"definitions":[{"id":"code-review","offer":"proactive","when_to_use":"After a change is complete."}]}""";

    [Test, NotInParallel]
    public async Task session_start_offers_proactive_flows_when_kcap_flows_is_registered() {
        using var absent = new TempDir();
        using var fx = new Fixture(Config.Root) { RespondJson = "{}", FlowDefinitionsBody = ProactiveCatalog };
        fx.RegisterClaudeMcpServer("kcap-flows");

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"{{AbsentCwd(absent)}}","source":"startup"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout).Contains("Flows you may offer");
        await Assert.That(stdout).Contains("- code-review: After a change is complete.");
    }

    [Test, NotInParallel]
    public async Task without_the_flows_mcp_server_flows_are_neither_requested_nor_offered() {
        using var absent = new TempDir();
        using var fx = new Fixture(Config.Root) { RespondJson = "{}", FlowDefinitionsBody = ProactiveCatalog };

        var sid = Guid.NewGuid().ToString("N");
        var (exit, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"{{AbsentCwd(absent)}}","source":"startup"}"""));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.FlowDefinitionsRequestCount).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("Flows you may offer");
    }

    [Test, NotInParallel]
    public async Task with_memory_disabled_the_flows_offer_still_arrives() {
        using var absent = new TempDir();
        using var fx = new Fixture(Config.Root, profile: new Profile { DisableMemoryIndex = true }) { RespondJson = "{}", FlowDefinitionsBody = ProactiveCatalog };
        fx.RegisterClaudeMcpServer("kcap-flows");

        var sid = Guid.NewGuid().ToString("N");
        var (_, stdout) = await RunCapturingStdoutAsync(() =>
            fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{sid}}","cwd":"{{AbsentCwd(absent)}}","source":"startup"}"""));

        await Assert.That(stdout).Contains("Flows you may offer");
        await Assert.That(fx.MemoryIndexRequested).IsFalse();
    }
```

Build the `Profile` the way other tests in this file do; search for `DisableMemoryIndex` in the file, and if no test sets it, construct it with the property initializer above.

Append to `FlowsLaneTests` a gate check that needs no hook:

```csharp
    [Test]
    public async Task The_lane_is_off_for_a_harness_without_kcap_flows() {
        using var home = new TempDir();

        await Assert.That(SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.Codex, TestHarnesses.Under(home))).IsTrue();
    }
```

`TestHarnesses.Under(home)` is the registry the Claude fixture uses. Match its parameter type there; if it takes a `UserHome`, build one from `home.Path` as that fixture does.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudeHookCommandTests/*"`
Expected: the three new tests FAIL (no flows block; memory-disabled returns early). Everything else passes.

- [ ] **Step 3: Implement the Claude path.** In `ClaudeHookCommand.cs`, beside `memoryDisabled` (~719):

```csharp
            var flowsDisabled = SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.Claude, harnesses);
```

Pass it into the task: `StartMemoryIndexTask(nativeSessionId, sessionCwd, memoryDisabled, flowsDisabled, lifecycleReason, budget.Remaining)`. Then rewrite `StartMemoryIndexTask`:

```csharp
    async Task<string?> StartMemoryIndexTask(
        string? nativeSessionId,
        string? cwd,
        bool disabled,
        bool flowsDisabled,
        SessionLifecycleReason reason,
        TimeSpan budget) {
        if ((disabled && flowsDisabled) || string.IsNullOrEmpty(nativeSessionId) || budget <= TimeSpan.Zero)
            return null;

        try {
            var store    = SessionStartMemoryLeaseStore.Create(config, clock.Time);
            var provider = SessionStartMemoryHookSupport.CompositeProvider(router, config, workdir, http.ForMemoryAsync, clock.Time);

            // Claude's guidelines arrive with the SessionStart response, so the composite runs memory and flows only.
            return await new SessionStartMemoryOrchestrator(store, provider, clock.Time).GetFragmentAsync(
                new SessionMemoryLifecycle(HarnessId.Claude, nativeSessionId, null,
                    IsTopLevel: true, ClassificationAuthoritative: true, reason,
                    CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(Url, cwd, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: true, FlowsDisabled: flowsDisabled));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return null;
        }
    }
```

The envelope assembly needs no change: the composed fragment arrives through `memoryFragment`.

- [ ] **Step 4: Implement the eight non-Claude hooks.** Make the same three edits in each file. The Codex version is shown in full; the other seven differ only in the harness id and where their flags come from.

Codex (`CodexHookCommand.cs`):

(a) `StartMemoryIndexTask` gains `bool flowsDisabled` after `guidelinesDisabled`. Its guard becomes:

```csharp
        if ((disabled && guidelinesDisabled && flowsDisabled) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero)
            return null;
```

(b) The request it builds becomes:

```csharp
                new SessionStartMemoryContextRequest(Url, scopeRoot, disabled, budget, CancellationToken.None,
                    GuidelinesDisabled: guidelinesDisabled, FlowsDisabled: flowsDisabled));
```

(c) The caller (~377-388) passes:

```csharp
            activeProfile?.DisableSessionGuidelines is true,
            SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.Codex, harnesses),
            budget.Remaining);
```

If the Codex hook already resolves a Codex config path for `IsRegisteredFor` or `WorkItemsNudgeEmitter.ToolsRegisteredFor` elsewhere in the file, pass that same path as `codexConfigPath`.

Apply (a), (b) and (c) to Cursor, Copilot, Gemini, Kiro, OpenCode, Antigravity and Pi with `HarnessId.Cursor`, `.Copilot`, `.Gemini`, `.Kiro`, `.OpenCode`, `.Antigravity` and `.Pi`.
- Cursor computes `guidelinesDisabled` as a local (~583) and pre-checks it (~584). Add `var flowsDisabled = SessionStartMemoryHookSupport.FlowsLaneDisabled(HarnessId.Cursor, harnesses);` beside it and extend that pre-check to all three flags.
- Every hook holds a `harnesses` field.
- The harness-id member names must match the `HarnessId` type; let the compiler confirm them.

- [ ] **Step 5: Run the tests and confirm they pass.**

Run the Step 2 command, then the full unit suite: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`. Expected: all pass. A test in the Claude class that registers `kcap-flows` (line ~572) now also triggers a flows GET; the default body is an empty catalogue, so its output does not change. If it asserts `ServerRequestCount`, update the expected count by one and say why in the commit body.

- [ ] **Step 6: Commit.**

```bash
git add src/Capacitor.Cli/Commands/Harness/ test/Capacitor.Cli.Tests.Unit/
git commit -m "Run the flows lane from every harness's SessionStart hook" -m "Claude moves to the composite provider with guidelines off; its guidelines arrive with the SessionStart response."
```

---

### Task 5: Skills — one generic driver, retire suggest-review-flow

**Files:**
- Modify: `kcap/skills/agent-flows/SKILL.md` (frontmatter; "When NOT to use"; "Choosing the flow definition"; new "Read the flow's guide first"; rule 8; Workflow; Tool reference)
- Modify: `kcap/skills/review-flows/SKILL.md` (description pointer)
- Delete: `kcap/skills/suggest-review-flow/`
- Modify: `src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs` (`SourceNames`; new `RetiredSourceNames`; prune in `Install` and `Remove`)
- Modify: `src/Capacitor.Cli.Core/Resources/help-plugin.txt:163-165`
- Modify: `test/Capacitor.Cli.Core.Tests.Unit/AgentsSkillsInstallerTests.cs:6` (mirror) and a new prune test
- Delete: `test/Capacitor.Cli.Tests.Unit/Commands/SuggestReviewFlowSkillConformanceTests.cs`
- Create: `test/Capacitor.Cli.Tests.Unit/Commands/AgentFlowsSkillConformanceTests.cs`

**Interfaces:**
- Produces: `AgentsSkillsInstaller.RetiredSourceNames : string[]` = `["suggest-review-flow"]`.

- [ ] **Step 1: Write the failing tests.**

Create `AgentFlowsSkillConformanceTests.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>agent-flows is the one skill that drives any catalogue flow, offered ones included. Its folded
/// description is the trigger surface a harness sees; a strict harness drops a skill over 1024 characters.</summary>
public class AgentFlowsSkillConformanceTests {
    static string SkillText() => File.ReadAllText(Path.Combine(RepoTree.SkillsSource(), "agent-flows", "SKILL.md"));

    static string FoldedDescription() {
        var text = SkillText().Replace("\r\n", "\n").Replace("\r", "\n");
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?m)^description: >-\n((?:^  .*\n)+)");
        return System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
    }

    [Test]
    public async Task Description_stays_within_the_1024_char_skill_limit() {
        var len = FoldedDescription().Length;
        await Assert.That(len).IsGreaterThan(0);
        await Assert.That(len).IsLessThanOrEqualTo(1024);
    }

    [Test]
    public async Task Description_triggers_on_an_offered_flow_from_session_context() {
        var d = FoldedDescription();
        await Assert.That(d).Contains("Flows you may offer");
        await Assert.That(d).Contains("get_flow_definition");
    }

    [Test]
    public async Task Body_reads_the_guide_before_starting() =>
        await Assert.That(SkillText()).Contains("## Read the flow's guide first");

    [Test]
    public async Task Body_names_no_built_in_definition_as_the_default_choice() {
        var text = SkillText();
        await Assert.That(text).DoesNotContain("Spec or design document → `definition_id: \"spec-review\"`");
        await Assert.That(text).DoesNotContain("For a code review flow (`definition_id: \"code-review\"`)");
    }

    [Test]
    public async Task Suggest_review_flow_is_retired() {
        await Assert.That(Directory.Exists(Path.Combine(RepoTree.SkillsSource(), "suggest-review-flow"))).IsFalse();
        await Assert.That(AgentsSkillsInstaller.RetiredSourceNames).Contains("suggest-review-flow");
    }
}
```

In `AgentsSkillsInstallerTests.cs`, remove `"suggest-review-flow"` from the mirror array on line 6, and add:

```csharp
    [Test]
    public async Task Install_prunes_a_retired_skill_folder() {
        using var tmp = new TempDir();
        var target = tmp.CreateDir("skills");
        tmp.CreateFile("skills/kcap-suggest-review-flow/SKILL.md", "stale");

        await Assert.That(AgentsSkillsInstaller.Install(RepoTree.SkillsSource(), target)).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-suggest-review-flow"))).IsFalse();
    }

    [Test]
    public async Task Remove_also_removes_a_retired_skill_folder() {
        using var tmp = new TempDir();
        var target = tmp.CreateDir("skills");
        tmp.CreateFile("skills/kcap-suggest-review-flow/SKILL.md", "stale");

        AgentsSkillsInstaller.Remove(target);

        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-suggest-review-flow"))).IsFalse();
    }

    [Test]
    public async Task A_retired_name_is_never_a_source_name() =>
        await Assert.That(AgentsSkillsInstaller.RetiredSourceNames.Intersect(AgentsSkillsInstaller.SourceNames)).IsEmpty();
```

Check `TempDir.CreateFile`'s signature in `test/Capacitor.Tests.Helpers` (relative path plus content, and whether it creates parent directories). If it does not create parents, call `tmp.CreateDir("skills/kcap-suggest-review-flow")` first.

Delete `test/Capacitor.Cli.Tests.Unit/Commands/SuggestReviewFlowSkillConformanceTests.cs`.

- [ ] **Step 2: Run the tests and confirm they fail.**

Run:
- `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentFlowsSkillConformanceTests/*"`
- `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentsSkillsInstallerTests/*"`

Expected: compile error (`RetiredSourceNames`), then content failures.

- [ ] **Step 3: Implement the installer.** In `AgentsSkillsInstaller.cs`:
- Remove `"suggest-review-flow",` from `SourceNames`.
- Add after `SourceNames`:

```csharp
    /// <summary>Skills this installer once shipped. Their <c>kcap-</c> folders are deleted on install and remove,
    /// so an upgraded user does not keep a skill that no longer matches the tools.</summary>
    public static readonly string[] RetiredSourceNames = [
        "suggest-review-flow"
    ];
```

In `Install`, after the `foreach (var name in SourceNames)` copy loop and before `WriteMarker`:

```csharp
            foreach (var name in RetiredSourceNames) {
                var retired = Path.Combine(targetDir, "kcap-" + name);
                if (Directory.Exists(retired)) Directory.Delete(retired, recursive: true);
            }
```

In `Remove`, change `foreach (var name in SourceNames)` to `foreach (var name in SourceNames.Concat(RetiredSourceNames))`.

In `help-plugin.txt:163-165`, remove `suggest-review-flow,` from the brace list and keep the line wrapping tidy.

- [ ] **Step 4: Implement the skills.**

Delete `kcap/skills/suggest-review-flow/` (`git rm -r`).

In `kcap/skills/agent-flows/SKILL.md`:

(a) Replace the whole `description: >-` block with:

```yaml
description: >-
  Use this skill to drive a structured agent *flow*: a separate hosted
  participant agent runs a definition from the server's flow catalogue and
  iterates with you until sign-off. Use it when the user names a flow or
  definition id ("run the code-review flow", "start a flow", "use flow
  definition X"), when the user accepts a flow you offered, or when you are
  about to offer one listed under "Flows you may offer" in your session
  context because its when-to-use applies. Before starting a flow, call
  get_flow_definition and follow its guide. Do NOT use this skill (and do NOT
  call the flows MCP tools) for an ordinary request such as "review my PR",
  "do X for me", or "check this over" where the user wants you to do the work
  yourself — do it directly instead. To start a hosted agent that works on its
  own, use `start-agents`.
```

(b) In "## When NOT to use this skill / these tools", replace the final paragraph ("Only start a flow when the user explicitly asks…") with:

```markdown
Only start a flow when the user explicitly asks for one — e.g. "start a flow", "run the code-review flow", "use flow definition X", or "re-review after I address the findings" via a flow — or accepts a flow you offered. Offer a flow only when your session context lists it under **Flows you may offer** and its when-to-use applies to what just happened; ask, and never start it before the user says yes. Skip the offer when the user asked you to do the work yourself, or while you are mid-task.
```

(c) In "## Choosing the flow definition", replace the two bullets that begin "Spec or design document →" and "Code changes or a pull request →" with one bullet:

```markdown
- The flow the user named, or the one you offered and they accepted.
```

Keep the `list_flow_definitions` bullet and everything after it in that section, including the pinned `### If \`start_flow\` has no \`vendor\` parameter` subsection, unchanged.

(d) Insert a new section directly before "## Composing a dynamic flow":

```markdown
## Read the flow's guide first

Call `get_flow_definition(definition_id)` before `start_flow`. The guide is written by whoever published the flow: what to put in `context`, which target to name, how to iterate on its results and when to close. Where it differs from the generic rules below, follow the guide. A server that does not publish guides says so; then work from the definition's description and the rules below.
```

(e) Replace Core rule 8 with:

```markdown
8. **Ask the participant only for what the flow's guide allows.** If the guide says CI covers tests, do not ask the participant to run them; participant feedback is on what the flow exists to judge.
```

(f) At the top of "## Workflow", before the first code block, insert:

```markdown
Every flow starts the same way:

```
get_flow_definition(definition_id)
  → read the driver guide; prepare the context it asks for
```
```

Render the inner fence as a real nested code block in the file: a plain paragraph followed by a fenced block.

(g) In the "## Tool reference" table, add a row after `list_flow_definitions`:

```markdown
| `get_flow_definition` | `definition_id` | — | Before `start_flow`, every time. Read-only: returns the definition's participants and when to use it, then its authored driver guide (what to submit, how to iterate, when to close). An unknown, disabled or deleted id is an error; an older server reports that it publishes no guides. |
```

In `kcap/skills/review-flows/SKILL.md`, append one sentence to the end of the `description: >-` block (keep it folded and under 1024 characters):

```
  For any other catalogue flow, or a flow offered from your session context,
  use `agent-flows`.
```

- [ ] **Step 5: Run the tests and confirm they pass.**

Run both Step 2 commands, then the `FlowsDriverSchemaConformanceTests` filter (it pins the `agent-flows` vendor heading block). Expected: all pass.

- [ ] **Step 6: Commit.**

```bash
git add -A kcap/skills src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs src/Capacitor.Cli.Core/Resources/help-plugin.txt test/Capacitor.Cli.Core.Tests.Unit/AgentsSkillsInstallerTests.cs test/Capacitor.Cli.Tests.Unit/Commands/
git commit -m "Drive every catalogue flow through agent-flows" -m "suggest-review-flow is pruned from installed skill trees, so an upgraded user is not offered review twice."
```

---

### Task 6: Agent instructions, help text, README

**Files:**
- Modify: `src/Capacitor.Cli.Core/Instructions/KcapAgentInstructions.cs:45-52` (the "When you **finish implementing a change**" paragraph)
- Modify: `test/Capacitor.Cli.Core.Tests.Unit/Instructions/AgentInstructionsWriterTests.cs:11-12`
- Modify: `src/Capacitor.Cli.Core/Resources/help-mcp.txt:80-87` (generic-flow tools)
- Modify: `README.md`:
  - the `### Flows MCP server (for agents)` bullets (~604-610), with a `get_flow_definition` bullet after line 608
  - a lane paragraph after the bullets
  - the skills table (~1515-1530): delete the `kcap-suggest-review-flow` row and update the `kcap-agent-flows` row

- [ ] **Step 1: Write the failing test.** In `AgentInstructionsWriterTests.cs`, replace lines 11-12 with:

```csharp
        await Assert.That(KcapAgentInstructions.Body).Contains("Flows you may offer");
        await Assert.That(KcapAgentInstructions.Body).Contains("get_flow_definition");
        await Assert.That(KcapAgentInstructions.Body).DoesNotContain("proactively OFFER an independent second-harness review");
```

- [ ] **Step 2: Run the test and confirm it fails.**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentInstructionsWriterTests/*"`
Expected: FAIL.

- [ ] **Step 3: Implement.**

In `KcapAgentInstructions.Body`, replace the paragraph starting "When you **finish implementing a change**" through "…isn't finished." with:

```
        When your session context lists **Flows you may offer**, OFFER one whose *when to use* fits what
        just happened — for example on finishing a change or finalizing a spec — even if the user didn't
        ask. Start it with `kcap-flows` `start_flow` ONLY after the user accepts; never auto-start it.
        Before starting, call `kcap-flows` `get_flow_definition` and follow its guide. Skip the offer when
        the user asked you to do the work yourself, or while you're mid-task.
```

Keep the preceding "When asked to **request or submit a review**" paragraph unchanged; the review aliases still exist.

In `help-mcp.txt`, extend the generic-flow tools block:

```
      Generic-flow tools (catalog + dynamic definitions):
        list_flow_definitions  List the definitions this server can start, with when
                               to use each and whether agents may offer it unprompted.
        get_flow_definition    Read one definition's driver guide: what to submit,
                               how to iterate, when to close.
        start_flow             …(unchanged)
```

Add after the `Claude Code auto-registers …` paragraph:

```
  When `kcap-flows` is registered for a harness, the SessionStart hook lists the
  catalogue flows an operator marked `offer: proactive`, so the agent can offer
  them at the right moment. Nothing is listed against a server that predates
  flow guidance.
```

In `README.md`, after the `list_flow_definitions` bullet in `### Flows MCP server (for agents)`:

```markdown
- **`get_flow_definition`** — read one definition's driver guide before starting it: its participants, when to use it, and the authored guidance on what context to submit, how to iterate and when to close. Read-only. An unknown, disabled or deleted id is an error; a server that predates flow guides is reported as publishing none.
```

Append to the `list_flow_definitions` bullet: ` Each entry also carries when to use it and whether agents may offer it unprompted (\`offer: proactive\`) or run it only on request.`

After the tool bullets, add:

```markdown
**Offered flows.** When `kcap-flows` is registered for a harness, the SessionStart hook adds a short "Flows you may offer" block listing the catalogue flows marked `offer: proactive`, each with when to use it. The agent offers one at that moment and starts it only after you accept. The built-in `spec-review` and `code-review` flows ship as proactive; an admin controls any flow's offer through the `guidance` block of its definition.
```

In the skills table, delete the `kcap-suggest-review-flow` row and change the `kcap-agent-flows` description cell to: `Drive any catalogue flow — named by you or offered from session context — following its driver guide`.

- [ ] **Step 4: Run the tests and confirm they pass.**

Run the Step 2 command, then the full Core unit suite, `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj`. Expected: all pass, including `PluginCommand*Tests`, which embed the instructions body.

- [ ] **Step 5: Commit.**

```bash
git add src/Capacitor.Cli.Core/Instructions/KcapAgentInstructions.cs src/Capacitor.Cli.Core/Resources/help-mcp.txt README.md test/Capacitor.Cli.Core.Tests.Unit/Instructions/AgentInstructionsWriterTests.cs
git commit -m "Point agents at catalogue-offered flows instead of review"
```

---

### Task 7: Verification

- [ ] **Step 1: Build the solution.** `dotnet build Capacitor.slnx`. Expected: 0 errors and 0 warnings (IDE0005 unused usings are build errors). If the build hangs past 10 minutes, run `dotnet build-server shutdown` and rebuild.
- [ ] **Step 2: Check AOT.** `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`. Expected: no output.
- [ ] **Step 3: Run the suites in the foreground, one at a time, with `--no-build`.**
  - `test/Capacitor.Cli.Core.Tests.Unit`
  - `test/Capacitor.Cli.Tests.Unit`
  - `test/Capacitor.Cli.Tests.Integration`

  Expected: all pass.
- [ ] **Step 4: Scan for Linear ids.** `bash scripts/check-linear-ids.sh`. Expected: clean.
- [ ] **Step 5: Read the README diff end to end.** Check that `suggest-review-flow` appears nowhere outside `docs/`: `rtk proxy grep -rn "suggest-review-flow" src test kcap README.md npm` should only match `RetiredSourceNames`, its tests, and `AgentFlowsSkillConformanceTests`.
