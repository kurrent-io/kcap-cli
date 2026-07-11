using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

[NotInParallel("HomeEnvVarMutation")]
public class ClaudeHookCommandTests {
    const string Sid = "9dc2775376454e4691ecc2d69973c152";

    [Test]
    public async Task session_start_posts_to_session_start_route() {
        using var fx = new Fixture();
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-start");
    }

    // AI-701: the session-start payload gains a best-effort workspace_root (the git repo root
    // for cwd), used server-side by plan-artifact discovery. Fail-open: a cwd with no
    // discoverable .git entry (e.g. "/tmp") must omit the field entirely rather than send null.
    [Test]
    public async Task session_start_includes_workspace_root_when_cwd_is_inside_a_git_repo() {
        var tmp = Directory.CreateTempSubdirectory("kcap-claude-hook-git-");
        try {
            Directory.CreateDirectory(Path.Combine(tmp.FullName, ".git"));
            var nested = Path.Combine(tmp.FullName, "nested", "dir");
            Directory.CreateDirectory(nested);

            using var fx = new Fixture();
            await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"{{nested.Replace("\\", "\\\\")}}"}""");

            var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|"));
            var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
            await Assert.That(body!["workspace_root"]?.GetValue<string>()).IsEqualTo(tmp.FullName);
        } finally {
            tmp.Delete(recursive: true);
        }
    }

    [Test]
    public async Task session_start_omits_workspace_root_when_cwd_has_no_git_repo() {
        using var fx = new Fixture();
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","cwd":"/tmp"}""");

        var posted = fx.Sent.Single(s => s.StartsWith("/hooks/session-start|"));
        var body   = JsonNode.Parse(posted[(posted.IndexOf('|') + 1)..]);
        await Assert.That(body!["workspace_root"]).IsNull();
    }

    // Covers the auth-hang case from the spec: the hard cap must beat an
    // uncancellable hang (e.g. TokenStore.RefreshAsync's untimed HttpClient.PostAsync).
    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        var inner = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(10)); return 42; });
        var sw    = System.Diagnostics.Stopwatch.StartNew();
        var exit  = await ClaudeHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var exit = await ClaudeHookCommand.WithHardCap(Task.FromResult(7), TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    [Test]
    public async Task session_end_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","reason":"other"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
        await Assert.That(content).Contains("ended_at");
    }

    [Test]
    public async Task session_end_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // processStart in the recent past leaves a small remaining budget.
        var exit = await fx.HandleAsync(
            $$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(15)); // did not wait the full 30s
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task session_end_on_4xx_is_not_spooled() {
        using var fx = new Fixture(HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task session_start_on_failure_is_spooled_with_minimal_body() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$"""{"hook_event_name":"SessionStart","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp","source":"startup"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-start\"");
        await Assert.That(JsonNode.Parse(JsonNode.Parse(content.Split('\n')[0])!["body"]!.GetValue<string>())!["session_id"]!.GetValue<string>())
            .IsEqualTo(Sid);
    }

    [Test]
    public async Task pending_backlog_is_drained_on_next_hook_when_server_up() {
        using var fx = new Fixture(); // 200 OK
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        // A fresh, unrelated stop hook with the server up flushes the backlog.
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("session-end"); // replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();          // delivered + cleaned
    }

    [Test]
    public async Task current_session_start_replays_before_its_session_end() {
        using var fx = new Fixture();
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var endIdx   = fx.RouteOrder.IndexOf("session-end");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(endIdx).IsGreaterThan(startIdx);
    }

    // CRITICAL 1: bound client creation. If CreateAuthenticatedClientAsync hangs (untimed
    // /auth/config GET or token refresh during an outage) past the hook budget, the lifecycle
    // event must still be spooled — spooling is a local disk write that needs no client.
    [Test]
    public async Task session_end_spooled_when_client_creation_exceeds_budget() {
        using var fx = new Fixture();
        // Slow factory: never completes within the cap (30s) so the budget elapses first.
        Func<Task<(HttpClient, AuthStatus)>> slowFactory = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => (new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);

        // processStart ~13.4s in the past → session-end remaining = 15 - 13.4 - 1.5 ≈ 0.1s cap.
        var processStart = System.Diagnostics.Stopwatch.GetTimestamp()
                         - (long)(13.4 * System.Diagnostics.Stopwatch.Frequency);

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await ClaudeHookCommand.HandleWithDeps(
            fx.Spool, processStart, "http://localhost",
            new StringReader($$"""{"hook_event_name":"SessionEnd","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}"""),
            updateCheckTask: null, clientFactory: slowFactory);
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(10)); // well under the 15s ceiling, not the 30s factory
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"session-end\"");
    }

    [Test]
    public async Task create_client_within_budget_returns_null_when_factory_slower_than_cap() {
        Func<Task<(HttpClient, AuthStatus)>> slow = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => (new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);
        var sw     = System.Diagnostics.Stopwatch.StartNew();
        var result = await ClaudeHookCommand.CreateClientWithinBudgetAsync(slow, TimeSpan.FromMilliseconds(50));
        sw.Stop();
        await Assert.That(result).IsNull();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task create_client_within_budget_returns_client_when_factory_fast() {
        var made   = new HttpClient();
        var result = await ClaudeHookCommand.CreateClientWithinBudgetAsync(() => Task.FromResult((made, AuthStatus.Ok)), TimeSpan.FromSeconds(2));
        await Assert.That(result).IsNotNull();
        await Assert.That(ReferenceEquals(result!.Value.Client, made)).IsTrue();
        result.Value.Client.Dispose();
    }

    const string AgentId = "a1b2c3d4";

    [Test]
    public async Task subagent_stop_on_5xx_is_spooled_and_returns_zero() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError);
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(exit).IsEqualTo(0);
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"subagent-stop\"");
    }

    [Test]
    public async Task subagent_stop_against_hung_server_is_spooled_within_budget() {
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromSeconds(30); // server hangs past the bounded attempt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var exit = await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5)); // did not wait the full 30s
        await Assert.That(fx.SpoolFiles.Any()).IsTrue();
    }

    [Test]
    public async Task subagent_stop_on_4xx_is_not_spooled() {
        using var fx = new Fixture(HttpStatusCode.BadRequest);
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task subagent_stop_without_agent_id_is_not_spooled() {
        // No agent_id → no SubagentCompleted to deliver → unchanged shared-path behavior (no spool).
        using var fx = new Fixture(); // OK
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task spooled_subagent_stop_is_replayed_on_next_hook() {
        using var fx = new Fixture(); // server up
        fx.Spool.Append(Sid, "subagent-stop", $$"""{"session_id":"{{Sid}}","agent_id":"{{AgentId}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.RouteOrder).Contains("subagent-stop"); // drained + replayed
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();           // delivered + cleaned
    }

    [Test]
    public async Task replayed_session_end_with_generate_whats_done_is_handled() {
        // Server returns generate_whats_done:false for the replayed session-end (set false to avoid process spawn).
        using var fx = new Fixture();
        fx.RespondJson = """{"generate_whats_done":false}""";
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
    }

    [Test]
    public async Task subagent_stop_spooled_when_client_creation_exceeds_budget() {
        using var fx = new Fixture();
        Func<Task<(HttpClient, AuthStatus)>> slowFactory = () =>
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => (new HttpClient(), AuthStatus.Ok), TaskScheduler.Default);
        // processStart ~3.4s in the past → subagent-stop remaining = 5 - 3.4 - 1.5 ≈ 0.1s cap.
        var processStart = System.Diagnostics.Stopwatch.GetTimestamp()
                         - (long)(3.4 * System.Diagnostics.Stopwatch.Frequency);
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await ClaudeHookCommand.HandleWithDeps(
            fx.Spool, processStart, "http://localhost",
            new StringReader($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}"""),
            updateCheckTask: null, clientFactory: slowFactory);
        sw.Stop();
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        var content = await File.ReadAllTextAsync(files[0]);
        await Assert.That(content).Contains("\"route\":\"subagent-stop\"");
    }

    [Test]
    public async Task current_session_start_replays_before_subagent_stop() {
        using var fx = new Fixture(); // server up
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");
        var startIdx = fx.RouteOrder.IndexOf("session-start");
        var stopIdx  = fx.RouteOrder.IndexOf("subagent-stop");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(stopIdx).IsGreaterThan(startIdx);
    }

    [Test]
    public async Task subagent_stop_spooled_not_posted_when_current_session_backlog_remains() {
        using var fx = new Fixture(HttpStatusCode.InternalServerError); // drain fails transiently → backlog remains
        fx.Spool.Append(Sid, "session-start", $$"""{"session_id":"{{Sid}}"}""");

        await fx.HandleAsync($$"""{"hook_event_name":"SubagentStop","session_id":"{{Sid}}","agent_id":"{{AgentId}}","transcript_path":"/none","cwd":"/tmp"}""");

        // The drain attempted the stranded session-start (and failed transiently, leaving backlog).
        await Assert.That(fx.RouteOrder).Contains("session-start");
        // Ordering guard fired: the fresh subagent-stop was spooled, NOT posted — so it never
        // appears in RouteOrder. (Without the guard it would be POSTed before this session's
        // stranded session-start is delivered.)
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-stop");
        // ...and it is durably spooled.
        var all = string.Concat(fx.SpoolFiles.Select(File.ReadAllText));
        await Assert.That(all).Contains("\"route\":\"subagent-stop\"");
    }

    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(Path.GetTempPath(), $"kcap-claude-hook-{Guid.NewGuid():N}");
        readonly string _spoolPath;
        public List<string> Sent { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool Spool { get; }
        public HttpClient Client { get; }
        public TimeSpan HoldOnPost { get; set; } = TimeSpan.Zero;
        public string? RespondJson { get; set; }
        readonly HttpStatusCode _postStatus;

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            _spoolPath  = Path.Combine(_tmpHome, "spool");
            _postStatus = postStatus;
            Spool = new HookSpool(_spoolPath);
            Client = new HttpClient(new StubHandler(async (req, ct) => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/")) RouteOrder.Add(path.Replace("/hooks/", ""));
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (HoldOnPost > TimeSpan.Zero) await Task.Delay(HoldOnPost, ct);
                var resp = new HttpResponseMessage(_postStatus);
                if (RespondJson is not null) resp.Content = new System.Net.Http.StringContent(RespondJson, System.Text.Encoding.UTF8, "application/json");
                return resp;
            }));
        }

        public Task<int> HandleAsync(string stdin, long processStart = 0) =>
            ClaudeHookCommand.HandleCore(Client, AuthStatus.Ok, Spool, processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart,
                "http://localhost", new StringReader(stdin));

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath) : [];

        public void Dispose() { Client.Dispose(); try { Directory.Delete(_tmpHome, true); } catch { } }
    }

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => impl(r, ct);
    }
}
