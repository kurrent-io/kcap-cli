# Plan Ledger Recall — CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the read-only `kcap mcp sessions` server two tools that find and read declared plans, a `declared_plans` pointer on `get_session_summary`, and skill text that teaches an agent to assess and resume a plan.

**Architecture:** Both tools are thin relays over server routes, added to `McpSessionsServer` in its existing style: a hand-written schema entry, an `internal static` URL builder, the body relayed as text. `get_session_summary` becomes a two-call tool — the second call runs in parallel with `/recap`, is bounded, and fails open — routed out of the shared dispatch switch the way `search_sessions` already is.

**Tech Stack:** .NET 10 native AOT CLI, `System.Text.Json` DOM (`JsonNode`) with no reflection serialization, TUnit on Microsoft Testing Platform, WireMock.Net for the stdio integration tests.

**Spec:** `docs/superpowers/specs/2026-09-21-ai3036-plan-ledger-recall-design.md` in **kcap-server** — read D1, D6, D7 and D8. This plan covers the spec's **CLI PR**.

**This plan executes in `kurrent-io/kcap-cli`, not in kcap-server.** It was written against `kcap-cli` at commit `7dcc295c` ("Add background desktop notifications (#1055)"); line numbers below are from that commit. On the feature branch, copy this file to `docs/superpowers/plans/2026-09-21-ai3036-plan-ledger-recall-cli.md` in kcap-cli, where that repo keeps its plans.

## Global Constraints

- **No Linear issue ids in C# source or commit messages.** CI runs `scripts/check-linear-ids.sh`; a match for `AI-<digits>` anywhere under `src/**/*.cs` fails with no suppression available. Filenames and docs may carry one.
- **Commit subjects:** one imperative clause, 80 characters maximum, ending in the GitHub issue reference `(#N)` **only if one exists for this work**. None does yet — never invent one. Leave the reference off and tell the user a kcap-cli GitHub issue is needed before the PR, whose description must carry `Closes #N` and the Linear id. End every commit message with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- **AOT.** Never call a reflection-based `JsonSerializer` overload. Build JSON text from `JsonEncodedText`-encoded primitives or the `JsonNode` DOM. Never write a `JsonArray` collection expression (`[a, b]`) — it binds to the generic `Add<T>`; use `new JsonArray(a, b)`. `dotnet build` does **not** surface IL2026/IL3050; only `dotnet publish -c Release` does.
- **`TimeProvider` everywhere under `src/`.** `new CancellationTokenSource(TimeSpan)` is a banned symbol; write `new CancellationTokenSource(delay, time)`.
- **Comments are scarce.** Write one only if it names a trap or a deliberate decision. No ticket ids, no spec coordinates, no change narration.
- **`README.md` moves with the CLI surface**, in the same PR.
- **A red test must be an assertion failure, not a compile error.** Each task scaffolds a compiling stub before its tests.
- **TUnit filters:** `--treenode-filter "/*/*/<Class>/<Method>"`, never `--filter`.

## The wire contract this plan relies on

`GET /api/repositories/{hash}/plans?state=open|all&owner=&limit=` → 200:

```json
{ "items": [ {
    "plan_id": "…", "key_kind": "document|session",
    "documents": [ { "kind": "plan", "path": "docs/x.md", "workspace_root": "/r", "content_hash": "…", "commit_sha": null } ],
    "progress":  { "completed": 1, "total": 3, "total_known": true, "finished": false },
    "next_task": { "task_id": "…", "ordinal": 2, "title": "…", "status": "pending", "status_partial": false },
    "sessions":  [ { "session_id": "…", "status": "active|ended", "stale": false, "owned_by_caller": true, "last_touched_at": "…" } ],
    "work_item_id": null, "declared_at": "…", "last_touched_at": "…",
    "is_complete": true, "withheld_contributions": 0
} ] }
```

An unknown hash is 200 with no items, so **a 404 from this route can only mean a server that predates it.** A bad `state` is 400.

`GET /api/plans/{planId}` → one `PlanDetail`; `GET /api/sessions/{id}/plans` → a JSON array of `PlanDetail`, the 20 most recently touched. `PlanDetail` carries `plan_id`, `tasks`, `progress { completed, total, total_known, finished }`, `is_complete`, `withheld_contributions`, `is_current`. **A server that predates this change omits `finished`.**

`is_complete` means the view is whole — nothing withheld, no partial status — **not** that the work is done. Done-ness is:

```
finished = total_known && completed == total && is_complete
```

---

### Task 0: Workspace

**Files:** none.

- [ ] **Step 1: Create an isolated worktree of kcap-cli on a fresh branch**

Use the `superpowers:using-git-worktrees` skill against `/Users/alexey/dev/eventstore/kcap-cli`, branching from an up-to-date `origin/main`. Do not work in that clone's main checkout — other sessions share it. Branch name: `alexeyzimarev/ai-3036-plan-ledger-recall-repo-level-open-plans-and-session-plans` (the name Linear links a PR by).

- [ ] **Step 2: Confirm the baseline is green**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: all PASS. If red, stop — nothing below can be trusted.

- [ ] **Step 3: Confirm the source still matches this plan**

Run: `git log -1 --format=%H`
If it is not `7dcc295c29e7209978f3658e55362dafbf5372b2`, open `src/Capacitor.Cli/Commands/McpSessionsServer.cs` and confirm these still hold before continuing: `HandleToolCallAsync` dispatches through a `using var httpResponse = toolName switch { … }`; `search_sessions` is routed out before that switch; `ProjectRecapToSummary(string body)` builds its JSON with a `StringBuilder`. If any has changed, adapt the edits below to the new shape rather than forcing them.

---

### Task 1: `list_repo_plans` and `get_declared_plans`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/McpSessionsServer.cs`
- Modify: `src/Capacitor.Cli.Core/KcapMcpRegistry.cs:101-104`
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Integration/McpSessionsServerTests.cs`

**Interfaces:**
- Consumes: `ReadString(JsonObject? args, string key, string shapeMessage)`, `TryReadInt(JsonObject? args, string key, out int value)`, `RepoHashHelper.TryParseRepoRef(string, out string)`, `RepoShapeMessage`, `BuildToolResult(JsonNode id, string text, bool isError = false)` — all already in `McpSessionsServer`.
- Produces:
  - `internal static string BuildRepoPlansUrl(string baseUrl, JsonObject? args, string? cwdRepoHash)`
  - `internal static string BuildDeclaredPlansUrl(string baseUrl, JsonObject? args, out bool singlePlan)`
  - `internal const string RepoPlansUnsupportedMessage`
  - Tools `list_repo_plans` and `get_declared_plans` in `BuildToolsList()` and in `KcapMcpRegistry.ReviewFlowUnattendedSafeTools["kcap-sessions"]`.

`KcapMcpRegistryReviewFlowTests.Sessions_server_advertises_exactly_its_unattended_safe_tool_set` compares the advertised tools with the registry set using `SetEquals`, so adding a tool to one without the other fails immediately. Both tools are pure reads and belong in the set.

- [ ] **Step 1: Scaffold both URL builders as stubs**

In `McpSessionsServer.cs`, directly after `BuildRepoSessionsUrl`, add:

```csharp
    internal const string RepoPlansUnsupportedMessage =
        "This server does not list a repository's plans yet. Read one session's plans with get_declared_plans instead.";

    internal static string BuildRepoPlansUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) => "";

    internal static string BuildDeclaredPlansUrl(string baseUrl, JsonObject? args, out bool singlePlan) {
        singlePlan = false;
        return "";
    }
```

- [ ] **Step 2: Write the failing unit tests**

Append to `test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs`, inside the class:

```csharp
    [Test]
    public async Task BuildRepoPlansUrl_no_args_uses_cwd_hash_and_defaults_state_to_open() {
        var url = McpSessionsServer.BuildRepoPlansUrl("http://srv", args: null, cwdRepoHash: CwdHash);

        await Assert.That(url).IsEqualTo($"http://srv/api/repositories/{CwdHash}/plans?state=open");
    }

    [Test]
    public async Task BuildRepoPlansUrl_carries_state_owner_and_limit() {
        var args = new JsonObject { ["state"] = "all", ["owner"] = "github:1 2", ["limit"] = 5 };

        var url = McpSessionsServer.BuildRepoPlansUrl("http://srv", args, CwdHash);

        await Assert.That(url).IsEqualTo($"http://srv/api/repositories/{CwdHash}/plans?state=all&owner=github%3A1%202&limit=5");
    }

    [Test]
    public async Task BuildRepoPlansUrl_rejects_an_unknown_state() {
        var ex = await Assert.That(() => McpSessionsServer.BuildRepoPlansUrl("http://srv", new JsonObject { ["state"] = "done" }, CwdHash))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("open or all");
    }

    [Test]
    public async Task BuildRepoPlansUrl_no_repo_and_no_cwd_hash_fails_closed_without_offering_all() {
        var ex = await Assert.That(() => McpSessionsServer.BuildRepoPlansUrl("http://srv", args: null, cwdRepoHash: null))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("<owner>/<name>");
        await Assert.That(ex.Message).DoesNotContain("\"all\"");
    }

    [Test]
    public async Task BuildDeclaredPlansUrl_by_plan_id_reads_one_plan() {
        var url = McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["plan_id"] = "p 1" }, out var single);

        await Assert.That(url).IsEqualTo("http://srv/api/plans/p%201");
        await Assert.That(single).IsTrue();
    }

    [Test]
    public async Task BuildDeclaredPlansUrl_by_session_id_reads_the_sessions_plans() {
        var url = McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["session_id"] = "s1" }, out var single);

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/s1/plans");
        await Assert.That(single).IsFalse();
    }

    [Test]
    public async Task BuildDeclaredPlansUrl_needs_exactly_one_of_plan_id_and_session_id() {
        var neither = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject(), out _))
            .Throws<ArgumentException>();
        var both = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["plan_id"] = "p1", ["session_id"] = "s1" }, out _))
            .Throws<ArgumentException>();

        await Assert.That(neither!.Message).Contains("exactly one");
        await Assert.That(both!.Message).Contains("exactly one");
    }

    /// <summary>"current" names a session's pointer and needs a session the route would not get;
    /// a dot segment would walk the URL path.</summary>
    [Test]
    [Arguments("current")]
    [Arguments(".")]
    [Arguments("..")]
    public async Task BuildDeclaredPlansUrl_rejects_a_plan_id_that_is_not_an_id(string planId) {
        var ex = await Assert.That(() => McpSessionsServer.BuildDeclaredPlansUrl("http://srv", new JsonObject { ["plan_id"] = planId }, out _))
            .Throws<ArgumentException>();

        await Assert.That(ex!.Message).Contains("session_id");
    }

    [Test]
    public async Task Tools_list_exposes_the_two_plan_tools_with_no_required_arguments() {
        var byName = McpSessionsServer.BuildToolsList().ToDictionary(t => t.Name);

        await Assert.That(byName["list_repo_plans"].InputSchema.Required.Length).IsEqualTo(0);
        await Assert.That(byName["get_declared_plans"].InputSchema.Required.Length).IsEqualTo(0);
        await Assert.That(byName["get_declared_plans"].Description).Contains("is_complete");
        await Assert.That(byName["list_repo_plans"].Description).Contains("finished");
    }
```

- [ ] **Step 3: Run the unit tests and watch them fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/Build*Plans*"`
Expected: every `BuildRepoPlansUrl_*` and `BuildDeclaredPlansUrl_*` test FAILS on its assertion (the stubs return `""` and throw nothing).

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/Tools_list_exposes_the_two_plan_tools_with_no_required_arguments"`
Expected: FAIL with `KeyNotFoundException` — the tools are not listed yet.

- [ ] **Step 4: Implement the URL builders**

Replace the two stubs with:

```csharp
    internal static string BuildRepoPlansUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var explicitRepo = ReadString(args, "repo", RepoShapeMessage);
        if (string.IsNullOrWhiteSpace(explicitRepo)) explicitRepo = null;

        string repoHash;

        if (explicitRepo is null) {
            repoHash = cwdRepoHash ?? throw new ArgumentException(
                "Cannot resolve the current repository owner/name from git metadata (e.g. a missing or " +
                "unparseable 'origin' remote). Pass repo: \"<owner>/<name>\" or a 16-hex repo hash.");
        } else if (!RepoHashHelper.TryParseRepoRef(explicitRepo, out repoHash)) {
            throw new ArgumentException(RepoShapeMessage);
        }

        var state = ReadString(args, "state", "`state` must be a string: open or all.") ?? "open";

        if (state is not ("open" or "all"))
            throw new ArgumentException("`state` must be open or all.");

        var qs = new List<string> { $"state={state}" };

        if (ReadString(args, "owner", "`owner` must be a string.") is { Length: > 0 } owner)
            qs.Add($"owner={Uri.EscapeDataString(owner)}");

        if (TryReadInt(args, "limit", out var limit)) qs.Add($"limit={limit}");

        return $"{baseUrl}/api/repositories/{repoHash}/plans?" + string.Join("&", qs);
    }

    internal static string BuildDeclaredPlansUrl(string baseUrl, JsonObject? args, out bool singlePlan) {
        const string oneOf = "Pass exactly one of plan_id and session_id.";

        var planId    = ReadString(args, "plan_id",    "`plan_id` must be a string.");
        var sessionId = ReadString(args, "session_id", "`session_id` must be a string.");

        if (string.IsNullOrWhiteSpace(planId))    planId    = null;
        if (string.IsNullOrWhiteSpace(sessionId)) sessionId = null;

        if ((planId is null) == (sessionId is null)) throw new ArgumentException(oneOf);

        singlePlan = planId is not null;

        if (planId is null) return $"{baseUrl}/api/sessions/{Uri.EscapeDataString(sessionId!)}/plans";

        if (planId is "current" or "." or "..")
            throw new ArgumentException("`plan_id` must be a plan id. To read the plans of a session, pass session_id instead.");

        return $"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}";
    }
```

- [ ] **Step 5: Dispatch the two tools**

In `HandleToolCallAsync`, declare the flag above the `try` and add two arms to the switch:

```csharp
        var singlePlan = false;

        try {
            using var httpResponse = toolName switch {
                "get_session_summary"    => await client.GetAsync(BuildSummaryUrl(baseUrl, arguments)),
                "get_session_transcript" => await client.GetAsync(BuildTranscriptUrl(baseUrl, arguments)),
                "get_turn"               => await client.GetAsync(BuildTurnDetailUrl(baseUrl, arguments)),
                "list_turns"             => await client.GetAsync(BuildTurnsUrl(baseUrl, arguments)),
                "list_repo_sessions"     => await client.GetAsync(BuildRepoSessionsUrl(baseUrl, arguments, cwdRepoHash)),
                "list_repo_plans"        => await client.GetAsync(BuildRepoPlansUrl(baseUrl, arguments, cwdRepoHash)),
                "get_declared_plans"     => await client.GetAsync(BuildDeclaredPlansUrl(baseUrl, arguments, out singlePlan)),
                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };
```

Directly after the `Unauthorized` check and before the generic `!IsSuccessStatusCode` check, add:

```csharp
            // That route answers 200 for a repository it has never seen, so a 404 is a server without it.
            if (toolName == "list_repo_plans" && httpResponse.StatusCode == HttpStatusCode.NotFound) {
                return BuildToolResult(id, RepoPlansUnsupportedMessage, isError: true);
            }
```

Replace the `var payload = …` line with:

```csharp
            var payload = toolName switch {
                "get_session_summary"                => ProjectRecapToSummary(body),
                "get_declared_plans" when singlePlan => $"[{body}]",
                _                                    => body
            };
```

The `[…]` wrap is what makes `get_declared_plans` answer with an array whichever argument it was given; the body is already JSON, so concatenation needs no serializer.

- [ ] **Step 6: List the tools and classify them**

In `BuildToolsList()`, after the `list_repo_sessions` entry, add:

```csharp
        new(
            "list_repo_plans",
            "List the declared plans on a repository that you are allowed to see, most recently touched first. Reach for this to find unfinished work: a plan a session left behind when it ended. Each row carries plan_id, documents (kind, path, content_hash, commit_sha — no bodies), progress {completed, total, total_known, finished}, next_task (the first task neither completed nor skipped, or null), sessions, work_item_id, last_touched_at, is_complete and withheld_contributions; read the full task list with get_declared_plans(plan_id). progress.finished is whether the work is done. is_complete is NOT that: it only says nothing was withheld from your view, and a half-done plan usually has is_complete true. A non-zero withheld_contributions means other people's tasks exist that you cannot see, so never call such a plan complete. On sessions, status and stale describe the session as a whole — stale means no activity for over an hour — and only last_touched_at is about this plan: a session stays attached after moving to other work, so an active session is not proof anyone is executing the plan.",
            new(
                "object",
                new() {
                    ["repo"]  = new("string",  "Optional: \"<owner>/<name>\" or a 16-hex repo hash. Defaults to the current repo (resolved from cwd at server startup). \"all\" is not accepted; the tool is repo-scoped."),
                    ["state"] = new("string",  "Optional: open (default — at least one task you can see is neither completed nor skipped) or all, which also returns finished plans and plans that declared documents but no tasks."),
                    ["owner"] = new("string",  "Optional: \"me\" or a canonical user id, matched against the owners of the plan's sessions. Absent means everyone visible."),
                    ["limit"] = new("integer", "Default 10, max 20.")
                },
                []
            )
        ),
        new(
            "get_declared_plans",
            "Read declared plans in full: documents, the ordered tasks with status, note and source, the contributing sessions, and progress. Pass exactly one of plan_id (one plan — the drill-down from list_repo_plans or from get_session_summary's declared_plans) or session_id (the plans that session and its continuation chain touched, the 20 most recently touched; use plan_id for one in particular). The result is always a JSON array. progress.finished is whether the work is done; is_complete only says nothing was withheld from your view. If progress has no finished field the server predates it: treat the plan as finished only when total_known is true AND completed equals total AND is_complete is true — never on completed equals total alone, since a plan with no declared task list also reads 0 of 0. A task with status_partial true had its status set by a session you cannot see; the status is real, its note is withheld.",
            new(
                "object",
                new() {
                    ["plan_id"]    = new("string", "A plan id, from list_repo_plans or declared_plans. Not \"current\"."),
                    ["session_id"] = new("string", "A session id, to read every plan its chain touched.")
                },
                []
            )
        ),
```

In `src/Capacitor.Cli.Core/KcapMcpRegistry.cs`, extend the `kcap-sessions` set:

```csharp
            ["kcap-sessions"] = new HashSet<string>(StringComparer.Ordinal) {
                "search_sessions", "list_repo_sessions", "get_session_summary", "get_session_transcript",
                "get_turn", "list_turns", "list_repo_plans", "get_declared_plans",
            },
```

- [ ] **Step 7: Run the unit tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: all PASS.

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/KcapMcpRegistryReviewFlowTests/*"`
Expected: all PASS. Remove one of the two names from the registry set and run again: `Sessions_server_advertises_exactly_its_unattended_safe_tool_set` must FAIL. Restore it.

- [ ] **Step 8: Write the integration tests**

In `test/Capacitor.Cli.Tests.Integration/McpSessionsServerTests.cs`, rename `Tools_list_returns_six_tools_with_correct_names` to `Tools_list_returns_eight_tools_with_correct_names`, change `IsEqualTo(6)` to `IsEqualTo(8)`, and add after the `list_repo_sessions` line:

```csharp
            await Assert.That(names.Contains("list_repo_plans")).IsTrue();
            await Assert.That(names.Contains("get_declared_plans")).IsTrue();
```

Add these tests to the class:

```csharp
    [Test]
    public async Task List_repo_plans_hits_the_repo_route_for_the_cwd_repo() {
        using var repo = CwdRepo("acme", "widgets");
        var       hash = RepoHashHelper.ComputeRepoHash("acme", "widgets");

        _server.Given(Request.Create().WithPath($"/api/repositories/{hash}/plans").UsingGet().WithParam("state", "open"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"items":[{"plan_id":"p-1","key_kind":"session","documents":[],"progress":{"completed":1,"total":3,"total_known":true,"finished":false},"next_task":{"task_id":"t-2","ordinal":2,"title":"Two","status":"pending","status_partial":false},"sessions":[],"work_item_id":null,"declared_at":"2026-09-02T09:00:00+00:00","last_touched_at":"2026-09-02T10:00:00+00:00","is_complete":true,"withheld_contributions":0}]}"""));

        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            await SendRequest(proc, InitializeRequest(1));

            var response = await SendRequest(proc, ToolsCallRequest(2, "list_repo_plans", new JsonObject()));
            var text     = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();

            await Assert.That(response["result"]?["isError"]).IsNull();
            await Assert.That(text).Contains("\"plan_id\":\"p-1\"");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    /// <summary>Nothing is stubbed, so the route answers 404 the way an older server does.</summary>
    [Test]
    public async Task List_repo_plans_against_a_server_without_the_route_says_so() {
        using var repo = CwdRepo("acme", "widgets");
        using var proc = SpawnMcpServer(workingDirectory: repo.Path);
        try {
            await SendRequest(proc, InitializeRequest(1));

            var response = await SendRequest(proc, ToolsCallRequest(2, "list_repo_plans", new JsonObject()));
            var text     = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();

            await Assert.That(response["result"]?["isError"]?.GetValue<bool>()).IsTrue();
            await Assert.That(text).Contains("does not list a repository's plans yet");
            await Assert.That(text).DoesNotContain("HTTP 404");
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_declared_plans_answers_with_an_array_for_either_argument() {
        const string plan = """{"plan_id":"p-1","tasks":[],"progress":{"completed":0,"total":0,"total_known":false,"finished":false},"is_complete":true,"withheld_contributions":0,"is_current":false}""";

        _server.Given(Request.Create().WithPath("/api/plans/p-1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(plan));
        _server.Given(Request.Create().WithPath("/api/sessions/abc/plans").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($"[{plan}]"));

        using var proc = SpawnMcpServer();
        try {
            await SendRequest(proc, InitializeRequest(1));

            foreach (var (requestId, args) in new[] { (2, new JsonObject { ["plan_id"] = "p-1" }), (3, new JsonObject { ["session_id"] = "abc" }) }) {
                var response = await SendRequest(proc, ToolsCallRequest(requestId, "get_declared_plans", args));
                var text     = response["result"]?["content"]?[0]?["text"]?.GetValue<string>();
                var plans    = JsonNode.Parse(text!)?.AsArray();

                await Assert.That(response["result"]?["isError"]).IsNull();
                await Assert.That(plans!.Count).IsEqualTo(1);
                await Assert.That(plans[0]?["plan_id"]?.GetValue<string>()).IsEqualTo("p-1");
            }
        } finally {
            await ShutdownAsync(proc);
        }
    }
```

- [ ] **Step 9: Run the integration tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: all PASS.

- [ ] **Step 10: Commit**

```bash
git add src/Capacitor.Cli/Commands/McpSessionsServer.cs src/Capacitor.Cli.Core/KcapMcpRegistry.cs test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs test/Capacitor.Cli.Tests.Integration/McpSessionsServerTests.cs
git commit -m "Find and read declared plans from the sessions MCP server" -m "The plans server prompts on every call and is shut out of unattended review flows, so recall could not reach a plan a dead session left behind." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: `declared_plans` on `get_session_summary`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/McpSessionsServer.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Integration/McpSessionsServerTests.cs`

**Interfaces:**
- Consumes: `BuildSummaryUrl(string baseUrl, JsonObject? args)`, `AppendJsonString(StringBuilder, string)`, `AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time)`, the injected `time` (`TimeProvider`).
- Produces:
  - `internal static string ProjectRecapToSummary(string body, string? plansBody = null)` — the existing one-argument call sites keep compiling.
  - `internal static string? ProjectDeclaredPlans(string? plansBody)` — the `declared_plans` array as JSON text, or null when there is nothing to show.
  - `get_session_summary` output gains `declared_plans: [{ plan_id, completed, total, total_known, finished, is_complete, is_current }]`, omitted when the session has no plans or the lookup failed.

The field is named `declared_plans` because the payload already has `plan`, the captured plan *text*, and the two must not read as one thing. The lookup **fails open**: the stdio loop is serial, so it is bounded, and a summary that worked must never be lost to it.

- [ ] **Step 1: Scaffold the projection**

In `McpSessionsServer.cs`, change the `ProjectRecapToSummary` signature to `internal static string ProjectRecapToSummary(string body, string? plansBody = null)` (leave its body alone for now) and add below it:

```csharp
    internal static string? ProjectDeclaredPlans(string? plansBody) => null;
```

- [ ] **Step 2: Write the failing unit tests**

Append to `test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs`:

```csharp
    const string Recap = """[{"type":"whats_done","content":"did X"}]""";

    static JsonArray? DeclaredPlans(string? plansBody) =>
        JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary(Recap, plansBody))!["declared_plans"]?.AsArray();

    [Test]
    public async Task ProjectRecapToSummary_carries_a_pointer_for_each_declared_plan() {
        const string plans = """
            [
              {"plan_id":"p-1","progress":{"completed":2,"total":7,"total_known":true,"finished":false},"is_complete":true,"is_current":true,"tasks":[{"title":"ignored"}]},
              {"plan_id":"p-2","progress":{"completed":3,"total":3,"total_known":true,"finished":true},"is_complete":true,"is_current":false}
            ]
            """;

        var pointers = DeclaredPlans(plans)!;

        await Assert.That(pointers.Count).IsEqualTo(2);
        await Assert.That(pointers[0]!.ToJsonString())
            .IsEqualTo("""{"plan_id":"p-1","completed":2,"total":7,"total_known":true,"finished":false,"is_complete":true,"is_current":true}""");
        await Assert.That(pointers[1]!["finished"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    [Arguments(null)]
    [Arguments("[]")]
    [Arguments("not json")]
    [Arguments("""{"error":"nope"}""")]
    public async Task ProjectRecapToSummary_omits_declared_plans_when_there_is_nothing_to_show(string? plansBody) {
        var projected = JsonNode.Parse(McpSessionsServer.ProjectRecapToSummary(Recap, plansBody))!.AsObject();

        await Assert.That(projected.ContainsKey("declared_plans")).IsFalse();
        await Assert.That(projected["summary_text"]!.GetValue<string>()).IsEqualTo("did X");
    }

    /// <summary>A server that predates the field omits it. Both zero-task shapes read 0 of 0 and
    /// differ only in total_known, so completed == total alone would call a plan with no task list
    /// finished.</summary>
    [Test]
    [Arguments("""{"completed":0,"total":0,"total_known":false}""", true,  false)]
    [Arguments("""{"completed":0,"total":0,"total_known":true}""",  true,  true)]
    [Arguments("""{"completed":3,"total":3,"total_known":true}""",  false, false)]
    [Arguments("""{"completed":3,"total":3,"total_known":true}""",  true,  true)]
    [Arguments("""{"completed":2,"total":3,"total_known":true}""",  true,  false)]
    public async Task ProjectDeclaredPlans_derives_finished_when_the_server_did_not_send_it(string progress, bool isComplete, bool expected) {
        var plans = $$"""[{"plan_id":"p-1","progress":{{progress}},"is_complete":{{(isComplete ? "true" : "false")}},"is_current":false}]""";

        await Assert.That(DeclaredPlans(plans)![0]!["finished"]!.GetValue<bool>()).IsEqualTo(expected);
    }

    [Test]
    public async Task ProjectDeclaredPlans_trusts_a_finished_the_server_sent() {
        const string plans = """[{"plan_id":"p-1","progress":{"completed":3,"total":3,"total_known":true,"finished":false},"is_complete":true,"is_current":false}]""";

        await Assert.That(DeclaredPlans(plans)![0]!["finished"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task ProjectDeclaredPlans_skips_an_entry_with_no_plan_id() {
        const string plans = """[{"progress":{"completed":0,"total":1,"total_known":true}},{"plan_id":"p-2","progress":{"completed":0,"total":1,"total_known":true},"is_complete":true,"is_current":false}]""";

        var pointers = DeclaredPlans(plans)!;

        await Assert.That(pointers.Count).IsEqualTo(1);
        await Assert.That(pointers[0]!["plan_id"]!.GetValue<string>()).IsEqualTo("p-2");
    }
```

- [ ] **Step 3: Run the unit tests and watch them fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/Project*"`
Expected: `ProjectRecapToSummary_carries_a_pointer_…`, every `ProjectDeclaredPlans_*` test FAIL with `NullReferenceException` on the missing `declared_plans`. `ProjectRecapToSummary_omits_declared_plans_…` and the existing projection tests PASS.

- [ ] **Step 4: Implement the projection**

Replace the `ProjectDeclaredPlans` stub with:

```csharp
    /// <summary>The declared-plans pointer as JSON text, or null when there is nothing to show.
    /// A server that does not send <c>finished</c> gets it derived; total_known is part of that,
    /// because a plan with no declared task list also reads 0 of 0.</summary>
    internal static string? ProjectDeclaredPlans(string? plansBody) {
        if (plansBody is null) return null;

        try {
            if (JsonNode.Parse(plansBody) is not JsonArray plans) return null;

            var sb    = new StringBuilder("[");
            var count = 0;

            foreach (var plan in plans) {
                if (plan?["plan_id"] is not JsonValue idValue || !idValue.TryGetValue(out string? planId) || planId is null) continue;

                var progress   = plan["progress"];
                var completed  = IntOrZero(progress?["completed"]);
                var total      = IntOrZero(progress?["total"]);
                var totalKnown = IsTrue(progress?["total_known"]);
                var isComplete = IsTrue(plan["is_complete"]);
                var finished   = progress?["finished"] is JsonValue sent && sent.TryGetValue(out bool fromServer)
                    ? fromServer
                    : totalKnown && completed == total && isComplete;

                if (count++ > 0) sb.Append(',');

                sb.Append("{\"plan_id\":");
                AppendJsonString(sb, planId);
                sb.Append(",\"completed\":").Append(completed.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"total\":").Append(total.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"total_known\":").Append(totalKnown ? "true" : "false");
                sb.Append(",\"finished\":").Append(finished ? "true" : "false");
                sb.Append(",\"is_complete\":").Append(isComplete ? "true" : "false");
                sb.Append(",\"is_current\":").Append(IsTrue(plan["is_current"]) ? "true" : "false");
                sb.Append('}');
            }

            return count == 0 ? null : sb.Append(']').ToString();
        } catch {
            return null;
        }

        static int  IntOrZero(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int i) ? i : 0;
        static bool IsTrue(JsonNode? node)    => node is JsonValue v && v.TryGetValue(out bool b) && b;
    }
```

Add `using System.Globalization;` at the top of the file if it is not already there.

In `ProjectRecapToSummary`, replace the closing `sb.Append('}');` with:

```csharp
        if (ProjectDeclaredPlans(plansBody) is { } declaredPlans) {
            sb.Append(",\"declared_plans\":");
            sb.Append(declaredPlans);
        }

        sb.Append('}');
```

and extend its summary comment's first line to read `/// Projects a /recap response (RecapEntry[]) into { summary_text, plan, declared_plans? } for agent consumption.`

- [ ] **Step 5: Run the unit tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/Project*"`
Expected: all PASS.

Mutation check: change the derived expression to `completed == total && isComplete` (dropping `totalKnown`) and run again — the first `[Arguments]` row of `ProjectDeclaredPlans_derives_finished_…` must FAIL. Restore it.

- [ ] **Step 6: Write the failing integration tests**

In `test/Capacitor.Cli.Tests.Integration/McpSessionsServerTests.cs`, in the existing `Get_session_summary_projects_recap_to_summary_text_and_plan`, add after the `plan` assertion — that test stubs only `/recap`, so the plans lookup gets a 404 and this pins the fail-open path:

```csharp
            await Assert.That(projected.ContainsKey("declared_plans")).IsFalse();
```

Add:

```csharp
    [Test]
    public async Task Get_session_summary_carries_declared_plans_when_the_session_has_any() {
        _server.Given(Request.Create().WithPath("/api/sessions/abc/recap").WithParam("chain", "false").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""[{"type":"whats_done","content":"did X"}]"""));
        _server.Given(Request.Create().WithPath("/api/sessions/abc/plans").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """[{"plan_id":"p-1","progress":{"completed":2,"total":7,"total_known":true,"finished":false},"is_complete":true,"is_current":true}]"""));

        using var proc = SpawnMcpServer();
        try {
            var response  = await SendRequest(proc, ToolsCallRequest(4, "get_session_summary", new JsonObject { ["session_id"] = "abc" }));
            var projected = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

            await Assert.That(projected["summary_text"]!.GetValue<string>()).IsEqualTo("did X");
            await Assert.That(projected["declared_plans"]![0]!["plan_id"]!.GetValue<string>()).IsEqualTo("p-1");
            await Assert.That(projected["declared_plans"]![0]!["completed"]!.GetValue<int>()).IsEqualTo(2);
            await Assert.That(projected["declared_plans"]![0]!["finished"]!.GetValue<bool>()).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }

    [Test]
    public async Task Get_session_summary_survives_a_failing_plans_lookup() {
        _server.Given(Request.Create().WithPath("/api/sessions/abc/recap").WithParam("chain", "false").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""[{"type":"whats_done","content":"did X"}]"""));
        _server.Given(Request.Create().WithPath("/api/sessions/abc/plans").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));

        using var proc = SpawnMcpServer();
        try {
            var response  = await SendRequest(proc, ToolsCallRequest(4, "get_session_summary", new JsonObject { ["session_id"] = "abc" }));
            var projected = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>())!.AsObject();

            await Assert.That(response["result"]?["isError"]).IsNull();
            await Assert.That(projected["summary_text"]!.GetValue<string>()).IsEqualTo("did X");
            await Assert.That(projected.ContainsKey("declared_plans")).IsFalse();
        } finally {
            await ShutdownAsync(proc);
        }
    }
```

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/Get_session_summary*"`
Expected: `…carries_declared_plans_when_the_session_has_any` FAILS — the tool never calls the plans route. The other two PASS.

- [ ] **Step 7: Make `get_session_summary` a two-call tool**

In `HandleToolCallAsync`, route it out beside `search_sessions`:

```csharp
        if (toolName == "get_session_summary") {
            return await HandleSessionSummaryAsync(id, arguments, client, baseUrl);
        }
```

Delete the `"get_session_summary"` arm from the `toolName switch` and the `"get_session_summary" => ProjectRecapToSummary(body),` arm from the payload switch.

Add after `HandleSearchSessionsAsync`:

```csharp
    /// <summary>The recap and the session's declared plans, fetched together. The plans lookup is
    /// best-effort: its failure or timeout returns the summary without them.</summary>
    async Task<string> HandleSessionSummaryAsync(JsonNode id, JsonObject? arguments, HttpClient client, string baseUrl) {
        try {
            var recapUrl  = BuildSummaryUrl(baseUrl, arguments);
            var sessionId = arguments!["session_id"]!.GetValue<string>();

            // The stdio loop is serial, so a stalled lookup would block every later request.
            using var plansCts  = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            var       plansTask = FetchDeclaredPlansAsync(client, $"{baseUrl}/api/sessions/{Uri.EscapeDataString(sessionId)}/plans", plansCts.Token);

            using var recap = await client.GetAsync(recapUrl);
            var       body  = await recap.Content.ReadAsStringAsync();
            var       plans = await plansTask;

            if (recap.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);
            }

            if (!recap.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)recap.StatusCode} — {body}", isError: true);
            }

            return BuildToolResult(id, ProjectRecapToSummary(body, plans));
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static async Task<string?> FetchDeclaredPlansAsync(HttpClient client, string url, CancellationToken ct) {
        try {
            using var response = await client.GetAsync(url, ct);

            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"kcap mcp sessions: declared plans lookup failed ({ex.GetType().Name}: {ex.Message}); returning the summary without them.");

            return null;
        }
    }
```

`plansTask` is awaited before every return, so the lookup never outlives the request or the disposed token source, and it cannot throw: `FetchDeclaredPlansAsync` catches everything. `BuildSummaryUrl` has already thrown `ArgumentException` for a missing `session_id`, so the `!` dereferences after it are safe.

Update the `get_session_summary` description in `BuildToolsList()`:

```csharp
            "Get a concise summary of a past session: the 'what was done' narrative (summary_text), the plan text the session captured (plan, if any), and declared_plans — one {plan_id, completed, total, total_known, finished, is_complete, is_current} per plan the session declared tasks or documents for, absent when it declared none. finished is whether that plan's work is done; is_complete only says nothing was withheld from your view. Read a plan's tasks with get_declared_plans(plan_id). Use this to orient yourself before drilling into the full transcript.",
```

- [ ] **Step 8: Run both suites**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: all PASS.

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- --treenode-filter "/*/*/McpSessionsServerTests/*"`
Expected: all PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.Cli/Commands/McpSessionsServer.cs test/Capacitor.Cli.Tests.Unit/Commands/McpSessionsServerTests.cs test/Capacitor.Cli.Tests.Integration/McpSessionsServerTests.cs
git commit -m "Point a session summary at the plans the session declared" -m "An agent recapping a dead session had no way to learn a task ledger existed. The lookup is bounded and fails open, since the stdio loop is serial." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Skills, README and the plugin version

**Files:**
- Modify: `kcap/skills/recap/SKILL.md`
- Modify: `kcap/skills/plans/SKILL.md`
- Modify: `README.md:551-560`
- Modify: `kcap/.claude-plugin/plugin.json`

**Interfaces:**
- Consumes: the tool names and fields from Tasks 1 and 2.
- Produces: nothing code depends on.

The tools are half the feature: an agent that does not know the flow exists will never call them. The resume procedure below encodes four traps found while designing it — each one silently loses or splits data if skipped — so reproduce it exactly.

- [ ] **Step 1: Teach `recap` to find and judge plans**

In `kcap/skills/recap/SKILL.md`:

In the frontmatter `description`, after `"search past sessions for…".`, insert:

```
  Also covers declared plans: "continue the plan", "resume the plan", "what's
  left on the plan", "is the plan finished", "unfinished plans", "what was the
  last session working through".
```

Replace the tool enumeration on line 18 so it reads:

```markdown
> **For agents:** When the `kcap-sessions` MCP server is available, prefer its tools (`search_sessions`, `list_repo_sessions`, `get_session_summary`, `list_turns`, `get_turn`, `get_session_transcript`, `list_repo_plans`, `get_declared_plans`) for retrieving past sessions and the plans they declared. This CLI-wrapped skill remains a fallback for shell use and when MCP isn't installed.
```

Insert a new section directly before `## Full Output (`--full`)`:

```markdown
## Plans

A session that works from a plan declares it to Capacitor: the documents, an ordered task list, and each task's status. That record outlives the session, so it is how you tell where earlier work stopped. It is reachable only through the `kcap-sessions` MCP tools — `kcap recap` does not print it, and the `## Plan` block above is the plan *text* a session captured, not its task list.

**Finding one.**
- `list_repo_plans` — the unfinished plans in this repository, most recently touched first. Start here when you have no session id: "what was left unfinished", "is there a plan to continue".
- `get_session_summary` — carries `declared_plans`, one pointer per plan that session declared, when it declared any.
- `get_declared_plans` — the full task list, by `plan_id` (from either of the above) or by `session_id`.

**Judging whether a plan is done.** Read `progress` and two flags, in this order:

1. `progress.finished` is `true` — the plan is done.
2. `progress.completed` is less than `progress.total` — the plan is open. What remains is every entry of `tasks` whose status is neither `completed` nor `skipped`; a `list_repo_plans` row has no `tasks`, and gives the first of them as `next_task`.
3. `is_complete` is `false` — everything you can see is done, but your view is partial: someone else's session contributed tasks or a status you cannot see. Say so. Do not call the plan complete.
4. Otherwise `progress.total_known` is `false` — the session declared documents but never a task list, so completion is unknown. That is not withheld data; do not report it as a partial view.

**`is_complete` does not mean the work is done.** It means nothing was withheld from your view, and a half-finished plan usually has `is_complete: true`. `finished` is the field that answers "is it done". If `progress` has no `finished` field, the server predates it: the plan is finished only when `total_known` is true **and** `completed` equals `total` **and** `is_complete` is true.

*Worked example — starting from a session.* `get_declared_plans(session_id: "4f2a…")` returns one plan with `progress: {completed: 2, total: 7, total_known: true, finished: false}` and `is_complete: true`. Branch 2: open, five tasks remain — list them from `tasks`.

*Worked example — starting from a summary.* `get_session_summary` shows `declared_plans: [{plan_id: "9c1e…", completed: 7, total: 7, total_known: true, finished: false, is_complete: false, is_current: true}]`. Not branch 1, not branch 2 — branch 3: all seven visible tasks are done, but the view is partial. Report that; do not report the plan as finished.

To pick a plan up and continue it, follow "Resuming a plan" in the `plans` skill.
```

- [ ] **Step 2: Teach `plans` to resume, and correct what it says about re-declaring**

In `kcap/skills/plans/SKILL.md`:

Rule 1 currently says declaring the same file again from a later session lands on the same plan. That holds only from the same checkout. Replace the sentence `The file is read locally, hashed, and keyed against the repository root; declaring the same file again from a later session lands on the same plan.` with:

```markdown
   The file is read locally, hashed, and keyed against the repository root
   **and the checkout it sits in**: declaring the same file again from the same
   checkout lands on the same plan, but from a different worktree or clone it
   starts a separate one. To continue a plan another session began, see
   "Resuming a plan" below — do not re-declare its document.
```

Insert a new section directly before `## Tool reference`:

```markdown
## Resuming a plan

To continue a plan that an earlier session left unfinished — yours or a teammate's. Finding and reading use the `kcap-sessions` tools; only the last step writes.

1. **Find it.** `list_repo_plans` lists this repository's open plans. Read each row's `sessions` together: a session that is `active`, not `stale`, and whose `last_touched_at` is recent may still be executing the plan — **ask the user before adopting it**. An active session whose `last_touched_at` is old has most likely moved to other work; a session stays attached to every plan it ever touched, so that alone is no reason to hold back.
2. **Read it.** `get_declared_plans(plan_id: …)` for the documents and the full task list.
3. **Compare the document.** Read the plan file from *this* checkout at its repo-relative `path` and compare it with `content_hash`. If it changed, the task list may be out of date. Reconciling it means sending a new snapshot with `set_plan_tasks(plan_id: …)`, and a snapshot **replaces** the list — a task it omits is deleted, a note it omits is cleared:
   - **`is_complete` is `true`:** reconcile now, **before** step 5, carrying every existing task's `task_id`, `status` and `note` over from step 2. Sent after step 5, the snapshot would carry the resumed task's earlier `pending` status and put it straight back.
   - **`is_complete` is `false`: never send a snapshot.** Your view is missing someone else's tasks or notes, and a list rebuilt from it would destroy them. Carry on with `update_plan_task`, which changes one task and nothing else, and tell the user the document changed and the list could not be reconciled from your view.
4. **Verify before continuing.** The ledger records what the earlier session *claimed*. A task left `in_progress` may be half-written: check the working tree and the history since the document's `commit_sha` before picking it up.
5. **Adopt it.** `update_plan_task(plan_id: …, task_id: …, status: "in_progress")` on the task you are resuming. That attaches this session to the plan and makes it the session's current plan, so later calls can omit `plan_id`. This is always the last write of the procedure.

**Do not call `declare_plan_document` for that plan's file while resuming from a different checkout** — a new worktree, another clone. It would start a second plan, point this session at it, and split the ledger: your progress would land on an empty plan while the original stops moving. Rule 1's "declare a document when you read it" does not apply here. From the same checkout path, declaring it again is harmless.
```

In the tool reference table, add a line under the table:

```markdown
To read a plan from *another* session, or to find unfinished plans in the repository, use `get_declared_plans` and `list_repo_plans` on the `kcap-sessions` server: they are read-only and do not prompt.
```

- [ ] **Step 3: Update `README.md`**

At `README.md:551`, change `It provides six tools:` to `It provides eight tools:`.

Replace the `get_session_summary` bullet with:

```markdown
- `get_session_summary` — concise `summary_text` + `plan` for a session, plus `declared_plans`: a progress pointer for each plan the session declared. Use this to orient before reading the transcript.
```

After the `list_repo_sessions` bullet, add:

```markdown
- `list_repo_plans` — the declared plans on a repository that you can see, unfinished ones by default, with progress, the next open task and the sessions attached to each. Use this to find work a session left behind.
- `get_declared_plans` — a plan's documents and full task list, by `plan_id` or for every plan a `session_id` touched.
```

In the repo-resolution failure paragraph at `README.md:560`, which names only `search_sessions`, extend it so `list_repo_sessions` and `list_repo_plans` are named as the repo-scoped tools that fail closed and do not accept `repo: "all"`.

- [ ] **Step 4: Bump the plugin version**

The skills changed, and this file has moved with every skill change. In `kcap/.claude-plugin/plugin.json`, change `"version": "1.10.0"` to `"version": "1.11.0"`.

Run: `git log -3 --format=%s -- kcap/.codex-plugin/plugin.json`
If the most recent entry is the commit that added the plans server ("Declare plans and tasks from the CLI through kcap mcp plans"), the Codex manifest moves with skill changes too: bump its `"version": "1.6.0"` to `"1.7.0"`. Otherwise leave it.

- [ ] **Step 5: Check the skill files still parse**

Run: `head -20 kcap/skills/recap/SKILL.md`
Expected: the frontmatter opens and closes with `---`, and `description: >-` is followed only by indented lines — a line starting in column 0 inside it ends the block and breaks skill loading.

- [ ] **Step 6: Commit**

```bash
git add kcap/skills/recap/SKILL.md kcap/skills/plans/SKILL.md README.md kcap/.claude-plugin/plugin.json kcap/.codex-plugin/plugin.json
git commit -m "Teach the recap and plans skills to find and resume a plan" -m "Declaring a plan's document from another worktree forks the plan, and a task snapshot sent from a partial view destroys what the caller cannot see; the resume procedure is ordered around both." -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Verification

**Files:** none.

- [ ] **Step 1: Publish for AOT and look for trim warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output. Any `IL2026` or `IL3050` naming `McpSessionsServer` means a reflection-based JSON call slipped in — fix it rather than suppressing it.

- [ ] **Step 2: Check for Linear ids**

Run: `bash scripts/check-linear-ids.sh`
Expected: exit 0.

- [ ] **Step 3: Run the full unit and integration projects**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: all PASS.

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`
Expected: all PASS. A macOS App-test timeout or a Daemon-test hang here is a known flake in unrelated suites; re-run that test alone before treating it as a regression, and never call a failure in `McpSessionsServerTests` a flake.

- [ ] **Step 4: Try it for real**

Build the dev binary and point a session at a server that has the listing route:

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`

From a repository with a recorded session that declared tasks, send the MCP server a `tools/call` for `list_repo_plans` with no arguments and confirm the row for that plan appears with a sensible `next_task`; then `get_declared_plans` with that `plan_id` and confirm the result is an array of one. Against a server **without** the route, confirm `list_repo_plans` answers with the "does not list a repository's plans yet" message rather than `Error: HTTP 404`.

## After the last task

- Before the PR: a **GitHub issue in kcap-cli** must exist for the commit and PR references. Ask the user to name one or approve creating it; do not invent a number.
- The PR description references both the Linear issue and `Closes #N`; the title carries neither.
- Once the CLI PR merges, bump the `src/cli` submodule in kcap-server in its own commit (`chore: bump kcap CLI submodule`).
