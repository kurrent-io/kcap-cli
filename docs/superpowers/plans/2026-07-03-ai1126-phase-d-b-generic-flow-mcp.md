# AI-1126 Phase D-b: Generic Flow MCP Tools — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose the flow engine's already-generic API through generic MCP tools (`start_flow` / `send_to_participant` / `get_flow_status` / `close_flow`) in `kcap mcp flows`, keep the four review tools as thin aliases with byte-identical schemas, and ship an `agent-flows` skill — per the approved Phase D spec (kcap-server `docs/superpowers/specs/2026-07-02-ai1126-flows-phase-d-design.md`, review flow `a556d844`).

**Architecture:** All tool changes live in `McpFlowsServer` (requester-side). Generic tools reuse the existing HTTP paths (`start_flow` → `POST /api/flows/review/start` with `kind = definition_id`; `send_to_participant` → `POST /api/flows/{id}/rounds` with the new optional `participant` passthrough — the server validates, the CLI holds no definition metadata). The reviewer-side `McpFlowResultServer` is a hard security boundary and is NOT touched.

**Tech Stack:** .NET AOT CLI, source-generated System.Text.Json (`McpJsonContext`), TUnit on MTP, WireMock integration tests driving the real built binary over stdio JSON-RPC.

## Global Constraints

- **Alias schema stability:** the four review tools keep their EXACT existing MCP input schemas — same property names, descriptions, and required lists; `start_review_flow` keeps `kind`; no alias gains `definition_id`, `participant`, or any new parameter. A schema-pinning test locks this.
- **Zero wire changes for existing callers:** `StartReviewFlowDto` unchanged; `SubmitReviewRoundDto` gains only the optional `[property: JsonPropertyName("participant")] string? Participant` (omitted when null — `McpJsonContext` uses `WhenWritingNull`); review-alias calls must serialize byte-identical request bodies to today.
- **`McpFlowResultServer` (reviewer side) is out of bounds** — generic flow-starting tools must never leak into it (AI-1139 recursion boundary).
- **Guardrail 400s surface verbatim:** the existing `Error: HTTP {code} — {body}` concatenation already does this; a test pins that a `max_rounds`/`budget_exceeded` ProblemDetails body reaches the tool result text.
- kcap-server HTTP JSON is snake_case — all DTO fields and tool arg names stay snake_case.
- AOT compatibility: no reflection-based serialization; DTOs already registered in `McpJsonContext` need no new `[JsonSerializable]` for added fields; any NEW DTO type must be registered in `McpReviewServer.cs`'s context.
- Tests: TUnit on MTP (`dotnet run --project …`, never `dotnet test`). Integration tests build/spawn the real binary — run `test/Capacitor.Cli.Tests.Integration` and `test/Capacitor.Cli.Tests.Unit`.
- Work in `/Users/alexey/dev/eventstore/kcap-cli-ai1126-db` on branch `alexeyzimarev/ai-1126-generic-flow-mcp`. Never edit the main kcap-cli checkout or the kcap-server submodule copy.

## File Structure

- Modify: `src/Capacitor.Cli/Commands/McpFlowsServer.cs` (tools, dispatch, DTO field)
- Create: `kcap/skills/agent-flows/SKILL.md`
- Modify: `kcap/.mcp.json` (kcap-flows description mentions generic tools)
- Modify: `kcap/skills/review-flows/SKILL.md` (one pointer line)
- Test: `test/Capacitor.Cli.Tests.Integration/McpFlowsServerTests.cs`

**Read `McpFlowsServer.cs` and `McpFlowsServerTests.cs` fully before coding** — reuse the existing arg-parse/dispatch/HTTP helper patterns and the WireMock harness verbatim.

---

### Task 1: Generic tools + review aliases in `McpFlowsServer`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/McpFlowsServer.cs`
- Test: `test/Capacitor.Cli.Tests.Integration/McpFlowsServerTests.cs`

**Interfaces:**
- Produces: MCP tools `start_flow`, `send_to_participant`, `get_flow_status`, `close_flow`; `SubmitReviewRoundDto.Participant` (`string?`, snake_case `participant`, null-omitted). Task 2's skill documents exactly these tool names/args.

- [ ] **Step 1: Failing tests.** Update/extend `McpFlowsServerTests.cs` (mirror its WireMock + stdio JSON-RPC harness):

```csharp
[Test]
public async Task Tools_list_returns_eight_flow_tools() {
    // was == 4 (line ~230): now assert count == 8 and names contain both the 4 review tools
    // and: start_flow, send_to_participant, get_flow_status, close_flow
}

[Test]
public async Task Review_tool_schemas_are_unchanged() {
    // Pin the alias schemas byte-stably: for each of the 4 review tools assert the exact
    // inputSchema property-name set and required list as they exist on main today
    // (start_review_flow: kind, target_kind, target_ref, target_title, context, instructions?,
    //  mode?, async? — copy the ACTUAL current sets from BuildToolsList before changing it).
    // No participant/definition_id anywhere in the four review schemas.
}

[Test]
public async Task Start_flow_posts_kind_from_definition_id() {
    // call start_flow with definition_id="my-custom-flow" + target/context args
    // → WireMock sees POST /api/flows/review/start with body kind=="my-custom-flow";
    //   response terminal → tool result contains the result text
}

[Test]
public async Task Send_to_participant_posts_participant_and_message_as_context() {
    // start (stub) then send_to_participant(flow_run_id, participant="reviewer", message="ctx2")
    // → POST /api/flows/{id}/rounds body has context=="ctx2" AND participant=="reviewer"
}

[Test]
public async Task Submit_review_round_body_has_no_participant_key() {
    // alias path: submit_review_round → POST body JSON does NOT contain "participant"
    // (null-omitted — byte-compat with old servers)
}

[Test]
public async Task Guardrail_400_body_surfaces_in_tool_error() {
    // stub POST /rounds → 400 with ProblemDetails {"detail":"max_rounds (2) reached for this run — close the flow."}
    // → send_to_participant tool result text contains "max_rounds (2)"
}

[Test]
public async Task Get_flow_status_and_close_flow_hit_same_endpoints_as_review_tools() {
    // get_flow_status → GET /api/flows/{id}; close_flow → POST /api/flows/{id}/close
}
```

- [ ] **Step 2: Run the integration suite filter to verify the new tests fail** (`dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj --treenode-filter "/*/*/McpFlowsServerTests/*"`). The pre-existing `Tools_list_returns_four_flow_tools` must be REPLACED by the new eight-count test (delete the old one in the same commit).

- [ ] **Step 3: Implement in `McpFlowsServer.cs`.**

1. `SubmitReviewRoundDto` gains the optional field (null-omitted by the existing `WhenWritingNull` context config):

```csharp
public sealed record SubmitReviewRoundDto(
    [property: JsonPropertyName("context")]      string  Context,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("async")]        bool    Async,
    [property: JsonPropertyName("participant")]  string? Participant = null
);
```
(Match the record's ACTUAL current shape — copy it and append the defaulted param.)

2. `BuildToolsList()` gains four generic `McpTool` entries (schemas mirror the review ones with these differences ONLY):
   - `start_flow`: same properties as `start_review_flow` but `definition_id` (required, description: "Flow definition id from the catalog (e.g. 'code-review', or a custom definition).") replaces `kind`.
   - `send_to_participant`: `flow_run_id` (required), `participant` (required, description: "The participant role to send to. Phase D flows have a single participant: 'reviewer'."), `message` (required — maps to the round context), `instructions` (optional), `async` (optional).
   - `get_flow_status`: `flow_run_id` (required) — identical schema to `get_review_flow_status`.
   - `close_flow`: `flow_run_id` (required) — identical schema to `close_review_flow`.
   The four review-tool entries are NOT modified in any way.

3. Dispatch (`HandleToolCallAsync`): generic names route to the SAME handlers —
   - `"start_flow"` → the existing start path with `kind = args["definition_id"]` (extract a shared private method if the current code inlines it; the review alias keeps reading `kind`).
   - `"send_to_participant"` → the existing submit path with `Context = args["message"]`, `Participant = args["participant"]`; `submit_review_round` calls the same path with `Participant = null` and `Context = args["context"]`.
   - `"get_flow_status"` / `"close_flow"` → the same switch arms as the review equivalents (add the names to the existing cases).
   Polling behavior (`PollUntilTerminalAsync`) is shared automatically by reusing the handlers.

4. Update the server's self-description strings (the MCP `serverInfo`/instructions text, if any mentions "review") to mention both tool families — check `RunAsync`'s initialize response.

- [ ] **Step 4: Run the McpFlowsServerTests filter → all green; then the FULL Integration + Unit suites** (the binary is shared across MCP server tests). Expected: all green, no schema drift elsewhere.

- [ ] **Step 5: Commit** — `git add src/Capacitor.Cli/Commands/McpFlowsServer.cs test/Capacitor.Cli.Tests.Integration/McpFlowsServerTests.cs && git commit -m "[AI-1126] D-b: generic flow MCP tools (start_flow/send_to_participant/get_flow_status/close_flow) + review aliases"`

---

### Task 2: `agent-flows` skill + manifest description

**Files:**
- Create: `kcap/skills/agent-flows/SKILL.md`
- Modify: `kcap/.mcp.json` (kcap-flows `description`)
- Modify: `kcap/skills/review-flows/SKILL.md` (pointer line)

**Interfaces:**
- Consumes: the Task 1 tool names/args exactly.

- [ ] **Step 1: Write `kcap/skills/agent-flows/SKILL.md`.** Mirror `review-flows/SKILL.md`'s structure (frontmatter with name + description trigger phrases; "if the tools are not loaded you are probably the hosted participant — do the work and end with the definition's result markers"; core rules; workflow; tool reference table; example). Content requirements:
   - Triggers: "start a flow", "run the X flow", "agent flow", "use flow definition X", plus generic-review phrasing.
   - The loop: `start_flow(definition_id, target_kind, target_ref, target_title, context)` → returns findings/clean per the definition's markers → address → `send_to_participant(flow_run_id, participant, message)` → repeat → `close_flow` only after the definition's clean marker.
   - Definition discovery: definition ids come from the catalog (`/admin/flows`); the built-ins are `spec-review` and `code-review`; the review tools are aliases of the generic ones.
   - **Guardrail errors section:** a `400` containing `max_rounds (N) reached` means address what you have and `close_flow` (the run stays open until you close it); a `budget_exceeded` error means the run is ALREADY failed and the participant stopped — report to the user, do not retry; a timed-out round surfaces as the round failing with `timeout`.
   - Single-participant note: in Phase D every definition has exactly one participant, `reviewer`; `send_to_participant` with anything else is rejected by the server naming the valid participant.
   - Same one-flow-per-task and no-nested-flows rules as review-flows.
- [ ] **Step 2:** `kcap/.mcp.json`: extend the `kcap-flows` entry's `description` to name the generic tools alongside the review ones. `review-flows/SKILL.md`: add one line under the intro: "These four tools are aliases of the generic flow tools (`start_flow`, `send_to_participant`, `get_flow_status`, `close_flow`) — see the `agent-flows` skill for non-review flows."
- [ ] **Step 3: Verify** the JSON is valid (`python3 -c "import json;json.load(open('kcap/.mcp.json'))"`) and the skill frontmatter matches the other skills' format (compare with `review-flows`).
- [ ] **Step 4: Commit** — `[AI-1126] D-b: agent-flows skill + manifest description for generic flow tools`

---

### Task 3: Full verification

- [ ] `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj` → green.
- [ ] `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj` → green.
- [ ] `git diff --stat origin/main` — only the five files above; `McpFlowResultServer.cs`, `McpReviewServer.cs` (except a new-DTO registration if one was truly needed — expected: none), launchers, and Models.cs untouched.

## Notes / carry-overs

- End-to-end `participant` validation needs a D-a-deployed server; an older server ignores the field (unknown JSON member) — graceful.
- README flows-tool docs (README.md ~124/301-321/489) mention the review tools; a docs touch-up can ride the PR if trivial, otherwise follow-up.
- `kcap/.codex-mcp.json` deliberately continues to omit kcap-flows (Codex drivers don't get flow-starting tools) — unchanged by design.
