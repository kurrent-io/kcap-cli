# Plan ledger CLI (kcap mcp plans) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give agents a way to declare the plan document they work from and its task list through a new `kcap-plans` MCP server, nudge them to do so at session start, ship the matching skill, and render the declared task list in `kcap validate-plan`.

**Architecture:** A new stdio MCP server (`McpPlansServer`) cloned from the work-items server's JSON-RPC loop calls the server's `/api/plans` routes; the document is read, hashed and snapshotted client-side so the server only ever sees a hash, an optional ≤256 KB snapshot and a repo-relative path. Session-id resolution and argument-shape helpers are extracted from the work-items server into two shared static classes so the two servers cannot drift. A `PlansNudgeEmitter` sits beside `WorkItemsNudgeEmitter` behind a generalized availability gate (`McpServerNudgeAvailability`) that reads the server's registration from the invoking harness's on-disk config. `validate-plan` reads the `ledger` the plan-artifacts route already returns and prefers a `declared` plan artifact for its `## Plan` section.

**Tech Stack:** .NET 10 / C# 13, NativeAOT (source-generated JSON only), TUnit tests, WireMock.Net for HTTP stubs.

**Spec:** kcap-server `docs/superpowers/specs/2026-09-14-ai2774-plan-ledger-design.md` (D7, D8 and the AI-2776 implementation notes). Issue: GitHub #939 / Linear AI-2778.

## Global Constraints

- Every wire key is snake_case; the server's routes are `POST /api/plans/documents`, `POST /api/plans/{planId}/tasks`, `POST /api/plans/{planId}/tasks/{taskRef}`, `GET /api/plans/{planId}`, where `planId` may be the literal `current` (then `session_id` is required on GET as a query string).
- Snapshot cap is 256 KB (`256 * 1024` bytes); larger content is declared by hash only and the tool result says so.
- `workspace_root` comes from `GitRepository.FindRoot(cwd)`, the same helper the Claude session-start hook uses.
- Session id resolution is the work-items one: explicit `session_id` argument, else `HarnessRequesterContext` (CLAUDE_CODE_SESSION_ID, then KCAP_SESSION_ID / CODEX_THREAD_ID).
- The skill names no other skill's file or grammar (no `progress.md`, no `### Task N`, no "superpowers").
- No Linear ids in C# comments (`scripts/check-linear-ids.sh` must pass). Comments are scarce; no change narration.
- `JsonArray.Add` on anything typed narrower than `JsonNode?` must be cast to `(JsonNode?)` (AOT trap).
- One type per file, named after the type. `FrozenSet`/`FrozenDictionary` singletons for empty read-only collections.
- README must be updated in the same PR for every user-facing CLI change.
- Commit subjects: imperative, ≤ 80 chars including the trailing `(#939)`.

---

### Task 1: Extract the shared MCP session-id resolver and argument helpers

**Files:**
- Create: `src/Capacitor.Cli/Commands/McpSessionId.cs`
- Create: `src/Capacitor.Cli/Commands/McpToolArguments.cs`
- Modify: `src/Capacitor.Cli/Commands/McpWorkItemsServer.cs` (drop `NoSessionIdMessage`, `ResolveSessionId`, `RequireString`, `TryReadInt`; call the shared classes)
- Create: `test/Capacitor.Cli.Tests.Unit/Commands/McpSessionIdTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/McpWorkItemsServerTests.cs` (move the seven `Resolve_session_id_*` tests out; repoint `NoSessionIdMessage`)

**Interfaces:**
- Produces: `static class McpSessionId { internal const string NoSessionIdMessage; internal static string Resolve(JsonObject? args); internal static string Resolve(JsonObject? args, Func<string,string?> getEnv); }`
- Produces: `static class McpToolArguments { internal static string RequireString(JsonObject? args, string key); internal static string? OptionalString(JsonObject? args, string key); internal static bool TryReadInt(JsonObject? args, string key, out int value); }`

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/Commands/McpSessionIdTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpSessionIdTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    // Injected rather than set on the process: the suite itself runs inside a harness session that
    // exports these variables, so a real-environment test could pass or fail on the runner's own id.
    static Func<string, string?> Env(Dictionary<string, string?> values) =>
        key => values.TryGetValue(key, out var value) ? value : null;

    [Test]
    public async Task Prefers_the_explicit_argument() {
        await Assert.That(McpSessionId.Resolve(Args("""{"session_id":"explicit1"}"""), Env(new()))).IsEqualTo("explicit1");
    }

    [Test]
    public async Task Strips_dashes_from_an_explicit_guid() {
        var id = McpSessionId.Resolve(Args("""{"session_id":"1234abcd-56ef-78ab-90cd-1234567890ab"}"""), Env(new()));
        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    public async Task Falls_back_to_kcap_session_id() {
        await Assert.That(McpSessionId.Resolve(new JsonObject(), Env(new() { ["KCAP_SESSION_ID"] = "envsess1" }))).IsEqualTo("envsess1");
    }

    [Test]
    public async Task Falls_back_to_codex_thread_id() {
        await Assert.That(McpSessionId.Resolve(new JsonObject(), Env(new() { ["CODEX_THREAD_ID"] = "thread-1" }))).IsEqualTo("thread-1");
    }

    [Test]
    public async Task Falls_back_to_the_running_harness_session() {
        var id = McpSessionId.Resolve(new JsonObject(), Env(new() { ["CLAUDE_CODE_SESSION_ID"] = "1234abcd-56ef-78ab-90cd-1234567890ab" }));
        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    public async Task Prefers_the_running_harness_session_over_an_inherited_env_var() {
        var id = McpSessionId.Resolve(new JsonObject(), Env(new() {
            ["KCAP_SESSION_ID"]        = "22222222222222222222222222222222",
            ["CLAUDE_CODE_SESSION_ID"] = "11111111-1111-1111-1111-111111111111"
        }));
        await Assert.That(id).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    public async Task Rejects_a_dot_segment_from_either_source() {
        var ambient = Assert.Throws<ArgumentException>(() => McpSessionId.Resolve(new JsonObject(), Env(new() { ["CLAUDE_CODE_SESSION_ID"] = ".." })));
        await Assert.That(ambient!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);

        var explicitId = Assert.Throws<ArgumentException>(() => McpSessionId.Resolve(Args("""{"session_id":"."}"""), Env(new())));
        await Assert.That(explicitId!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);
    }

    [Test]
    public async Task Rejects_a_non_string_argument_as_a_field_error() {
        await Assert.That(() => McpSessionId.Resolve(Args("""{"session_id":42}"""), Env(new())))
            .Throws<ArgumentException>().WithMessageContaining("session_id");
    }

    [Test]
    public async Task Throws_when_no_source_yields_an_id() {
        var ex = Assert.Throws<ArgumentException>(() => McpSessionId.Resolve(new JsonObject(), Env(new())));
        await Assert.That(ex!.Message).IsEqualTo(McpSessionId.NoSessionIdMessage);
    }
}

public class McpToolArgumentsTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Optional_string_is_null_for_absent_null_or_blank() {
        await Assert.That(McpToolArguments.OptionalString(Args("{}"), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":null}"""), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":"  "}"""), "k")).IsNull();
        await Assert.That(McpToolArguments.OptionalString(null, "k")).IsNull();
    }

    [Test]
    public async Task Optional_string_trims_and_rejects_a_non_string() {
        await Assert.That(McpToolArguments.OptionalString(Args("""{"k":" v "}"""), "k")).IsEqualTo("v");
        await Assert.That(() => McpToolArguments.OptionalString(Args("""{"k":7}"""), "k"))
            .Throws<ArgumentException>().WithMessageContaining("'k' must be a string");
    }

    [Test]
    public async Task Require_string_rejects_missing_blank_and_wrong_type() {
        await Assert.That(() => McpToolArguments.RequireString(Args("{}"), "k")).Throws<ArgumentException>().WithMessageContaining("required");
        await Assert.That(() => McpToolArguments.RequireString(Args("""{"k":" "}"""), "k")).Throws<ArgumentException>().WithMessageContaining("blank");
        await Assert.That(() => McpToolArguments.RequireString(Args("""{"k":[]}"""), "k")).Throws<ArgumentException>().WithMessageContaining("string");
    }

    [Test]
    public async Task Try_read_int_reads_wire_integers_and_rejects_other_shapes() {
        await Assert.That(McpToolArguments.TryReadInt(Args("""{"n":3}"""), "n", out var n)).IsTrue();
        await Assert.That(n).IsEqualTo(3);
        await Assert.That(McpToolArguments.TryReadInt(Args("{}"), "n", out _)).IsFalse();
        await Assert.That(McpToolArguments.TryReadInt(Args("""{"n":null}"""), "n", out _)).IsFalse();
        await Assert.That(() => McpToolArguments.TryReadInt(Args("""{"n":"3"}"""), "n", out _)).Throws<ArgumentException>();
        await Assert.That(() => McpToolArguments.TryReadInt(Args("""{"n":1.5}"""), "n", out _)).Throws<ArgumentException>();
    }
}
```

In `McpWorkItemsServerTests.cs`: delete the seven `Resolve_session_id_*` tests (they now live above) and replace every `McpWorkItemsServer.NoSessionIdMessage` with `McpSessionId.NoSessionIdMessage` (none remain once the seven are gone; grep to confirm).

- [ ] **Step 2: Run the new test class to verify it fails to compile**

Run: `dotnet build test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj 2>&1 | grep -E 'error CS' | head`
Expected: errors naming `McpSessionId` and `McpToolArguments`.

- [ ] **Step 3: Create the two shared classes**

`src/Capacitor.Cli/Commands/McpSessionId.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>The session an MCP tool call acts on, shared by every kcap MCP server whose tools take
/// an optional <c>session_id</c>. An explicit argument wins; otherwise the running harness's own
/// session (<see cref="HarnessRequesterContext"/>), never a bare inherited env var: a Claude Code
/// MCP server never sees <c>KCAP_SESSION_ID</c>, and one launched from another session's shell
/// inherits the parent's. Throws rather than sending a blank id, so the tool answers with a clean
/// error. Both sources canonicalize alike (a GUID to its 32-hex form).</summary>
static class McpSessionId {
    internal const string NoSessionIdMessage =
        "No session id: pass session_id explicitly or run inside a harness session kcap can identify (CLAUDE_CODE_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).";

    internal static string Resolve(JsonObject? args) => Resolve(args, Environment.GetEnvironmentVariable);

    internal static string Resolve(JsonObject? args, Func<string, string?> getEnv) {
        if (args?["session_id"] is { } node) {
            // Shape-tested like RequireString: a number or object here must answer as a field error,
            // not fall out of the dispatcher as a generic internal failure.
            if (node is not JsonValue value || !value.TryGetValue<string>(out var explicitId))
                throw new ArgumentException("'session_id' must be a string.");
            if (explicitId.Length > 0)
                return WorkContextIds.CanonicalSessionId(explicitId) ?? throw new ArgumentException(NoSessionIdMessage);
        }

        var ambient = HarnessRequesterContext.Resolve(getEnv, Directory.Exists).SessionId;
        if (WorkContextIds.CanonicalSessionId(ambient) is { } fromEnv) return fromEnv;

        throw new ArgumentException(NoSessionIdMessage);
    }
}
```

`src/Capacitor.Cli/Commands/McpToolArguments.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>Shape checks for MCP tool arguments. Only SHAPE is validated here — a present-but-wrong
/// typed argument must fail loudly rather than be dropped — while the rules the server owns
/// (vocabularies, cross-entity constraints) surface as its coded 4xx bodies.</summary>
static class McpToolArguments {
    /// <summary>A required non-blank string; absent, null, blank or wrong-typed throws the clean
    /// tool-error shape.</summary>
    internal static string RequireString(JsonObject? args, string key) {
        var node = args?[key];

        if (node is null) throw new ArgumentException($"'{key}' is required.");

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var value))
            throw new ArgumentException($"'{key}' must be a string.");

        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{key}' must not be blank.");

        return value;
    }

    /// <summary>An optional string: absent, JSON null or blank is null (trimmed otherwise); a present
    /// non-string throws.</summary>
    internal static string? OptionalString(JsonObject? args, string key) {
        var node = args?[key];

        if (node is null) return null;

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var value))
            throw new ArgumentException($"'{key}' must be a string.");

        var trimmed = value.Trim();

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Reads a numeric field as int. Returns false ONLY when the key is absent or JSON null;
    /// any PRESENT non-integer shape (string, object, array, fractional or out-of-range number)
    /// throws, so a malformed selector fails instead of degrading into a differently-shaped
    /// request. Wire JSON is validated against the RAW token via TryGetInt32 — exact, no lossy
    /// double round-trip; the int/long branches cover programmatically constructed nodes.</summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        if (node is JsonValue v) {
            if (v.TryGetValue<JsonElement>(out var el)) {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value)) return true;

                throw new ArgumentException($"'{key}' must be an integer within int range.");
            }

            if (v.TryGetValue(out value)) return true;

            if (v.TryGetValue<long>(out var lv)) {
                if (lv is < int.MinValue or > int.MaxValue)
                    throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

                value = (int)lv;

                return true;
            }
        }

        throw new ArgumentException($"'{key}' must be an integer.");
    }
}
```

- [ ] **Step 4: Repoint the work-items server**

In `McpWorkItemsServer.cs`:
- Delete the `NoSessionIdMessage` const, both `ResolveSessionId` overloads (and their doc comment), `RequireString`, and `TryReadInt`.
- Replace `ResolveSessionId(args)` with `McpSessionId.Resolve(args)` (four sites: `BuildDeclareBody`, `BuildSessionUrl`, `BuildDetachBody`; keep `ItemUrl` using `McpToolArguments.RequireString`).
- Replace `RequireString(` with `McpToolArguments.RequireString(` and `TryReadInt(` with `McpToolArguments.TryReadInt(`.
- Drop the now-unused `using System.Text.Json;` only if nothing else in the file uses it (`JsonSerializer` still does — keep it).

- [ ] **Step 5: Build and run both test classes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpSessionIdTests/*"` then the same with `McpToolArgumentsTests` and `McpWorkItemsServerTests`.
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli/Commands/McpSessionId.cs src/Capacitor.Cli/Commands/McpToolArguments.cs src/Capacitor.Cli/Commands/McpWorkItemsServer.cs test/Capacitor.Cli.Tests.Unit/Commands/McpSessionIdTests.cs test/Capacitor.Cli.Tests.Unit/Commands/McpWorkItemsServerTests.cs
/usr/bin/git -C <worktree> commit -m "Share the MCP session-id resolver across servers (#939)"
```

---

### Task 2: The plans MCP server's body builders and tool schema

**Files:**
- Create: `src/Capacitor.Cli/Commands/McpPlansServer.cs`
- Create: `src/Capacitor.Cli/Commands/PlanDocumentDeclaration.cs`
- Create: `test/Capacitor.Cli.Tests.Unit/Commands/McpPlansServerTests.cs`

**Interfaces:**
- Consumes: `McpSessionId.Resolve`, `McpToolArguments.*`, `GitRepository.FindRoot`, `McpTool`/`McpInputSchema`/`McpSchemaProperty` records from `McpReviewServer.cs`.
- Produces: `sealed class McpPlansServer(ConfigRoot, ProfileContext, TokenStore, ICapacitorHttpClient, TelemetryStartup)` with `internal const int MaxSnapshotBytes`, `internal const string CurrentPlan = "current"`, `internal const string ServerInstructions`, `internal static McpTool[] BuildToolsList()`, `internal static PlanDocumentDeclaration BuildDeclaration(JsonObject? args, string cwd, string? repoRoot)`, `internal static string WirePath(string fullPath, string? repoRoot)`, `internal static JsonObject BuildSetTasksBody(JsonObject? args)`, `internal static JsonObject BuildUpdateBody(JsonObject? args, string sessionId)`, `internal static string TaskRef(JsonObject? args)`, `internal static string? OptionalPlanId(JsonObject? args)`, `internal static string? DecodeMethod(JsonObject request)`.
- Produces: `sealed record PlanDocumentDeclaration(JsonObject Body, string WirePath, string? WorkspaceRoot, long ContentBytes, bool SnapshotAttached)`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/Commands/McpPlansServerTests.cs` (first half — builders and schema; dispatch tests are added in Task 3):

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpPlansServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A throwaway repo: a `.git` directory at the root so FindRoot recognises it, and a
    /// document under docs/.</summary>
    (string Root, string DocPath) SeedRepo(string content = "# Plan\n\n1. do it\n") {
        var root = Tmp.CreateDir("repo");
        root.CreateDir(".git");
        var doc = root.CreateDir("docs").CreateFile("plan.md", content);
        return (root.Path, doc);
    }

    // ── tool schema ──────────────────────────────────────────────────────────

    [Test]
    public async Task Tools_list_exposes_the_four_plan_tools() {
        var names = McpPlansServer.BuildToolsList().Select(t => t.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "declare_plan_document", "set_plan_tasks", "update_plan_task", "get_plan" });
    }

    [Test]
    public async Task Every_tool_declares_its_required_arguments() {
        var byName = McpPlansServer.BuildToolsList().ToDictionary(t => t.Name);
        await Assert.That(byName["declare_plan_document"].InputSchema.Required).IsEquivalentTo(new[] { "kind", "path" });
        await Assert.That(byName["set_plan_tasks"].InputSchema.Required).IsEquivalentTo(new[] { "tasks" });
        await Assert.That(byName["update_plan_task"].InputSchema.Required).IsEquivalentTo(new[] { "status" });
        await Assert.That(byName["get_plan"].InputSchema.Required).IsEmpty();
    }

    [Test]
    public async Task Tasks_array_declares_object_items() {
        var tasks = McpPlansServer.BuildToolsList().Single(t => t.Name == "set_plan_tasks").InputSchema.Properties["tasks"];
        await Assert.That(tasks.Type).IsEqualTo("array");
        await Assert.That(tasks.Items).IsNotNull();
        await Assert.That(tasks.Items!.Type).IsEqualTo("object");
    }

    [Test]
    public async Task No_tool_advertises_a_server_owned_source_argument() {
        foreach (var tool in McpPlansServer.BuildToolsList())
            await Assert.That(tool.InputSchema.Properties.Keys).DoesNotContain("source").Because($"{tool.Name} must not advertise a server-owned field");
    }

    [Test]
    public async Task Server_instructions_say_the_three_things() {
        var s = McpPlansServer.ServerInstructions;
        await Assert.That(s).Contains("declare_plan_document");
        await Assert.That(s).Contains("set_plan_tasks");
        await Assert.That(s).Contains("update_plan_task");
        await Assert.That(s).Contains("not your notes");
    }

    // ── declare_plan_document ────────────────────────────────────────────────

    [Test]
    public async Task Declaration_reads_the_file_hashes_it_and_keys_the_path_off_the_repo_root() {
        var (root, doc) = SeedRepo("# Plan\n");
        var d = McpPlansServer.BuildDeclaration(Args($$"""{"session_id":"s1","kind":"plan","path":"{{doc.Replace("\\", "\\\\")}}"}"""), cwd: root, repoRoot: root);

        await Assert.That(d.Body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(d.Body["kind"]!.GetValue<string>()).IsEqualTo("plan");
        await Assert.That(d.Body["path"]!.GetValue<string>()).IsEqualTo("docs/plan.md");
        await Assert.That(d.Body["workspace_root"]!.GetValue<string>()).IsEqualTo(root);
        await Assert.That(d.Body["content"]!.GetValue<string>()).IsEqualTo("# Plan\n");
        await Assert.That(d.Body["content_hash"]!.GetValue<string>())
            .IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("# Plan\n"))));
        await Assert.That(d.SnapshotAttached).IsTrue();
        await Assert.That(d.ContentBytes).IsEqualTo(7L);
        await Assert.That(d.Body.ContainsKey("argues_from")).IsFalse();
        await Assert.That(d.Body.ContainsKey("work_item_id")).IsFalse();
    }

    [Test]
    public async Task Declaration_resolves_a_relative_path_against_the_project_directory() {
        var (root, _) = SeedRepo();
        var d = McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"spec","path":"docs/plan.md"}"""), cwd: root, repoRoot: root);
        await Assert.That(d.Body["path"]!.GetValue<string>()).IsEqualTo("docs/plan.md");
    }

    [Test]
    public async Task Declaration_omits_the_snapshot_above_the_cap_and_still_hashes() {
        var big = new string('x', McpPlansServer.MaxSnapshotBytes + 1);
        var (root, _) = SeedRepo(big);
        var d = McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/plan.md"}"""), cwd: root, repoRoot: root);

        await Assert.That(d.Body.ContainsKey("content")).IsFalse();
        await Assert.That(d.SnapshotAttached).IsFalse();
        await Assert.That(d.ContentBytes).IsEqualTo((long)McpPlansServer.MaxSnapshotBytes + 1);
        await Assert.That(d.Body["content_hash"]!.GetValue<string>()).HasLength().EqualTo(64);
    }

    [Test]
    public async Task Declaration_at_exactly_the_cap_attaches_the_snapshot() {
        var (root, _) = SeedRepo(new string('y', McpPlansServer.MaxSnapshotBytes));
        var d = McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/plan.md"}"""), cwd: root, repoRoot: root);
        await Assert.That(d.SnapshotAttached).IsTrue();
    }

    [Test]
    public async Task Declaration_carries_argues_from_and_work_item_id_when_given() {
        var (root, _) = SeedRepo();
        var d = McpPlansServer.BuildDeclaration(
            Args("""{"session_id":"s1","kind":"plan","path":"docs/plan.md","argues_from":"docs/spec.md","work_item_id":"wi-1"}"""),
            cwd: root, repoRoot: root);

        // argues_from is a key, not a file: it need not exist and is normalized like path.
        await Assert.That(d.Body["argues_from"]!.GetValue<string>()).IsEqualTo("docs/spec.md");
        await Assert.That(d.Body["work_item_id"]!.GetValue<string>()).IsEqualTo("wi-1");
    }

    [Test]
    public async Task Declaration_outside_a_repo_sends_the_absolute_path_and_no_root() {
        var dir = Tmp.CreateDir("loose");
        var doc = dir.CreateFile("plan.md", "x");
        var d = McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"plan.md"}"""), cwd: dir.Path, repoRoot: null);

        await Assert.That(d.Body["path"]!.GetValue<string>()).IsEqualTo(doc);
        await Assert.That(d.Body["workspace_root"]).IsNull();
    }

    [Test]
    public async Task Declaration_rejects_a_missing_file_before_any_request() {
        var (root, _) = SeedRepo();
        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/nope.md"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("docs/nope.md");
    }

    [Test]
    public async Task Declaration_requires_kind_and_path() {
        var (root, _) = SeedRepo();
        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","path":"docs/plan.md"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("kind");
        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("path");
    }

    [Test]
    public async Task Wire_path_is_root_relative_with_forward_slashes_or_absolute_when_outside() {
        var root = Tmp.CreateDir("r").Path;
        await Assert.That(McpPlansServer.WirePath(Path.Combine(root, "a", "b.md"), root)).IsEqualTo("a/b.md");
        var outside = Path.Combine(Tmp.Path, "elsewhere.md");
        await Assert.That(McpPlansServer.WirePath(outside, root)).IsEqualTo(outside);
        await Assert.That(McpPlansServer.WirePath(outside, null)).IsEqualTo(outside);
    }

    // ── set_plan_tasks ───────────────────────────────────────────────────────

    [Test]
    public async Task Set_tasks_body_whitelists_task_fields_and_keeps_order() {
        var body = McpPlansServer.BuildSetTasksBody(Args("""
            {"session_id":"s1","tasks":[
              {"title":"One","status":"completed","task_id":"t1","note":"done","source":"user"},
              {"title":"Two","task_id":null}
            ]}
            """));

        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        var tasks = body["tasks"]!.AsArray();
        await Assert.That(tasks.Count).IsEqualTo(2);
        await Assert.That(tasks[0]!.ToJsonString()).IsEqualTo("""{"title":"One","task_id":"t1","status":"completed","note":"done"}""");
        await Assert.That(tasks[1]!.ToJsonString()).IsEqualTo("""{"title":"Two"}""");
    }

    [Test]
    public async Task Set_tasks_body_rejects_a_wrong_shaped_list_instead_of_dropping_it() {
        await Assert.That(() => McpPlansServer.BuildSetTasksBody(Args("""{"session_id":"s1"}""")))
            .Throws<ArgumentException>().WithMessageContaining("tasks");
        await Assert.That(() => McpPlansServer.BuildSetTasksBody(Args("""{"session_id":"s1","tasks":"One"}""")))
            .Throws<ArgumentException>().WithMessageContaining("array");
        await Assert.That(() => McpPlansServer.BuildSetTasksBody(Args("""{"session_id":"s1","tasks":["One"]}""")))
            .Throws<ArgumentException>().WithMessageContaining("object");
        await Assert.That(() => McpPlansServer.BuildSetTasksBody(Args("""{"session_id":"s1","tasks":[{"status":"pending"}]}""")))
            .Throws<ArgumentException>().WithMessageContaining("title");
    }

    [Test]
    public async Task Set_tasks_body_forwards_an_unknown_status_for_the_server_to_reject() {
        var body = McpPlansServer.BuildSetTasksBody(Args("""{"session_id":"s1","tasks":[{"title":"One","status":"parked"}]}"""));
        await Assert.That(body["tasks"]![0]!["status"]!.GetValue<string>()).IsEqualTo("parked");
    }

    // ── update_plan_task ─────────────────────────────────────────────────────

    [Test]
    public async Task Update_body_carries_status_and_optional_note() {
        var body = McpPlansServer.BuildUpdateBody(Args("""{"status":"completed","note":"shipped"}"""), "s1");
        await Assert.That(body.ToJsonString()).IsEqualTo("""{"session_id":"s1","status":"completed","note":"shipped"}""");

        var bare = McpPlansServer.BuildUpdateBody(Args("""{"status":"skipped"}"""), "s1");
        await Assert.That(bare.ContainsKey("note")).IsFalse();
    }

    [Test]
    public async Task Task_ref_takes_task_id_or_a_positive_ordinal_but_not_both() {
        await Assert.That(McpPlansServer.TaskRef(Args("""{"task_id":"t1"}"""))).IsEqualTo("t1");
        await Assert.That(McpPlansServer.TaskRef(Args("""{"ordinal":3}"""))).IsEqualTo("3");
        await Assert.That(() => McpPlansServer.TaskRef(Args("""{"task_id":"t1","ordinal":3}"""))).Throws<ArgumentException>().WithMessageContaining("not both");
        await Assert.That(() => McpPlansServer.TaskRef(Args("{}"))).Throws<ArgumentException>().WithMessageContaining("task_id");
        await Assert.That(() => McpPlansServer.TaskRef(Args("""{"ordinal":0}"""))).Throws<ArgumentException>().WithMessageContaining("positive");
        await Assert.That(() => McpPlansServer.TaskRef(Args("""{"task_id":".."}"""))).Throws<ArgumentException>().WithMessageContaining("task_id");
    }

    [Test]
    public async Task Optional_plan_id_is_null_when_omitted_and_rejects_dot_segments() {
        await Assert.That(McpPlansServer.OptionalPlanId(Args("{}"))).IsNull();
        await Assert.That(McpPlansServer.OptionalPlanId(Args("""{"plan_id":null}"""))).IsNull();
        await Assert.That(McpPlansServer.OptionalPlanId(Args("""{"plan_id":"p1"}"""))).IsEqualTo("p1");
        await Assert.That(() => McpPlansServer.OptionalPlanId(Args("""{"plan_id":"."}"""))).Throws<ArgumentException>().WithMessageContaining("plan_id");
    }

    [Test]
    public async Task Decode_method_returns_null_for_a_wrong_shaped_method() {
        await Assert.That(McpPlansServer.DecodeMethod(Args("""{"id":1,"method":{}}"""))).IsNull();
    }
}
```

Note for `TempDir` API: `Tmp.CreateDir("repo")` returns a directory handle with `.Path`, `.CreateDir(...)`, `.CreateFile(name, content)` returning the file path (check `test/Capacitor.Tests.Helpers/TempDir.cs` for the exact return types and adjust the `SeedRepo` helper accordingly before running).

- [ ] **Step 2: Run to verify compile failure**

Run: `dotnet build test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj 2>&1 | grep -E 'error CS' | head -3`
Expected: `McpPlansServer` not found.

- [ ] **Step 3: Write the declaration record**

`src/Capacitor.Cli/Commands/PlanDocumentDeclaration.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>What `declare_plan_document` sends and what it read to build it. The body is the wire
/// request; the rest annotates the tool result, since the server's response cannot know whether a
/// snapshot was attached or how large the file was.</summary>
sealed record PlanDocumentDeclaration(JsonObject Body, string WirePath, string? WorkspaceRoot, long ContentBytes, bool SnapshotAttached);
```

- [ ] **Step 4: Write the server (builders, schema, loop)**

`src/Capacitor.Cli/Commands/McpPlansServer.cs`:

```csharp
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>MCP tools for the plan ledger: declare the plan, spec or design document a session works
/// from, declare and update its task list, and read the plan back. The document is read here, so
/// its hash and snapshot are the CLI's; the server keys it off the path and the workspace root the
/// same way discovery keys a repo file. Same stdio JSON-RPC loop as <see cref="McpWorkItemsServer"/>.</summary>
sealed class McpPlansServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup) {
    /// <summary>The server's per-artifact transport cap; a larger document is declared by hash only.</summary>
    internal const int MaxSnapshotBytes = 256 * 1024;

    /// <summary>The reserved plan id the server resolves to the session's most recently written plan.</summary>
    internal const string CurrentPlan = "current";

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Resolved once, from the running harness rather than the inherited environment (see
        // HarnessRequesterContext): a relative document path is resolved against the directory the
        // harness is working in, and the repo root is what the path is keyed against.
        var requester = HarnessRequesterContext.Resolve();
        var cwd       = requester.ProjectDir ?? Directory.GetCurrentDirectory();
        var repoRoot  = GitRepository.FindRoot(cwd);

        var tools = BuildToolsList();

        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }

        var telemetry = CliTelemetry.Start(startup with { Command = "mcp-server" }, config);
        telemetry.AddSharedProperty("logged_in", loggedIn);

        await using var mcp = new McpTelemetry(telemetry);

        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        HttpClient? client = null;

        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await http.ForSessionAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwd, repoRoot);
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync($"kcap mcp plans: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
        }

        async Task<string> TimedDispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            var start = Stopwatch.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                mcp.ToolCalled("kcap-plans", tool, ok, CommandTiming.ElapsedMs(start));
            }
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        try {
            while (await reader.ReadLineAsync() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;

                try {
                    request = JsonNode.Parse(line)?.AsObject();
                } catch {
                    continue;
                }

                if (request is null) continue;

                var id     = request["id"];
                var method = DecodeMethod(request);

                if (id is null) continue;

                var response = method switch {
                    null         => BuildErrorResponse(id, -32600, "Invalid request: method must be a string"),
                    "initialize" => BuildInitializeResponse(id, request),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await TimedDispatchToolCallAsync(id, request),
                    _            => McpProtocol.TryHandleStandardMethod(method, id)
                                    ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
                };

                await writer.WriteLineAsync(response);
            }
        } finally {
            if (client is not null) {
                try { client.Dispose(); } catch { }
            }
        }

        return 0;
    }

    internal const string ServerInstructions =
        "Use these tools to keep Capacitor's record of the plan this session executes. When you write or are " +
        "handed a plan, spec or design document, declare it with declare_plan_document. When a plan has " +
        "discrete steps, declare them with set_plan_tasks and record every status change with " +
        "update_plan_task; after context compaction, get_plan returns the list with its ids. Keep whatever " +
        "ledger your own workflow asks for as well — these tools replace the harness's task list, not your notes.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-plans", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string     cwd,
            string?    repoRoot
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            return toolName switch {
                "declare_plan_document" => await DeclareAsync(id, client, baseUrl, arguments, cwd, repoRoot),
                "set_plan_tasks"        => await SetTasksAsync(id, client, baseUrl, arguments),
                "update_plan_task"      => await UpdateTaskAsync(id, client, baseUrl, arguments),
                "get_plan"              => await GetPlanAsync(id, client, baseUrl, arguments),
                _                       => throw new ArgumentException($"Unknown tool: {toolName}")
            };
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    async Task<string> DeclareAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args, string cwd, string? repoRoot) {
        var declaration = BuildDeclaration(args, cwd, repoRoot);

        using var response = await client.PostAsync($"{baseUrl}/api/plans/documents", ToJsonContent(declaration.Body));

        return await RelayAsync(id, response, body => AnnotateDeclaration(body, declaration));
    }

    async Task<string> SetTasksAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args) {
        var body   = BuildSetTasksBody(args);
        var planId = OptionalPlanId(args) ?? CurrentPlan;

        using var response = await client.PostAsync($"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}/tasks", ToJsonContent(body));

        return await RelayAsync(id, response);
    }

    /// <summary>The update route answers with the task alone, so the plan is resolved first when the
    /// caller named none: the result must say which plan it acted on, and a session with no plan
    /// gets an error that names the fix rather than a bare 404.</summary>
    async Task<string> UpdateTaskAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args) {
        var sessionId = McpSessionId.Resolve(args);
        var taskRef   = TaskRef(args);
        var body      = BuildUpdateBody(args, sessionId);
        var planId    = OptionalPlanId(args);

        if (planId is null) {
            using var current = await client.GetAsync(CurrentPlanUrl(baseUrl, sessionId));

            if (current.StatusCode == HttpStatusCode.NotFound)
                return BuildToolResult(id, NoPlanMessage(sessionId), isError: true);

            if (!current.IsSuccessStatusCode) return await RelayAsync(id, current);

            planId = ReadPlanId(await current.Content.ReadAsStringAsync())
                  ?? throw new ArgumentException("The server's current-plan response carries no plan_id.");
        }

        using var response = await client.PostAsync(
            $"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}/tasks/{Uri.EscapeDataString(taskRef)}", ToJsonContent(body));

        return await RelayAsync(id, response, task => new JsonObject {
            ["plan_id"] = planId,
            ["task"]    = TryParseObject(task) ?? (JsonNode)JsonValue.Create(task)!
        }.ToJsonString());
    }

    async Task<string> GetPlanAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args) {
        if (OptionalPlanId(args) is { } planId) {
            using var response = await client.GetAsync($"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}");

            return await RelayAsync(id, response);
        }

        var sessionId = McpSessionId.Resolve(args);

        using var current = await client.GetAsync(CurrentPlanUrl(baseUrl, sessionId));

        // A session on no plan is a valid answer, not a failure: the agent learns it has nothing
        // to recover and declares afresh.
        if (current.StatusCode == HttpStatusCode.NotFound)
            return BuildToolResult(id, EmptyPlan(sessionId));

        return await RelayAsync(id, current);
    }

    async Task<string> RelayAsync(JsonNode id, HttpResponseMessage response, Func<string, string>? shape = null) {
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, profiles.Resolution.ServerUrl!), isError: true);

        if (!response.IsSuccessStatusCode)
            return BuildToolResult(id, $"Error: HTTP {(int)response.StatusCode} — {body}", isError: true);

        return BuildToolResult(id, shape is null ? body : shape(body));
    }

    internal static string CurrentPlanUrl(string baseUrl, string sessionId) =>
        $"{baseUrl}/api/plans/{CurrentPlan}?session_id={Uri.EscapeDataString(sessionId)}";

    internal static string NoPlanMessage(string sessionId) =>
        $"Error: session {sessionId} has no plan yet — declare a plan document with declare_plan_document or call set_plan_tasks first.";

    internal static string EmptyPlan(string sessionId) =>
        new JsonObject {
            ["plan_id"]    = null,
            ["session_id"] = sessionId,
            ["documents"]  = new JsonArray(),
            ["tasks"]      = new JsonArray(),
            ["progress"]   = new JsonObject { ["completed"] = 0, ["total"] = 0, ["total_known"] = false },
            ["message"]    = "No plan declared for this session yet."
        }.ToJsonString();

    static string AnnotateDeclaration(string body, PlanDocumentDeclaration declaration) {
        if (TryParseObject(body) is not { } result) return body;

        result["path"]              = declaration.WirePath;
        result["workspace_root"]    = declaration.WorkspaceRoot;
        result["content_bytes"]     = declaration.ContentBytes;
        result["snapshot_attached"] = declaration.SnapshotAttached;
        if (!declaration.SnapshotAttached)
            result["message"] = $"Content exceeds {MaxSnapshotBytes} bytes: declared by hash only.";

        return result.ToJsonString();
    }

    static JsonObject? TryParseObject(string text) {
        try {
            return JsonNode.Parse(text) as JsonObject;
        } catch {
            return null;
        }
    }

    internal static string? ReadPlanId(string body) =>
        TryParseObject(body)?["plan_id"] is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // ── builders (pure, tested) ────────────────────────────────────────────

    internal static PlanDocumentDeclaration BuildDeclaration(JsonObject? args, string cwd, string? repoRoot) {
        var sessionId = McpSessionId.Resolve(args);
        var kind      = McpToolArguments.RequireString(args, "kind");
        var rawPath   = McpToolArguments.RequireString(args, "path");
        var fullPath  = Path.GetFullPath(rawPath, cwd);

        if (!File.Exists(fullPath))
            throw new ArgumentException($"'{rawPath}' does not exist (resolved against {cwd}).");

        var bytes    = ReadShared(fullPath);
        var attach   = bytes.Length <= MaxSnapshotBytes;
        var wirePath = WirePath(fullPath, repoRoot);

        var body = new JsonObject {
            ["session_id"]     = sessionId,
            ["kind"]           = kind,
            ["path"]           = wirePath,
            ["workspace_root"] = repoRoot,
            ["content_hash"]   = Convert.ToHexStringLower(SHA256.HashData(bytes))
        };

        if (attach) body["content"] = Encoding.UTF8.GetString(bytes);

        if (McpToolArguments.OptionalString(args, "argues_from") is { } arguesFrom)
            body["argues_from"] = WirePath(Path.GetFullPath(arguesFrom, cwd), repoRoot);

        if (McpToolArguments.OptionalString(args, "work_item_id") is { } workItemId)
            body["work_item_id"] = workItemId;

        return new(body, wirePath, repoRoot, bytes.Length, attach);
    }

    /// <summary>Root-relative with forward slashes when the file is inside the repo, so the server
    /// keys it exactly as discovery keys the same file; the absolute path otherwise, which the
    /// server accepts only when its own captured root contains it.</summary>
    internal static string WirePath(string fullPath, string? repoRoot) {
        if (repoRoot is null) return fullPath;

        var relative = Path.GetRelativePath(repoRoot, fullPath);

        if (relative == "." || relative == ".." || Path.IsPathRooted(relative)
         || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
         || relative.StartsWith("../", StringComparison.Ordinal))
            return fullPath;

        return relative.Replace('\\', '/');
    }

    // Shared-read so the agent that just wrote the document is never denied its own write handle.
    static byte[] ReadShared(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static JsonObject BuildSetTasksBody(JsonObject? args) {
        var body = new JsonObject { ["session_id"] = McpSessionId.Resolve(args) };

        if (args is null || !args.TryGetPropertyValue("tasks", out var node) || node is null)
            throw new ArgumentException("'tasks' is required.");

        if (node is not JsonArray array) throw new ArgumentException("'tasks' must be an array of task objects.");

        var tasks = new JsonArray();

        foreach (var element in array) {
            if (element is not JsonObject task)
                throw new ArgumentException("'tasks' must contain only objects, each with a 'title'.");

            var entry = new JsonObject { ["title"] = McpToolArguments.RequireString(task, "title") };

            if (McpToolArguments.OptionalString(task, "task_id") is { } taskId) entry["task_id"] = taskId;
            if (McpToolArguments.OptionalString(task, "status")  is { } status) entry["status"]  = status;
            if (McpToolArguments.OptionalString(task, "note")    is { } note)   entry["note"]    = note;

            tasks.Add((JsonNode?)entry);
        }

        body["tasks"] = tasks;

        return body;
    }

    internal static JsonObject BuildUpdateBody(JsonObject? args, string sessionId) {
        var body = new JsonObject {
            ["session_id"] = sessionId,
            ["status"]     = McpToolArguments.RequireString(args, "status")
        };

        if (McpToolArguments.OptionalString(args, "note") is { } note) body["note"] = note;

        return body;
    }

    /// <summary>The route segment naming the task: its id, or its 1-based ordinal.</summary>
    internal static string TaskRef(JsonObject? args) {
        var taskId     = McpToolArguments.OptionalString(args, "task_id");
        var hasOrdinal = McpToolArguments.TryReadInt(args, "ordinal", out var ordinal);

        if (taskId is not null && hasOrdinal) throw new ArgumentException("Pass either 'task_id' or 'ordinal', not both.");
        if (taskId is not null) return ValidId(taskId, "task_id");
        if (hasOrdinal) return ordinal > 0 ? ordinal.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("'ordinal' must be a positive integer.");

        throw new ArgumentException("Pass 'task_id' or 'ordinal' to name the task.");
    }

    internal static string? OptionalPlanId(JsonObject? args) =>
        McpToolArguments.OptionalString(args, "plan_id") is { } id ? ValidId(id, "plan_id") : null;

    // `.` is unreserved, so escaping leaves a dot segment intact and URI normalization would walk
    // it out of the route.
    static string ValidId(string id, string key) =>
        id is "." or ".." ? throw new ArgumentException($"'{key}' is not a valid id.") : id;

    internal static string? DecodeMethod(JsonObject request) {
        try {
            return request["method"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse<McpToolCallResult>(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

    static string BuildErrorResponse(JsonNode id, int code, string message) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = JsonSerializer.SerializeToNode(new McpError(code, message), McpJsonContext.Default.McpError)
        };

        return envelope.ToJsonString();
    }

    static string ToResponse<T>(JsonNode id, T result, JsonTypeInfo<T> typeInfo) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = JsonSerializer.SerializeToNode(result, typeInfo)
        };

        return envelope.ToJsonString();
    }

    internal static McpTool[] BuildToolsList() => [
        new("declare_plan_document",
            "Declare the plan, spec or design document this session works from. Call it when you write such a "
          + "document or are handed one. The file is read locally: its SHA-256 and, up to 256 KB, its content are "
          + "recorded, and the path is keyed against the git repository root. Returns plan_id, document_key and "
          + "whether the plan was created; declaring the same document again attaches this session to the same plan.",
            new("object", new() {
                ["kind"]         = new("string", "One of 'plan', 'spec' or 'design'."),
                ["path"]         = new("string", "Path to the document, absolute or relative to the project directory."),
                ["argues_from"]  = new("string", "Path of the document this one argues from — the spec a plan implements, or the design a spec refines — so both land on one plan."),
                ["work_item_id"] = new("string", "Work item the plan belongs to, when known."),
                ["session_id"]   = new("string", "Session to attach. Defaults to the session this server runs in when omitted.")
            }, ["kind", "path"])),
        new("set_plan_tasks",
            "Declare the plan's task list as a full ordered snapshot, replacing the declared list. Call it when a "
          + "plan has discrete steps, and again — with the whole list — when the steps change. An entry carrying a "
          + "task_id the plan already knows keeps it; the rest are minted. Without plan_id the session's current "
          + "plan is used, and a session with no plan gets one created. Returns the tasks with their ids and ordinals.",
            new("object", new() {
                ["tasks"]      = new("array", "The complete ordered task list.",
                    new("object", "A task: {title, task_id?, status?: pending|in_progress|completed|skipped, note?}.")),
                ["plan_id"]    = new("string", "Plan to write to. Defaults to the session's current plan."),
                ["session_id"] = new("string", "Session making the declaration. Defaults to the session this server runs in when omitted.")
            }, ["tasks"])),
        new("update_plan_task",
            "Record one task's status transition — call it every time a task starts, finishes or is skipped. Name "
          + "the task by task_id (from set_plan_tasks or get_plan) or by its 1-based ordinal. Without plan_id the "
          + "session's current plan is used. The result names the plan it acted on.",
            new("object", new() {
                ["task_id"]    = new("string", "The task's id."),
                ["ordinal"]    = new("integer", "The task's 1-based position, as an alternative to task_id."),
                ["status"]     = new("string", "One of 'pending', 'in_progress', 'completed' or 'skipped'."),
                ["note"]       = new("string", "Optional note on the transition — why a task was skipped, what blocked it."),
                ["plan_id"]    = new("string", "Plan the task belongs to. Defaults to the session's current plan."),
                ["session_id"] = new("string", "Session recording the change. Defaults to the session this server runs in when omitted.")
            }, ["status"])),
        new("get_plan",
            "Read a plan back: its documents, tasks with status and source, and progress. Call it to recover the "
          + "task list after context compaction instead of re-reading a ledger file. Without plan_id the session's "
          + "current plan is returned; a session with no plan gets an empty result, not an error.",
            new("object", new() {
                ["plan_id"]    = new("string", "Plan to read. Defaults to the session's current plan."),
                ["session_id"] = new("string", "Session whose current plan to read. Defaults to the session this server runs in when omitted.")
            }, []))
    ];
}
```

- [ ] **Step 5: Run the builder/schema tests**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/McpPlansServerTests/*"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli/Commands/McpPlansServer.cs src/Capacitor.Cli/Commands/PlanDocumentDeclaration.cs test/Capacitor.Cli.Tests.Unit/Commands/McpPlansServerTests.cs
/usr/bin/git -C <worktree> commit -m "Add the kcap-plans MCP server (#939)"
```

---

### Task 3: Dispatch tests, DI registration and the `kcap mcp plans` entry point

**Files:**
- Modify: `src/Capacitor.Cli/Commands/CommandServices.cs` (add `services.AddTransient<McpPlansServer>();` after the workitems line)
- Modify: `src/Capacitor.Cli/Program.cs` (usage lines + `case "plans"`)
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/McpPlansServerTests.cs` (append dispatch tests)

- [ ] **Step 1: Append the dispatch tests**

Append inside `McpPlansServerTests`:

```csharp
    // ── dispatch: the route/method/body pairing itself ────────────────────────

    /// <summary>Scripted fake transport: each call is recorded, and a GET of the current plan can be
    /// answered with a canned body so the update path's resolve-then-post is observable.</summary>
    sealed class ScriptedHandler(string currentPlanBody = """{"plan_id":"resolved1"}""", int currentPlanStatus = 200) : HttpMessageHandler {
        public List<(HttpMethod Method, string Url, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method, request.RequestUri!.ToString(), body));

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/api/plans/current", StringComparison.Ordinal))
                return new HttpResponseMessage((System.Net.HttpStatusCode)currentPlanStatus) { Content = new StringContent(currentPlanBody) };

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"plan_id":"p1","tasks":[]}""") };
        }
    }

    McpPlansServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), NoTelemetry.Startup);

    async Task<(ScriptedHandler Handler, string Response)> DispatchAsync(string toolName, string argsJson, ScriptedHandler? handler = null, string? cwd = null, string? repoRoot = null) {
        handler ??= new ScriptedHandler();
        using var client = new HttpClient(handler);

        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = JsonNode.Parse(argsJson) }
        };

        var response = await Server().HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x", cwd ?? Tmp.Path, repoRoot);

        return (handler, response);
    }

    static string ResultText(string response) =>
        JsonNode.Parse(response)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    static bool IsError(string response) =>
        JsonNode.Parse(response)!["result"]!["isError"]?.GetValue<bool>() == true;

    [Test]
    public async Task Dispatch_declare_posts_the_declaration_and_annotates_the_result() {
        var (root, _) = SeedRepo("# Plan\n");
        var (h, response) = await DispatchAsync("declare_plan_document", """{"session_id":"s1","kind":"plan","path":"docs/plan.md"}""", cwd: root, repoRoot: root);

        await Assert.That(h.Calls.Count).IsEqualTo(1);
        await Assert.That(h.Calls[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Calls[0].Url).IsEqualTo("http://x/api/plans/documents");
        var sent = JsonNode.Parse(h.Calls[0].Body!)!.AsObject();
        await Assert.That(sent["path"]!.GetValue<string>()).IsEqualTo("docs/plan.md");
        await Assert.That(sent["workspace_root"]!.GetValue<string>()).IsEqualTo(root);

        var result = JsonNode.Parse(ResultText(response))!.AsObject();
        await Assert.That(result["plan_id"]!.GetValue<string>()).IsEqualTo("p1");
        await Assert.That(result["snapshot_attached"]!.GetValue<bool>()).IsTrue();
        await Assert.That(result["content_bytes"]!.GetValue<long>()).IsEqualTo(7L);
    }

    [Test]
    public async Task Dispatch_set_tasks_targets_the_named_plan_or_current() {
        var (named, _) = await DispatchAsync("set_plan_tasks", """{"session_id":"s1","plan_id":"p1","tasks":[{"title":"One"}]}""");
        await Assert.That(named.Calls[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(named.Calls[0].Url).IsEqualTo("http://x/api/plans/p1/tasks");
        await Assert.That(named.Calls[0].Body).IsEqualTo("""{"session_id":"s1","tasks":[{"title":"One"}]}""");

        var (current, _) = await DispatchAsync("set_plan_tasks", """{"session_id":"s1","tasks":[{"title":"One"}]}""");
        await Assert.That(current.Calls[0].Url).IsEqualTo("http://x/api/plans/current/tasks");
    }

    [Test]
    public async Task Dispatch_update_with_a_plan_id_posts_the_ordinal_route_and_names_the_plan() {
        var (h, response) = await DispatchAsync("update_plan_task", """{"session_id":"s1","plan_id":"p1","ordinal":3,"status":"completed"}""");

        await Assert.That(h.Calls.Count).IsEqualTo(1);
        await Assert.That(h.Calls[0].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Calls[0].Url).IsEqualTo("http://x/api/plans/p1/tasks/3");
        await Assert.That(h.Calls[0].Body).IsEqualTo("""{"session_id":"s1","status":"completed"}""");

        var result = JsonNode.Parse(ResultText(response))!.AsObject();
        await Assert.That(result["plan_id"]!.GetValue<string>()).IsEqualTo("p1");
        await Assert.That(result["task"]).IsNotNull();
    }

    [Test]
    public async Task Dispatch_update_without_a_plan_id_resolves_the_current_plan_first() {
        var (h, response) = await DispatchAsync("update_plan_task", """{"session_id":"s1","task_id":"t9","status":"in_progress","note":"started"}""");

        await Assert.That(h.Calls.Count).IsEqualTo(2);
        await Assert.That(h.Calls[0].Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(h.Calls[0].Url).IsEqualTo("http://x/api/plans/current?session_id=s1");
        await Assert.That(h.Calls[1].Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(h.Calls[1].Url).IsEqualTo("http://x/api/plans/resolved1/tasks/t9");
        await Assert.That(h.Calls[1].Body).IsEqualTo("""{"session_id":"s1","status":"in_progress","note":"started"}""");
        await Assert.That(JsonNode.Parse(ResultText(response))!["plan_id"]!.GetValue<string>()).IsEqualTo("resolved1");
    }

    [Test]
    public async Task Dispatch_update_on_a_session_with_no_plan_is_an_error_that_names_the_fix() {
        var (h, response) = await DispatchAsync("update_plan_task", """{"session_id":"s1","ordinal":1,"status":"completed"}""",
            new ScriptedHandler(currentPlanBody: "", currentPlanStatus: 404));

        await Assert.That(h.Calls.Count).IsEqualTo(1);
        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(ResultText(response)).Contains("declare_plan_document");
    }

    [Test]
    public async Task Dispatch_get_plan_reads_the_named_plan_or_the_sessions_current_one() {
        var (named, _) = await DispatchAsync("get_plan", """{"plan_id":"p1"}""");
        await Assert.That(named.Calls[0].Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(named.Calls[0].Url).IsEqualTo("http://x/api/plans/p1");
        await Assert.That(named.Calls[0].Body).IsNull();

        var (current, _) = await DispatchAsync("get_plan", """{"session_id":"s1"}""");
        await Assert.That(current.Calls[0].Url).IsEqualTo("http://x/api/plans/current?session_id=s1");
    }

    [Test]
    public async Task Dispatch_get_plan_on_a_session_with_no_plan_is_an_empty_result_not_an_error() {
        var (_, response) = await DispatchAsync("get_plan", """{"session_id":"s1"}""", new ScriptedHandler(currentPlanBody: "", currentPlanStatus: 404));

        await Assert.That(IsError(response)).IsFalse();
        var result = JsonNode.Parse(ResultText(response))!.AsObject();
        await Assert.That(result["plan_id"]).IsNull();
        await Assert.That(result["tasks"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(result["progress"]!["total_known"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Dispatch_of_a_malformed_call_never_reaches_the_network() {
        var (h, response) = await DispatchAsync("set_plan_tasks", """{"session_id":"s1","tasks":"nope"}""");
        await Assert.That(h.Calls.Count).IsEqualTo(0);
        await Assert.That(IsError(response)).IsTrue();
    }

    [Test]
    public async Task Response_ok_reads_false_from_an_unknown_tool_and_true_from_a_success() {
        var (_, unknown) = await DispatchAsync("not_a_real_tool", "{}");
        await Assert.That(Capacitor.Cli.Core.Telemetry.McpTelemetry.ResponseOk(unknown)).IsFalse();

        var (_, ok) = await DispatchAsync("get_plan", """{"plan_id":"p1"}""");
        await Assert.That(Capacitor.Cli.Core.Telemetry.McpTelemetry.ResponseOk(ok)).IsTrue();
    }
```

- [ ] **Step 2: Run the class; expected PASS** (the server already exists; these exercise it)

- [ ] **Step 3: Register and route**

`CommandServices.cs`: after `services.AddTransient<McpWorkItemsServer>();` add `services.AddTransient<McpPlansServer>();`.

`Program.cs` mcp block: change the usage line to `Usage: kcap mcp review|judge|sessions|flows|flow-result|memory|workitems|plans|analytics …`, add `Console.Error.WriteLine("  kcap mcp plans");` after the workitems line, and add before `case "analytics"`:

```csharp
            case "plans":
                return await Run<McpPlansServer>().RunAsync();
```

- [ ] **Step 4: Build the CLI and smoke the stdio loop**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj 2>&1 | grep -E 'warning|error' | head`
Expected: no output (no warnings).

Then: `printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05"}}' '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' | KCAP_CONFIG_DIR=$(mktemp -d) dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj --no-build -- mcp plans`
Expected: two JSON-RPC lines, the second listing the four tools.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli/Commands/CommandServices.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/Commands/McpPlansServerTests.cs
/usr/bin/git -C <worktree> commit -m "Route kcap mcp plans to the plans server (#939)"
```

---

### Task 4: Register `kcap-plans` everywhere the other servers are

**Files:**
- Modify: `src/Capacitor.Cli.Core/Mcp/KcapMcpServers.cs` (new `All` entry after `kcap-workitems`)
- Modify: `src/Capacitor.Cli.Core/KcapMcpRegistry.cs` (new `Entries` line)
- Modify: `src/Capacitor.Cli.Core/Harness/Pi/PiMcpExtensionInstaller.cs` (`KCAP_MCP_SERVERS` gains `"plans"`)
- Modify: `kcap/.mcp.json`, `kcap/.codex-mcp.json` (new `kcap-plans` entries)
- Modify: `kcap/.claude-plugin/plugin.json` (`1.9.0` → `1.10.0`)
- Modify tests: `test/Capacitor.Cli.Core.Tests.Unit/Mcp/KcapMcpServersTests.cs` (four list literals), `test/Capacitor.Cli.Core.Tests.Unit/Harness/Pi/PiMcpExtensionInstallerTests.cs` (array literal), `test/Capacitor.Cli.Tests.Unit/Commands/KcapMcpRegistryReviewFlowTests.cs` (add a `kcap-plans` rejection test), `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryTests.cs` (add `[Arguments("kcap-plans")]`)

- [ ] **Step 1: Update the test literals first**

`KcapMcpServersTests.cs`: every `new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-analytics" }` becomes `new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-plans", "kcap-analytics" }`; the repo-scoped literal gains `"kcap-plans"` after `"kcap-workitems"`.

`PiMcpExtensionInstallerTests.cs` line 38: `"[\"review\", \"sessions\", \"flows\", \"memory\", \"analytics\", \"workitems\", \"plans\"]"`.

`KcapMcpRegistryReviewFlowTests.cs`: add after the workitems test:

```csharp
    [Test]
    public async Task Resolve_rejects_write_server_kcap_plans() {
        var ok = KcapMcpRegistry.TryResolveReviewFlowAllowlist(["kcap-plans"], out _, out var rejected);

        await Assert.That(ok).IsFalse();
        await Assert.That(rejected).IsEqualTo("kcap-plans");
    }
```

`AcpHostedAgentRuntimeFactoryTests.cs`: add `[Arguments("kcap-plans")]` after `[Arguments("kcap-workitems")]`.

- [ ] **Step 2: Run the four affected classes to see them fail**

Run each with `--treenode-filter "/*/*/<Class>/*"` in the owning project. Expected: FAIL (list mismatches; `McpCanonicalContractTests` also fails once `All` changes until the JSON files follow).

- [ ] **Step 3: Make the registrations**

`KcapMcpServers.All`, after the workitems entry:

```csharp
        new("kcap-plans", ["mcp", "plans"], NeedsProjectCwd: true,
            "Declare the plan, spec or design document a session works from and the plan's task list; update task status and read the plan back after compaction."),
```

`KcapMcpRegistry.Entries`: `["kcap-plans"] = new("kcap-plans", ["mcp", "plans"], false),` after the workitems line.

`PiMcpExtensionInstaller.ExtensionContent`: `const KCAP_MCP_SERVERS = ["review", "sessions", "flows", "memory", "analytics", "workitems", "plans"];`

`kcap/.mcp.json`, after `kcap-workitems`:

```json
    "kcap-plans": {
      "command": "kcap",
      "args": ["mcp", "plans"],
      "cwd": "${CLAUDE_PROJECT_DIR}",
      "description": "Plan ledger — declare the plan, spec or design document this session works from (declare_plan_document), declare its task list (set_plan_tasks), record each task's status change (update_plan_task) and read the plan back after compaction (get_plan)."
    },
```

`kcap/.codex-mcp.json`, after `kcap-workitems`:

```json
    "kcap-plans": {
      "command": "kcap",
      "args": ["mcp", "plans"]
    }
```

`kcap/.claude-plugin/plugin.json`: `"version": "1.10.0"`.

- [ ] **Step 4: Run the affected classes plus the contract tests; expected PASS**

`McpCanonicalContractTests`, `KcapMcpServersTests`, `PiMcpExtensionInstallerTests`, `KcapMcpRegistryTests` (Core suite); `FlowsDriverSchemaConformanceTests`, `KcapMcpRegistryReviewFlowTests`, `PluginCommandCodexTests`, `PluginCommandCursorTests`, `PluginCommandGeminiTests` (CLI suite); `AcpHostedAgentRuntimeFactoryTests` (daemon suite).

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli.Core/Mcp/KcapMcpServers.cs src/Capacitor.Cli.Core/KcapMcpRegistry.cs src/Capacitor.Cli.Core/Harness/Pi/PiMcpExtensionInstaller.cs kcap/.mcp.json kcap/.codex-mcp.json kcap/.claude-plugin/plugin.json test/Capacitor.Cli.Core.Tests.Unit/Mcp/KcapMcpServersTests.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Pi/PiMcpExtensionInstallerTests.cs test/Capacitor.Cli.Tests.Unit/Commands/KcapMcpRegistryReviewFlowTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryTests.cs
/usr/bin/git -C <worktree> commit -m "Register kcap-plans with every harness (#939)"
```

---

### Task 5: `validate-plan` renders the declared ledger and prefers the declared plan

**Files:**
- Create: `src/Capacitor.Cli.Core/Plans/PlanLedgerDto.cs`, `src/Capacitor.Cli.Core/Plans/PlanLedgerTaskDto.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` (`PlanArtifactsResponseDto.Ledger`; `[JsonSerializable(typeof(Plans.PlanLedgerDto))]`)
- Modify: `src/Capacitor.Cli/Commands/ValidatePlanCommand.cs`
- Modify: `src/Capacitor.Cli.Core/Resources/help-validate-plan.txt`, `kcap/skills/validate-plan/SKILL.md`
- Modify: `test/Capacitor.Cli.Tests.Unit/Commands/ValidatePlanCommandTests.cs`

**Interfaces:**
- Produces: `PlanLedgerDto { PlanId, Tasks, Completed, Total, TotalKnown, IsComplete, WithheldContributions }`, `PlanLedgerTaskDto { TaskId, Ordinal, Title, Status, Note, Source, StatusPartial }`; `PlanArtifactsResponseDto.Ledger`.

- [ ] **Step 1: Write the failing tests** (append to `ValidatePlanCommandTests`; reuse its `SessionId`, `Api()`, `CaptureStdoutAsync`, `RecapJson` helpers)

```csharp
    static string ArtifactJson(string id, string kind, string source, string content) => $$"""
        {
          "artifact_id": "{{id}}", "kind": "{{kind}}", "title": "{{id}}", "source": "{{source}}",
          "session_id": "{{SessionId}}", "content": "{{content}}", "content_state": "ok",
          "is_complete": true, "is_confirmed": true, "is_truncated": false, "content_hash": "h-{{id}}",
          "version": 1, "discovered_at": "2026-07-01T00:00:00Z", "confidence": "high", "reason": "r",
          "is_primary": false
        }
        """;

    void StubArtifacts(string body) =>
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/plan-artifacts").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));

    void StubRecap() =>
        _server.Given(Request.Create().WithPath($"/api/sessions/{SessionId}/recap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RecapJson()));

    [Test, NotInParallel]
    public async Task Ledger_renders_a_tasks_section_with_ordinal_status_title_and_source() {
        StubArtifacts($$"""
            {
              "primary": {{ArtifactJson("art-1", "plan", "native_plan", "Step 1")}},
              "artifacts": [],
              "diagnostics": [],
              "ledger": {
                "plan_id": "p1",
                "tasks": [
                  {"task_id":"t1","ordinal":1,"title":"Add DTOs","status":"completed","note":null,"source":"mcp","status_partial":false},
                  {"task_id":"t2","ordinal":2,"title":"Wire server","status":"in_progress","note":"halfway","source":"mcp","status_partial":true}
                ],
                "completed": 1, "total": 2, "total_known": true, "is_complete": true, "withheld_contributions": 0
              }
            }
            """);
        StubRecap();

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains("## Tasks");
        await Assert.That(stdout).Contains("1 of 2 completed");
        await Assert.That(stdout).Contains("1. [completed] Add DTOs (mcp)");
        await Assert.That(stdout).Contains("2. [in_progress] Wire server (mcp) (status partial)");
        await Assert.That(stdout).Contains("note: halfway");
        // Tasks sit between the plan and what's done.
        await Assert.That(stdout.IndexOf("## Plan", StringComparison.Ordinal)).IsLessThan(stdout.IndexOf("## Tasks", StringComparison.Ordinal));
        await Assert.That(stdout.IndexOf("## Tasks", StringComparison.Ordinal)).IsLessThan(stdout.IndexOf("## What's Done", StringComparison.Ordinal));
        await Assert.That(stdout).Contains("Tasks section");
    }

    [Test, NotInParallel]
    public async Task Ledger_with_unknown_total_and_withheld_rows_says_so() {
        StubArtifacts($$"""
            {
              "primary": {{ArtifactJson("art-1", "plan", "native_plan", "Step 1")}},
              "artifacts": [], "diagnostics": [],
              "ledger": { "plan_id": "p1",
                "tasks": [{"task_id":"t1","ordinal":1,"title":"Only","status":"pending","note":null,"source":"mcp","status_partial":false}],
                "completed": 0, "total": 1, "total_known": false, "is_complete": false, "withheld_contributions": 2 }
            }
            """);
        StubRecap();

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).Contains("0 completed, total unknown");
        await Assert.That(stdout).Contains("[tasks incomplete: 2 contribution(s) from sessions you cannot see were withheld]");
    }

    [Test, NotInParallel]
    public async Task No_ledger_renders_no_tasks_section() {
        StubArtifacts($$"""{ "primary": {{ArtifactJson("art-1", "plan", "native_plan", "Step 1")}}, "artifacts": [], "diagnostics": [] }""");
        StubRecap();

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout).DoesNotContain("## Tasks");
        await Assert.That(stdout).DoesNotContain("Tasks section");
    }

    [Test, NotInParallel]
    public async Task Declared_plan_document_leads_the_plan_section_over_the_primary() {
        StubArtifacts($$"""
            {
              "primary": {{ArtifactJson("art-native", "plan", "native_plan", "NATIVE PLAN")}},
              "artifacts": [{{ArtifactJson("art-native", "plan", "native_plan", "NATIVE PLAN")}}, {{ArtifactJson("art-declared", "plan", "declared", "DECLARED PLAN")}}],
              "diagnostics": []
            }
            """);
        StubRecap();

        var stdout = await CaptureStdoutAsync(() => ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(stdout.IndexOf("DECLARED PLAN", StringComparison.Ordinal)).IsLessThan(stdout.IndexOf("NATIVE PLAN", StringComparison.Ordinal));
        // Both still render, once each.
        await Assert.That(stdout.Split("NATIVE PLAN").Length - 1).IsEqualTo(1);
    }

    [Test, NotInParallel]
    public async Task Unavailable_declared_plan_exits_2_like_an_unavailable_primary() {
        var declaredUnavailable = ArtifactJson("art-declared", "plan", "declared", "").Replace("\"content_state\": \"ok\"", "\"content_state\": \"unavailable\"").Replace("\"content\": \"\"", "\"content\": null");
        StubArtifacts($$"""
            { "primary": {{ArtifactJson("art-native", "plan", "native_plan", "NATIVE PLAN")}},
              "artifacts": [{{declaredUnavailable}}], "diagnostics": [] }
            """);
        StubRecap();

        var exitCode = -1;
        await CaptureStdoutAsync(async () => exitCode = await ValidatePlanCommand.HandleCore(Api(), SessionId));

        await Assert.That(exitCode).IsEqualTo(2);
    }
```

- [ ] **Step 2: Run the class; expected FAIL** (no `## Tasks`, declared not preferred)

- [ ] **Step 3: DTOs**

`src/Capacitor.Cli.Core/Plans/PlanLedgerDto.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Plans;

/// The declared task list riding `GET /api/sessions/{id}/plan-artifacts` as `ledger`. Absent on a
/// server without the plan ledger and on a session with nothing declared. `IsComplete` is false and
/// `WithheldContributions` counts the rows when contributions from sessions the viewer cannot see
/// were excluded.
public sealed record PlanLedgerDto {
    List<PlanLedgerTaskDto> _tasks = [];

    [JsonPropertyName("plan_id")]                public string?                 PlanId                { get; init; }
    [JsonPropertyName("tasks")]                  public List<PlanLedgerTaskDto> Tasks                 { get => _tasks; init => _tasks = value ?? []; }
    [JsonPropertyName("completed")]              public int                     Completed             { get; init; }
    [JsonPropertyName("total")]                  public int                     Total                 { get; init; }
    [JsonPropertyName("total_known")]            public bool                    TotalKnown            { get; init; }
    [JsonPropertyName("is_complete")]            public bool                    IsComplete            { get; init; } = true;
    [JsonPropertyName("withheld_contributions")] public int                     WithheldContributions { get; init; }
}
```

`src/Capacitor.Cli.Core/Plans/PlanLedgerTaskDto.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Plans;

/// One declared task. `Status` is pending|in_progress|completed|skipped and `Source` is `mcp`,
/// `user` or `adapter:<name>`; both are displayed, never branched on, so the server may widen them.
/// `StatusPartial` marks a status whose latest change came from a session the viewer cannot see.
public sealed record PlanLedgerTaskDto {
    [JsonPropertyName("task_id")]        public string? TaskId        { get; init; }
    [JsonPropertyName("ordinal")]        public int     Ordinal       { get; init; }
    [JsonPropertyName("title")]          public string  Title         { get; init; } = "";
    [JsonPropertyName("status")]         public string  Status        { get; init; } = "";
    [JsonPropertyName("note")]           public string? Note          { get; init; }
    [JsonPropertyName("source")]         public string  Source        { get; init; } = "";
    [JsonPropertyName("status_partial")] public bool    StatusPartial { get; init; }
}
```

`Models.cs`: in `PlanArtifactsResponseDto` add `[JsonPropertyName("ledger")]      public Plans.PlanLedgerDto? Ledger { get; init; }`; add `[JsonSerializable(typeof(Plans.PlanLedgerDto))]` after the `PlanArtifactsResponseDto` registration.

- [ ] **Step 4: Rendering**

In `ValidatePlanCommand.HandleCore`, after `var artifacts = ...`:

```csharp
        // The declared plan document leads: it is the one the agent said it executes, whatever
        // discovery ranked first.
        var lead = artifacts.FirstOrDefault(a => a.Source == "declared" && a.Kind == "plan") ?? primary;
```

Replace `RenderPlanArtifacts(primary, artifacts)` with `RenderPlanArtifacts(lead, artifacts)` and rename that method's parameter from `primary` to `lead` (and `isPrimary` to `isLead`, `primaryUnavailable` to `leadUnavailable`); update its doc comment to say the lead artifact is the declared plan document when one exists, else the server's primary. Then:

```csharp
        var ledger = response?.Ledger;
        var leadUnavailable = await RenderPlanArtifacts(lead, artifacts);
        await RenderTasks(ledger);
        await RenderWhatsDoneAndInstructions(summaries, work, withTasks: ledger is { Tasks.Count: > 0 });
```

Add:

```csharp
    /// <summary>The declared task list, when the server sent one with at least one task. Omitted
    /// otherwise, so a server without the ledger renders exactly as before.</summary>
    static async Task RenderTasks(PlanLedgerDto? ledger) {
        if (ledger is null || ledger.Tasks.Count == 0) return;

        await Console.Out.WriteLineAsync("## Tasks");
        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync(ledger.TotalKnown
            ? $"{ledger.Completed} of {ledger.Total} completed"
            : $"{ledger.Completed} completed, total unknown");

        if (!ledger.IsComplete)
            await Console.Out.WriteLineAsync(
                $"[tasks incomplete: {ledger.WithheldContributions} contribution(s) from sessions you cannot see were withheld]");

        await Console.Out.WriteLineAsync();

        foreach (var task in ledger.Tasks) {
            var partial = task.StatusPartial ? " (status partial)" : "";
            await Console.Out.WriteLineAsync($"{task.Ordinal}. [{task.Status}] {task.Title} ({task.Source}){partial}");

            if (!string.IsNullOrWhiteSpace(task.Note))
                await Console.Out.WriteLineAsync($"   note: {task.Note}");
        }

        await Console.Out.WriteLineAsync();
    }
```

`RenderWhatsDoneAndInstructions(List<RecapEntry> summaries, List<RecapEntry> work, bool withTasks = false)`: after the existing instructions line add

```csharp
        if (withTasks)
            await Console.Out.WriteLineAsync(
                "The Tasks section is the declared checklist: a task still pending or in_progress is not done, whatever the file list suggests."
            );
```

Add `using Capacitor.Cli.Core.Plans;` to the file.

- [ ] **Step 5: Docs for the command**

`help-validate-plan.txt` — replace the whole file with:

```
kcap validate-plan — Validate plan completion for a session

Usage: kcap validate-plan [sessionId]

Arguments:
  sessionId               Session ID (defaults to KCAP_SESSION_ID
                          on Claude or CODEX_THREAD_ID on Codex 0.81+)

Output:
  ## Plan          The plan text — the document declared through
                   `kcap mcp plans` when there is one, else the
                   server's best discovered candidate, then the
                   other candidates.
  ## Tasks         The declared task list (ordinal, status, title,
                   source) and its progress; omitted when nothing
                   was declared.
  ## What's Done   AI summary and the session's file writes/edits.
  ## Instructions  What to compare against what.

Exit codes: 0 rendered or no plan found; 1 refused/unreachable
server; 2 the leading plan's content could not be retrieved.
```

`kcap/skills/validate-plan/SKILL.md` — in "What It Returns", after the `## Plan` bullet add:

```
- **`## Tasks`** — the task list declared through the `kcap-plans` MCP tools, one line per task as `ordinal. [status] title (source)`, with a progress line above it; absent when no tasks were declared
```

and change the `## Plan` bullet to `— the full plan text: the document declared through the kcap-plans MCP tools when there is one, otherwise the server's best discovered candidate`. In "What To Do With The Output", step 2 becomes: `Compare each item against the summary and file list under "What's Done"; when a Tasks section is present, treat it as the checklist — a task still pending or in_progress is not done`.

- [ ] **Step 6: Run `ValidatePlanCommandTests`; expected PASS**

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C <worktree> add src/Capacitor.Cli.Core/Plans src/Capacitor.Cli.Core/Models.cs src/Capacitor.Cli/Commands/ValidatePlanCommand.cs src/Capacitor.Cli.Core/Resources/help-validate-plan.txt kcap/skills/validate-plan/SKILL.md test/Capacitor.Cli.Tests.Unit/Commands/ValidatePlanCommandTests.cs
/usr/bin/git -C <worktree> commit -m "Render the declared task list in validate-plan (#939)"
```

---

### Task 6: The SessionStart plans nudge

**Files:**
- Rename: `src/Capacitor.Cli/WorkItemsNudgeAvailability.cs` → `src/Capacitor.Cli/McpServerNudgeAvailability.cs` (class `McpServerNudgeAvailability`, `IsRegisteredFor(HarnessId, HarnessRegistry, string serverName, string? codexConfigPath = null)`)
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginInstaller.cs` (split `IsEffectivelyInstalled` into `EffectiveMcpJsonPath(settingsPath) → string?` plus the bool wrapper)
- Create: `src/Capacitor.Cli/PlansNudgeEmitter.cs`
- Modify: `src/Capacitor.Cli/WorkItemsNudgeEmitter.cs` (call the renamed gate with `"kcap-workitems"`)
- Modify: `src/Capacitor.Cli/HarnessNudgeEmitter.cs` (`Combine(params string?[] nudges)`)
- Modify: `src/Capacitor.Cli.Core/Config/ProfileConfig.cs` (`DisablePlansNudge`), `src/Capacitor.Cli/Commands/ConfigCommand.cs` (`disable_plans_nudge`), `src/Capacitor.Cli.Core/Resources/help-config.txt`
- Modify: the nine hook commands under `src/Capacitor.Cli/Commands/Harness/`
- Modify: `test/Capacitor.Cli.Tests.Unit/SessionStartMemory/WorkItemsNudgeTests.cs` (rename the gate calls)
- Create: `test/Capacitor.Cli.Tests.Unit/SessionStartMemory/PlansNudgeTests.cs`

**Interfaces:**
- Produces: `static class PlansNudgeEmitter { static string? Resolve(HarnessId, string? sessionId, bool optedOut, HarnessRegistry, string? codexConfigPath = null); static string? Build(string? sessionId); }`
- Produces: `ClaudePluginInstaller.EffectiveMcpJsonPath(string settingsPath) → string?`
- Produces: `HarnessNudgeEmitter.Combine(params string?[] nudges)`

- [ ] **Step 1: Write the failing tests**

`PlansNudgeTests.cs`:

```csharp
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class PlansNudgeEmitterTests {
    [TempHome] public required TempHome Home { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    [Test]
    public async Task Build_returns_null_for_a_missing_oversized_or_unsafe_session_id() {
        await Assert.That(PlansNudgeEmitter.Build(null)).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("  ")).IsNull();
        await Assert.That(PlansNudgeEmitter.Build(new string('a', 257))).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("abc`x`")).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("abc\ndef")).IsNull();
    }

    [Test]
    public async Task Build_is_two_sentences_naming_the_four_tools_and_the_session_id() {
        var nudge = PlansNudgeEmitter.Build("s1")!;

        await Assert.That(nudge).StartsWith("## Plans\n");
        await Assert.That(nudge).Contains("declare_plan_document");
        await Assert.That(nudge).Contains("set_plan_tasks");
        await Assert.That(nudge).Contains("update_plan_task");
        await Assert.That(nudge).Contains("get_plan");
        await Assert.That(nudge).Contains("`s1`");
        // Two sentences: exactly two full stops that end a sentence.
        var body = nudge["## Plans\n".Length..];
        await Assert.That(body.Split(". ").Length + (body.EndsWith('.') ? 0 : 1)).IsEqualTo(2);
    }

    static string CodexConfigWithPlans(TempDir tmp) =>
        tmp.CreateFile("config.toml", "[mcp_servers.kcap-plans]\ncommand = \"kcap\"\nargs = [\"mcp\", \"plans\"]\n");

    [Test]
    public async Task Resolve_returns_null_when_opted_out() {
        using var tmp = new TempDir();
        await Assert.That(PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: true, Harnesses, CodexConfigWithPlans(tmp))).IsNull();
    }

    [Test]
    public async Task Resolve_returns_the_nudge_when_codex_registers_kcap_plans() {
        using var tmp = new TempDir();
        var nudge = PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, CodexConfigWithPlans(tmp));
        await Assert.That(nudge).IsNotNull();
        await Assert.That(nudge!).Contains("`s1`");
    }

    [Test]
    public async Task Resolve_returns_null_when_codex_registers_only_workitems() {
        using var tmp = new TempDir();
        var config = tmp.CreateFile("config.toml", "[mcp_servers.kcap-workitems]\ncommand = \"kcap\"\nargs = [\"mcp\", \"workitems\"]\n");
        await Assert.That(PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, config)).IsNull();
    }
}

public class McpServerNudgeAvailabilityPlansTests {
    [TempHome] public required TempHome Home { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    [Test]
    public async Task Pi_extension_listing_plans_is_available_and_one_without_is_not() {
        var path = Harnesses.Of<PiHarness>().Paths.KcapMcpExtension;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, """const KCAP_MCP_SERVERS = ["review", "workitems", "plans"];""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-plans")).IsTrue();

        await File.WriteAllTextAsync(path, """const KCAP_MCP_SERVERS = ["review", "workitems"];""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-plans")).IsFalse();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-workitems")).IsTrue();
    }

    [Test]
    public async Task Claude_requires_the_server_in_the_installed_plugins_mcp_json() {
        // An enabled plugin whose materialized .mcp.json predates kcap-plans must not nudge toward it.
        var settings = Harnesses.Of<Capacitor.Cli.Core.Harness.Claude.ClaudeHarness>().Paths.UserSettings;
        var claudeHome = Path.GetDirectoryName(settings)!;
        var install = Path.Combine(claudeHome, "plugins", "cache", "kcap");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(Path.Combine(claudeHome, "plugins"));
        await File.WriteAllTextAsync(settings, """{"enabledPlugins":{"kcap@kcap":true}}""");
        await File.WriteAllTextAsync(Path.Combine(claudeHome, "plugins", "installed_plugins.json"),
            $$"""{"plugins":{"kcap@kcap":[{"scope":"user","installPath":"{{install.Replace("\\", "\\\\")}}"}]}}""");
        await File.WriteAllTextAsync(Path.Combine(install, ".mcp.json"), """{"mcpServers":{"kcap-workitems":{"command":"kcap","args":["mcp","workitems"]}}}""");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems")).IsTrue();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsFalse();

        await File.WriteAllTextAsync(Path.Combine(install, ".mcp.json"), """{"mcpServers":{"kcap-workitems":{},"kcap-plans":{"command":"kcap","args":["mcp","plans"]}}}""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsTrue();
    }
}
```

Also in `WorkItemsNudgeTests.cs`: replace `WorkItemsNudgeAvailability.IsRegisteredFor(HarnessId.X, Harnesses)` with `McpServerNudgeAvailability.IsRegisteredFor(HarnessId.X, Harnesses, "kcap-workitems")` and `WorkItemsNudgeAvailability.IsRegisteredFor(HarnessId.Codex, Harnesses, codexConfig)` with `McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Codex, Harnesses, "kcap-workitems", codexConfig)` (sed both forms; rename the test class to `McpServerNudgeAvailabilityTests`). Check the Claude test in that file (`Claude_without_an_effective_plugin_suppresses`) still holds — it seeds no plugin, so it stays false.

- [ ] **Step 2: Build; expected compile failure on the new names**

- [ ] **Step 3: Generalize the availability gate**

Rename the file and class; body:

```csharp
/// <summary>Is a kcap MCP server actually MATERIALIZED in the invoking harness's on-disk config?
/// A SessionStart nudge tells the agent to use tools that exist only if that server is registered,
/// so a stale install (an upgraded CLI whose harness config predates the server) must not be nudged
/// toward a tool it lacks. Reads the real config entry, not an ownership marker, so a removed,
/// disabled or malformed entry also suppresses. A config-level check, never a runtime probe. Fails
/// CLOSED: anything absent, disabled, malformed or unreadable suppresses.</summary>
static class McpServerNudgeAvailability {
    /// <param name="serverName">The registration name, e.g. <c>kcap-plans</c>.</param>
    /// <param name="codexConfigPath">Overrides the Codex <c>config.toml</c> path (test seam); null uses the default.</param>
    public static bool IsRegisteredFor(HarnessId harness, HarnessRegistry harnesses, string serverName, string? codexConfigPath = null) {
        try {
            return harness switch {
                // Claude loads the plugin's bundled .mcp.json, so the server is available exactly when
                // the plugin is effectively installed AND the copy Claude loads names it.
                HarnessId.Claude  => ClaudePluginInstaller.EffectiveMcpJsonPath(harnesses.Of<ClaudeHarness>().Paths.UserSettings) is { } mcpJson
                                  && JsonBlockHasServer(mcpJson, "mcpServers", serverName),
                HarnessId.Codex   => CodexHas(serverName, codexConfigPath ?? harnesses.Of<CodexHarness>().Paths.ConfigToml),
                HarnessId.Cursor  => JsonBlockHasServer(harnesses.Of<CursorHarness>().Paths.UserMcpJson, "mcpServers", serverName),
                HarnessId.Copilot => JsonBlockHasServer(harnesses.Of<CopilotHarness>().Paths.McpConfigJson, "mcpServers", serverName),
                HarnessId.Gemini  => JsonBlockHasServer(harnesses.Of<GeminiHarness>().Paths.SettingsJson, "mcpServers", serverName),
                HarnessId.Kiro    => JsonBlockHasServer(harnesses.Of<KiroHarness>().Paths.SettingsMcpJson, "mcpServers", serverName),
                HarnessId.OpenCode    => JsonBlockHasServer(harnesses.Of<OpenCodeHarness>().Paths.McpConfigJson, "mcp", serverName),
                HarnessId.Antigravity => JsonBlockHasServer(harnesses.Of<AntigravityHarness>().Paths.McpConfigJson, "mcpServers", serverName),
                HarnessId.Pi          => PiHas(harnesses, PiToken(serverName)),
                _ => false
            };
        } catch {
            return false;
        }
    }

    /// <summary>The Pi bridge lists servers by their <c>kcap mcp &lt;name&gt;</c> subcommand.</summary>
    static string PiToken(string serverName) =>
        serverName.StartsWith("kcap-", StringComparison.Ordinal) ? serverName["kcap-".Length..] : serverName;
```

`CodexHas(serverName, path)`, `PiHas(harnesses, token)` (compare elements to `$"\"{token}\""` / `$"'{token}'"`), `JsonBlockHasServer(path, blockKey, serverName)`: same bodies as today with the constant replaced by the parameter; keep `StripJsComments`. Delete the `ServerName` const.

In `ClaudePluginInstaller`: rename the body of `IsEffectivelyInstalled` into `public static string? EffectiveMcpJsonPath(string settingsPath)` returning `Path.Combine(installPath, ".mcp.json")` where it returned `true` (the per-scope loop) and `Path.Combine(installLocation, ".mcp.json")` at the directory-marketplace tail (return the path only when `File.Exists` holds, else null; every `return false` becomes `return null`), then `public static bool IsEffectivelyInstalled(string settingsPath) => EffectiveMcpJsonPath(settingsPath) is not null;`. The doc comment stays on `IsEffectivelyInstalled`; give the new method one line: "The `.mcp.json` Claude actually loads for the enabled kcap plugin, or null when the plugin is not effective."

`WorkItemsNudgeEmitter.Resolve`: `if (!McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-workitems", codexConfigPath)) return null;` and fix its doc comment's reference.

- [ ] **Step 4: The emitter**

`src/Capacitor.Cli/PlansNudgeEmitter.cs`:

```csharp
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>The SessionStart "plans" nudge: two sentences telling the agent to declare the plan
/// document and task list it works from through the kcap-plans MCP tools. Like the work-items
/// nudge it is a pure function of the session id, composed at the output layer after the
/// lease-gated fragments are decided, and gated on the server being materialized in the invoking
/// harness's config.</summary>
static class PlansNudgeEmitter {
    const int MaxSessionIdLength = 256;

    public static string? Resolve(HarnessId harness, string? sessionId, bool optedOut,
                                  HarnessRegistry harnesses, string? codexConfigPath = null) {
        if (optedOut) return null;
        if (!McpServerNudgeAvailability.IsRegisteredFor(harness, harnesses, "kcap-plans", codexConfigPath)) return null;
        return Build(sessionId);
    }

    public static string? Build(string? sessionId) {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var id = sessionId.Trim();
        if (id.Length > MaxSessionIdLength) return null;
        // Rendered verbatim inside a code span: a backtick or control char would break the span or
        // smuggle formatting, so such an id suppresses the nudge instead.
        foreach (var c in id)
            if (c == '`' || char.IsControl(c)) return null;

        return
            "## Plans\n" +
            "Plans and tasks are declared through the kcap-plans MCP tools: when you write or are handed a " +
            "plan, spec or design document, declare it with `declare_plan_document`; when a plan has discrete " +
            "steps, declare them with `set_plan_tasks` and record every status change with `update_plan_task`. " +
            $"After compaction `get_plan` returns the list, and if a tool cannot resolve the session, pass `session_id` (`{id}`) explicitly.";
    }
}
```

`HarnessNudgeEmitter.Combine`:

```csharp
    /// <summary>Joins SessionStart nudges (any may be null) into one blank-line-separated blob, so a
    /// delivery helper that carries a single nudge slot can carry them all.</summary>
    public static string? Combine(params string?[] nudges) {
        var kept = nudges.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return kept.Count == 0 ? null : string.Join("\n\n", kept);
    }
```

- [ ] **Step 5: The opt-out**

`ProfileConfig.cs`, after `DisableWorkItemsNudge`:

```csharp
    /// <summary>when true, kcap skips injecting the SessionStart plans nudge. Independent of the
    /// other SessionStart opt-outs.</summary>
    [JsonPropertyName("disable_plans_nudge")]
    public bool? DisablePlansNudge { get; init; }
```

`ConfigCommand.ApplySet`, after the workitems pair:

```csharp
            "disable_plans_nudge" when bool.TryParse(value, out var b) => profile with { DisablePlansNudge = b },
            "disable_plans_nudge" => throw new ArgumentException($"Invalid value for disable_plans_nudge: '{value}'. Must be true or false."),
```

`help-config.txt`, after the workitems line: `  disable_plans_nudge         Skip injecting the plans nudge at SessionStart (true/false)`.

- [ ] **Step 6: The nine hook sites**

At each site resolve the plans nudge with the same session id and opt-out shape, and fold it into the existing combine:

- Antigravity (`AntigravityHookCommand.cs` ~216): inside the `IsFirstInvocation(payload)` branch, `HarnessNudgeEmitter.Combine(WorkItemsNudgeEmitter.Resolve(...), PlansNudgeEmitter.Resolve(HarnessId.Antigravity, sessionId, activeProfile?.DisablePlansNudge is true, harnesses), HarnessNudgeEmitter.ResolveFragmentForHook(...))`.
- Gemini (~354), Pi (~177), Copilot (~322), Codex (~409): same three-argument `Combine` with the matching `HarnessId`.
- Kiro (~274, inside `ResolveNudges()`): same.
- OpenCode (~175): `var workItemsNudge = canConsumeFragment ? HarnessNudgeEmitter.Combine(WorkItemsNudgeEmitter.Resolve(HarnessId.OpenCode, ...), PlansNudgeEmitter.Resolve(HarnessId.OpenCode, sessionId, activeProfile?.DisablePlansNudge is true, harnesses)) : null;` (the harness nudge is combined on the next line already).
- Cursor (~524): `var plansNudge = PlansNudgeEmitter.Resolve(HarnessId.Cursor, sessionId, nudgeProfile?.DisablePlansNudge is true, harnesses);` and `HarnessNudgeEmitter.Combine(workItemsNudge, plansNudge, harnessNudge)`.
- Claude (~774): `var plansNudge = PlansNudgeEmitter.Resolve(HarnessId.Claude, sessionId, activeProfile?.DisablePlansNudge is true, harnesses);` and pass it to `BuildEnvelope(... workItemsNudge, plansNudge, harnessNudge)`. Delete the stale comment above `workItemsNudge` claiming Claude's gate is always satisfied.

- [ ] **Step 7: Build the whole solution and run the nudge classes + every `*HookCommand*` test class**

Run: `dotnet build Capacitor.slnx 2>&1 | grep -E 'warning|error' | head` — expected none.
Run the CLI suite filtered on `PlansNudgeEmitterTests`, `McpServerNudgeAvailabilityPlansTests`, `WorkItemsNudgeEmitterTests`, `McpServerNudgeAvailabilityTests`, `WorkItemsNudgeRenderCompositionTests`, `ConfigCommandTests`, then the whole CLI suite. Expected PASS.

- [ ] **Step 8: Commit**

```bash
/usr/bin/git -C <worktree> add -A src/Capacitor.Cli src/Capacitor.Cli.Core/Config/ProfileConfig.cs src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginInstaller.cs src/Capacitor.Cli.Core/Resources/help-config.txt test/Capacitor.Cli.Tests.Unit/SessionStartMemory
/usr/bin/git -C <worktree> commit -m "Nudge agents to declare plans at session start (#939)"
```

---

### Task 7: The `kcap:plans` skill

**Files:**
- Create: `kcap/skills/plans/SKILL.md`
- Modify: `src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs` (`SourceNames` gains `"plans"` after `"work-items"`)
- Modify: `src/Capacitor.Cli.Core/Resources/help-plugin.txt` (the `kcap-{…}` list gains `plans`)
- Modify: `test/Capacitor.Cli.Core.Tests.Unit/AgentsSkillsInstallerTests.cs` (mirror list gains `"plans"`)

- [ ] **Step 1: Add `"plans"` to the test's mirror list; run `AgentsSkillsInstallerTests`; expected FAIL** (installer list and help text lack it; `SourceNames_covers_every_shipped_skill` fails once the folder exists).

- [ ] **Step 2: Write the skill**

`kcap/skills/plans/SKILL.md`:

```markdown
---
name: plans
description: >-
  This skill should be used whenever you write or read a plan, spec or design
  document, when you start executing a plan, and whenever a task list, todo
  list or checklist comes up — yours or the user's. It says how to record the
  plan and its tasks in Kurrent Capacitor through the `kcap mcp plans` MCP
  tools, so progress shows in the session view and survives context
  compaction.
---

# Plans — declaring the document and its tasks

Capacitor keeps its own record of the plan a session executes: the document,
the ordered task list, and each task's status. It is written only by you,
through the `kcap-plans` MCP tools; nothing infers it. Three rules:

1. **When you write or are handed a plan, spec or design document, declare it.**
   Call `declare_plan_document` with its `kind` (`plan`, `spec` or `design`)
   and `path`. The file is read locally, hashed, and keyed against the
   repository root; declaring the same file again from a later session lands
   on the same plan. When a plan implements a spec, or a spec refines a design,
   pass the other file as `argues_from` so both attach to one plan.
2. **When a plan has discrete steps, declare them and update each transition.**
   Call `set_plan_tasks` with the whole ordered list (`title`, optional
   `task_id`, `status`, `note`); re-send the whole list when the steps change,
   carrying the `task_id`s you were given. Call `update_plan_task` every time a
   task starts, finishes or is skipped, by `task_id` or 1-based `ordinal`, with
   `status` `pending`, `in_progress`, `completed` or `skipped` and a `note`
   when the reason matters. After compaction, `get_plan` returns the list.
3. **Keep whatever ledger your own workflow asks for as well.** These tools
   replace the harness's task list, not your notes or any file another
   workflow tells you to maintain.

## Tool reference

| Tool | Required args | Purpose |
|---|---|---|
| `declare_plan_document` | `kind`, `path` (+ `argues_from`, `work_item_id`, `session_id`) | Declare the document; returns `plan_id`, `document_key`, `created`. |
| `set_plan_tasks` | `tasks` (+ `plan_id`, `session_id`) | Replace the declared task list; returns tasks with ids and ordinals. |
| `update_plan_task` | `status` and one of `task_id` / `ordinal` (+ `note`, `plan_id`) | Record one transition; the result names the plan. |
| `get_plan` | — (+ `plan_id`, `session_id`) | Documents, tasks with status and source, progress. |

Without `plan_id` every tool acts on the session's current plan — the one it
most recently wrote to; a session on no plan gets one created by
`set_plan_tasks` and an empty result from `get_plan`. If a tool cannot resolve
the session, pass `session_id` explicitly.

## Requirements

Requires `kcap login`. The `kcap-plans` MCP server is registered for every
supported harness by `kcap setup` and `kcap plugin install`.
```

- [ ] **Step 3: Wire the installer and help**

`AgentsSkillsInstaller.SourceNames`: insert `"plans",` after `"work-items",`.
`help-plugin.txt` line 158: `validate-plan,review-flows,agent-flows,work-items,plans,guided-tour,suggest-review-flow}/.`

- [ ] **Step 4: Run `AgentsSkillsInstallerTests`, `PluginCommandSkillsTests`, `PluginCommandVendorSkillsTests`, `UninstallCommandTests`; expected PASS**

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C <worktree> add kcap/skills/plans/SKILL.md src/Capacitor.Cli.Core/AgentsSkillsInstaller.cs src/Capacitor.Cli.Core/Resources/help-plugin.txt test/Capacitor.Cli.Core.Tests.Unit/AgentsSkillsInstallerTests.cs
/usr/bin/git -C <worktree> commit -m "Ship the kcap:plans skill (#939)"
```

---

### Task 8: README, help text and change notes

**Files:**
- Modify: `README.md`, `src/Capacitor.Cli.Core/Resources/help-mcp.txt`, `src/Capacitor.Cli.Core/Resources/help-usage.txt`, `docs/CHANGES.md`

- [ ] **Step 1: README**

1. After the `kcap mcp workitems` getting-started paragraph (line ~251) add:

   > The `kcap mcp plans` stdio server lets agents declare the plan, spec or design document a session works from and the plan's task list — `declare_plan_document`, `set_plan_tasks`, `update_plan_task`, `get_plan` — so progress shows in the session view and the list survives context compaction. `kcap setup` / `kcap plugin install` **register it for every supported harness** alongside `kcap-workitems`. See the [Plans MCP server](#plans-mcp-server-for-agents) section for details.

2. After the SessionStart work-items nudge bullet (line ~269) add a **SessionStart plans nudge** bullet: same harness list, `## Plans` block, two sentences, names the four tools, shown only when `kcap-plans` is registered for the harness (for Claude: named in the installed plugin's `.mcp.json`), opt out with `disable_plans_nudge`.

3. Plan validation section (~437): after the existing paragraph add: "When the session's plan was declared through the [plans MCP tools](#plans-mcp-server-for-agents), the declared plan document leads the `## Plan` section and a `## Tasks` section lists the declared tasks — `ordinal. [status] title (source)` with a progress line — between the plan and `## What's Done`."

4. After the Work items MCP server section (before `### Analytics MCP server`) add `### Plans MCP server (for agents)` with the `kcap mcp plans` code block, one paragraph on what it records and where it is registered, the four tools as bullets (args and returns, the resolve-then-post note for `update_plan_task`, the empty result for `get_plan`), the `current` plan rule, the 256 KB snapshot cap and repo-relative path keying, and the session-id defaulting sentence copied from the work-items section.

5. Codex skills table: add `| \`kcap-plans\` | \`kcap mcp plans\` | Declare the plan document and task list a session executes |` after the work-items row, and in the paragraph below add "`kcap-plans` writes through `kcap mcp plans` and defaults the session like `kcap-work-items`."

6. Replace the four "six kcap MCP servers" with "seven kcap MCP servers".

- [ ] **Step 2: help-mcp.txt**

Add `  plans                   Start the MCP plans server (stdio) — declare the plan document and task list a session executes.` to the subcommand list; add a `mcp plans:` section after `mcp workitems:` listing the four tools one line each plus "Requires `kcap login`. Registered for every harness by `kcap setup` / `kcap plugin install`, like `kcap-workitems`."; add `kcap-plans` to the INSTALL sentence's list.

- [ ] **Step 3: help-usage.txt**

After the `mcp workitems` line: `  mcp plans                          Declare a session's plan document and task list`.

- [ ] **Step 4: docs/CHANGES.md** — add at the top, under the header:

```markdown
## Plans are declared from the CLI

A plan document is read by the CLI, not sent for the server to fetch: the server keys a document
on its repo-relative path and the workspace root exactly as discovery keys a written file, so the
tool sends the path relative to the git top level and only attaches content at or under the
server's 256 KB transport cap; above it the document is declared by hash alone and the tool result
says so. `update_plan_task` resolves the session's current plan before it posts when no `plan_id` is
given, because the update route answers with the task alone and every result has to name the plan
it acted on. The SessionStart nudge for Claude reads the installed plugin's `.mcp.json` rather than
assuming the bundled copy: a plugin installed before `kcap-plans` existed carries no such server,
and a nudge toward a tool the session lacks is worse than none.
```

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C <worktree> add README.md src/Capacitor.Cli.Core/Resources/help-mcp.txt src/Capacitor.Cli.Core/Resources/help-usage.txt docs/CHANGES.md
/usr/bin/git -C <worktree> commit -m "Document the plans MCP server and nudge (#939)"
```

---

### Task 9: Whole-tree verification

- [ ] **Step 1: Build everything, warnings clean**

Run: `dotnet build Capacitor.slnx 2>&1 | grep -E 'warning|error' | head`
Expected: nothing.

- [ ] **Step 2: AOT publish, no IL warnings**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: nothing.

- [ ] **Step 3: Linear-id check**

Run: `bash scripts/check-linear-ids.sh`
Expected: exit 0, no output.

- [ ] **Step 4: Unit suites**

Run each of `test/Capacitor.Cli.Core.Tests.Unit`, `test/Capacitor.Cli.Tests.Unit`, `test/Capacitor.Cli.Daemon.Tests.Unit` with `dotnet run --project … --no-build` (build first). Expected: green; a daemon timing flake is re-run alone before blaming the diff.

- [ ] **Step 5: Commit the plan document**

```bash
/usr/bin/git -C <worktree> add docs/superpowers/plans/2026-09-15-ai2778-plan-ledger-cli.md
/usr/bin/git -C <worktree> commit -m "Add the plan-ledger CLI implementation plan (#939)"
```
