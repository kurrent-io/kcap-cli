using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpPlansServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A throwaway repo: a `.git` directory at the root so FindRoot recognises it, and a
    /// document under docs/.</summary>
    (TempDirHandle Root, string DocPath) SeedRepo(string content = "# Plan\n\n1. do it\n") {
        var root = Tmp.CreateDir("repo");
        root.CreateDir(".git");
        var doc = root.CreateDir("docs").CreateFile("plan.md", content);
        return (root, doc);
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
        await Assert.That(byName["get_plan"].InputSchema.Required.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Tasks_array_declares_object_items() {
        // An `array` with no `items` is incomplete JSON Schema: a strict client can reject it and a
        // model has to guess the element type.
        var tasks = McpPlansServer.BuildToolsList().Single(t => t.Name == "set_plan_tasks").InputSchema.Properties["tasks"];
        await Assert.That(tasks.Type).IsEqualTo("array");
        await Assert.That(tasks.Items).IsNotNull();
        await Assert.That(tasks.Items!.Type).IsEqualTo("object");
    }

    [Test]
    public async Task No_tool_advertises_a_server_owned_source_argument() {
        // The server stamps `mcp` as the source of every write; an advertised argument would be
        // ignored at best and a spoofing surface at worst.
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
        var args = new JsonObject { ["session_id"] = "s1", ["kind"] = "plan", ["path"] = doc };
        var d = McpPlansServer.BuildDeclaration(args, cwd: root, repoRoot: root);

        await Assert.That(d.Body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(d.Body["kind"]!.GetValue<string>()).IsEqualTo("plan");
        await Assert.That(d.Body["path"]!.GetValue<string>()).IsEqualTo("docs/plan.md");
        await Assert.That(d.Body["workspace_root"]!.GetValue<string>()).IsEqualTo(root.Path);
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
        await Assert.That(d.Body["kind"]!.GetValue<string>()).IsEqualTo("spec");
    }

    [Test]
    public async Task Declaration_omits_the_snapshot_above_the_cap_and_still_hashes() {
        var big = new string('x', McpPlansServer.MaxSnapshotBytes + 1);
        var (root, _) = SeedRepo(big);
        var d = McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/plan.md"}"""), cwd: root, repoRoot: root);

        await Assert.That(d.Body.ContainsKey("content")).IsFalse();
        await Assert.That(d.SnapshotAttached).IsFalse();
        await Assert.That(d.ContentBytes).IsEqualTo((long)McpPlansServer.MaxSnapshotBytes + 1);
        await Assert.That(d.Body["content_hash"]!.GetValue<string>().Length).IsEqualTo(64);
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
        await Assert.That(d.WorkspaceRoot).IsNull();
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
    public async Task Declaration_refuses_a_path_outside_the_project_before_reading_it() {
        var (root, _) = SeedRepo();
        var secret = Tmp.CreateFile("secret.txt", "hunter2");

        var absolute = new JsonObject { ["session_id"] = "s1", ["kind"] = "plan", ["path"] = secret };
        await Assert.That(() => McpPlansServer.BuildDeclaration(absolute, root, root))
            .Throws<ArgumentException>().WithMessageContaining("outside the project root");

        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"../secret.txt"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("outside the project root");
    }

    [Test]
    public async Task Declaration_refuses_a_symlink_that_leaves_the_project() {
        // Creating a symlink needs a privilege the Windows CI runner lacks.
        if (OperatingSystem.IsWindows()) return;

        var (root, _) = SeedRepo();
        var secret = Tmp.CreateFile("secret.txt", "hunter2");
        File.CreateSymbolicLink(root.PathTo("docs", "link.md"), secret);

        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/link.md"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("links outside the project root");

        // A linked directory between the root and the file is caught the same way.
        var elsewhere = Tmp.CreateDir("elsewhere");
        elsewhere.CreateFile("plan.md", "x");
        Directory.CreateSymbolicLink(root.PathTo("linked"), elsewhere.Path);

        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"linked/plan.md"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("links outside the project root");
    }

    [Test]
    public async Task Declaration_refuses_a_file_link_whose_target_sits_under_a_linked_directory() {
        if (OperatingSystem.IsWindows()) return;

        // docs/plan.md -> linked/secret.txt, and linked/ -> an outside directory: the file link's
        // resolved target is lexically inside the repo, but the directory it names is not.
        var (root, _) = SeedRepo();
        var outside = Tmp.CreateDir("outside");
        outside.CreateFile("secret.txt", "hunter2");
        Directory.CreateSymbolicLink(root.PathTo("linked"), outside.Path);
        File.CreateSymbolicLink(root.PathTo("docs", "plan.md.link"), root.PathTo("linked", "secret.txt"));

        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/plan.md.link"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("links outside the project root");
    }

    [Test]
    public async Task Declaration_refuses_a_relative_link_target_that_climbs_out_of_a_linked_directory() {
        if (OperatingSystem.IsWindows()) return;

        // docs2/ -> outside/subdir, and outside/subdir/plan.md -> ../secret.txt: normalized against
        // the lexical parent the `..` lands inside the repo; against the real one it lands outside.
        var (root, _) = SeedRepo();
        var outside = Tmp.CreateDir("outside");
        var subdir  = outside.CreateDir("subdir");
        outside.CreateFile("secret.txt", "hunter2");
        Directory.CreateSymbolicLink(root.PathTo("docs2"), subdir.Path);
        File.CreateSymbolicLink(subdir.PathTo("plan.md"), "../secret.txt");

        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs2/plan.md"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("links outside the project root");
    }

    [Test]
    public async Task Declaration_refuses_a_link_target_whose_dotdot_climbs_out_of_a_linked_directory() {
        if (OperatingSystem.IsWindows()) return;

        // plan.md -> linked/../secret.txt with linked/ -> outside/subdir: collapsed lexically the
        // target is the in-repo decoy, but the kernel follows `linked` first and lands outside.
        var (root, _) = SeedRepo();
        var outside = Tmp.CreateDir("outside");
        outside.CreateDir("subdir");
        outside.CreateFile("secret.txt", "hunter2");
        root.CreateFile("secret.txt", "decoy");
        Directory.CreateSymbolicLink(root.PathTo("linked"), outside.PathTo("subdir"));
        File.CreateSymbolicLink(root.PathTo("plan.md"), "linked/../secret.txt");

        await Assert.That(() => McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"plan.md"}"""), root, root))
            .Throws<ArgumentException>().WithMessageContaining("links outside the project root");
    }

    [Test]
    public async Task Declaration_accepts_links_that_stay_inside_the_project() {
        if (OperatingSystem.IsWindows()) return;

        var (root, _) = SeedRepo("# Plan\n");
        File.CreateSymbolicLink(root.PathTo("docs", "alias.md"), root.PathTo("docs", "plan.md"));
        File.CreateSymbolicLink(root.PathTo("docs", "relative.md"), "plan.md");
        File.CreateSymbolicLink(root.PathTo("docs", "via-dotdot.md"), "../docs/plan.md");
        Directory.CreateSymbolicLink(root.PathTo("linked-docs"), root.PathTo("docs"));

        foreach (var path in new[] { "docs/alias.md", "docs/relative.md", "docs/via-dotdot.md", "linked-docs/plan.md" }) {
            var d = McpPlansServer.BuildDeclaration(new JsonObject { ["session_id"] = "s1", ["kind"] = "plan", ["path"] = path }, root, root);

            await Assert.That(d.Body["path"]!.GetValue<string>()).IsEqualTo(path);
            await Assert.That(d.Body["content"]!.GetValue<string>()).IsEqualTo("# Plan\n");
        }
    }

    [Test]
    public async Task Declaration_without_a_repo_is_bounded_by_the_project_directory() {
        var dir    = Tmp.CreateDir("loose");
        var secret = Tmp.CreateFile("secret.txt", "hunter2");
        var args   = new JsonObject { ["session_id"] = "s1", ["kind"] = "plan", ["path"] = secret };

        await Assert.That(() => McpPlansServer.BuildDeclaration(args, dir.Path, repoRoot: null))
            .Throws<ArgumentException>().WithMessageContaining("outside the project root");
    }

    [Test]
    public async Task Declaration_of_invalid_utf8_goes_by_hash_only() {
        var (root, doc) = SeedRepo();
        byte[] bytes = [0xff, 0xfe, (byte)'a'];
        await File.WriteAllBytesAsync(doc, bytes);

        var d = McpPlansServer.BuildDeclaration(Args("""{"session_id":"s1","kind":"plan","path":"docs/plan.md"}"""), root, root);

        await Assert.That(d.Body.ContainsKey("content")).IsFalse();
        await Assert.That(d.SnapshotAttached).IsFalse();
        await Assert.That(d.SnapshotOmitted!).Contains("UTF-8");
        await Assert.That(d.Body["content_hash"]!.GetValue<string>()).IsEqualTo(Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Test]
    public async Task Wire_path_is_root_relative_with_forward_slashes_or_absolute_when_outside() {
        var root = Tmp.CreateDir("r").Path;
        await Assert.That(McpPlansServer.WirePath(Path.Combine(root, "a", "b.md"), root)).IsEqualTo("a/b.md");
        var outside = Path.Combine(Tmp.Path, "elsewhere.md");
        await Assert.That(McpPlansServer.WirePath(outside, root)).IsEqualTo(outside);
        await Assert.That(McpPlansServer.WirePath(outside, null)).IsEqualTo(outside);
        await Assert.That(McpPlansServer.WirePath(root, root)).IsEqualTo(root);
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
        // The server owns the vocabulary; its coded rejection names the real reason.
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

    // Resolutions.None: these tests exercise routing, not profile selection.
    McpPlansServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), NoTelemetry.Startup,
            new WorkingDirectory(AppContext.BaseDirectory));

    async Task<(ScriptedHandler Handler, string Response)> DispatchAsync(
            string toolName, string argsJson, ScriptedHandler? handler = null, string? cwd = null, string? repoRoot = null) {
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
        await Assert.That(sent["workspace_root"]!.GetValue<string>()).IsEqualTo(root.Path);

        await Assert.That(IsError(response)).IsFalse();
        var result = JsonNode.Parse(ResultText(response))!.AsObject();
        await Assert.That(result["plan_id"]!.GetValue<string>()).IsEqualTo("p1");
        await Assert.That(result["snapshot_attached"]!.GetValue<bool>()).IsTrue();
        await Assert.That(result["content_bytes"]!.GetValue<long>()).IsEqualTo(7L);
        await Assert.That(result["path"]!.GetValue<string>()).IsEqualTo("docs/plan.md");
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
        await Assert.That(result["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(result["tasks"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(result["progress"]!["total_known"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Dispatch_of_a_malformed_call_never_reaches_the_network() {
        // Local validation fails BEFORE a request is built — otherwise a malformed call would hit
        // some other route and the error would describe the wrong thing.
        var (h, response) = await DispatchAsync("set_plan_tasks", """{"session_id":"s1","tasks":"nope"}""");
        await Assert.That(h.Calls.Count).IsEqualTo(0);
        await Assert.That(IsError(response)).IsTrue();
    }

    [Test]
    public async Task Response_ok_reads_false_from_an_unknown_tool_and_true_from_a_success() {
        // Telemetry reads ok from the JSON-RPC result, not from whether dispatch returned a string.
        var (_, unknown) = await DispatchAsync("not_a_real_tool", "{}");
        await Assert.That(McpTelemetry.ResponseOk(unknown)).IsFalse();

        var (_, ok) = await DispatchAsync("get_plan", """{"plan_id":"p1"}""");
        await Assert.That(McpTelemetry.ResponseOk(ok)).IsTrue();
    }
}
