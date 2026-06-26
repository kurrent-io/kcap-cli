using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

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

    [Test]
    public async Task replayed_session_end_with_generate_whats_done_is_handled() {
        // Server returns generate_whats_done:false for the replayed session-end (set false to avoid process spawn).
        using var fx = new Fixture();
        fx.RespondJson = """{"generate_whats_done":false}""";
        fx.Spool.Append(Sid, "session-end", $$"""{"session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"Stop","session_id":"{{Sid}}","transcript_path":"/none","cwd":"/tmp"}""");
        await Assert.That(fx.SpoolFiles.Any()).IsFalse();
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
            ClaudeHookCommand.HandleCore(Client, Spool, processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart,
                "http://localhost", new StringReader(stdin));

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath) : [];

        public void Dispose() { Client.Dispose(); try { Directory.Delete(_tmpHome, true); } catch { } }
    }

    sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => impl(r, ct);
    }
}
