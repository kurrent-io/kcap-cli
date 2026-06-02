using System.Net;
using System.Text.Json.Nodes;
using Kapacitor.Cli;
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit.Cursor;

// Several tests here read HOME-derived paths (DisabledSessions marker dir,
// PathHelpers.HomeDirectory injection into the outgoing payload) and one
// mutates KAPACITOR_AGENT_ID. Serialise against every other test that
// mutates HOME so a racing HOME-setter from PluginCommand* tests can't
// land our marker writes in the wrong directory.
[NotInParallel("HomeEnvVarMutation")]
public class CursorHookCommandTests {
    const string Sid = "8c3276c2c8f743ce98898c2becf5240a";
    [Test]
    public async Task malformed_stdin_returns_zero() {
        using var fx = new Fixture();
        var exit = await fx.HandleAsync("not a json payload");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task missing_hook_event_name_returns_zero() {
        using var fx = new Fixture();
        var exit = await fx.HandleAsync("""{"session_id":"abc"}""");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task session_id_is_normalised_dashless_in_outgoing_payload() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"8c3276c2-c8f7-43ce-9889-8c2becf5240a"}""");
        var sent = fx.SentToHook("session-start/cursor");
        await Assert.That(JsonNode.Parse(sent)!["session_id"]!.GetValue<string>())
            .IsEqualTo("8c3276c2c8f743ce98898c2becf5240a");
    }

    [Test]
    [NotInParallel("KapacitorAgentIdEnvVar")]
    public async Task home_dir_and_agent_host_id_are_injected() {
        Environment.SetEnvironmentVariable("KAPACITOR_AGENT_ID", "host-42");
        try {
            using var fx = new Fixture();
            await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
            var sent = fx.SentToHook("session-start/cursor");
            var node = JsonNode.Parse(sent)!;
            await Assert.That(node["home_dir"]?.GetValue<string>()).IsNotNull();
            await Assert.That(node["agent_host_id"]?.GetValue<string>()).IsEqualTo("host-42");
        } finally {
            Environment.SetEnvironmentVariable("KAPACITOR_AGENT_ID", null);
        }
    }

    [Test]
    public async Task disabled_session_suppresses_POST() {
        var sid = Guid.NewGuid().ToString("N");
        DisabledSessions.Mark(sid);
        try {
            using var fx = new Fixture();
            await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}"}""");
            await Assert.That(fx.Sent).IsEmpty();
        } finally {
            DisabledSessions.RemoveMarker(sid);
        }
    }

    [Test]
    public async Task telemetry_events_post_but_do_not_spool_on_failure() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync("""{"hook_event_name":"preToolUse","session_id":"abc","tool_name":"Glob"}""");
        await Assert.That(fx.SpoolFiles).IsEmpty();
    }

    [Test]
    public async Task canonical_events_spool_on_POST_failure() {
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        await fx.HandleAsync($$$"""{"hook_event_name":"sessionEnd","session_id":"{{{Sid}}}"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith(Sid + ".jsonl");
    }

    [Test]
    public async Task spool_drain_runs_before_current_event_under_budget() {
        using var fx = new Fixture();
        fx.Spool.Append(Sid, "sessionStart", $$$"""{"hook_event_name":"sessionStart","session_id":"{{{Sid}}}"}""");
        await fx.HandleAsync($$$"""{"hook_event_name":"sessionEnd","session_id":"{{{Sid}}}"}""");
        await Assert.That(fx.RouteOrder).IsEquivalentTo(new[] { "session-start/cursor", "session-end/cursor" });
    }

    [Test]
    public async Task afterAgentThought_canonical_id_is_stable_across_replays() {
        using var fx = new Fixture();
        var body = """{"hook_event_name":"afterAgentThought","session_id":"abc","generation_id":"gen1","text":"hello"}""";
        await fx.HandleAsync(body);
        await fx.HandleAsync(body);
        var ids = fx.AllSentTo("agent-thought/cursor")
            .Select(b => JsonNode.Parse(b)!["canonical_event_id"]!.GetValue<string>())
            .Distinct()
            .ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task null_transcript_path_does_not_trigger_backfill() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc","transcript_path":null}""");
        await Assert.That(fx.AllSentTo("transcript-line/cursor")).IsEmpty();
    }

    [Test]
    public async Task expired_budget_returns_zero_not_throws() {
        using var fx = new Fixture();
        // budgetTotal=0 forces BudgetExpired() true on first check, which can also
        // propagate as OperationCanceledException from stdin/HTTP. Either way the
        // dispatcher must fail-open with return 0, never bubble the exception.
        var exit = await CursorHookCommand.HandleCore(
            fx.Client, "http://localhost",
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            fx.Spool, TimeSpan.Zero);
        await Assert.That(exit).IsEqualTo(0);
    }

    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(
            Path.GetTempPath(), $"kapacitor-cursor-hook-test-{Guid.NewGuid().ToString("N")[..8]}");

        public List<string> Sent       { get; } = new();
        public List<string> RouteOrder { get; } = new();
        public CursorHookSpool Spool   { get; }
        readonly string _spoolPath;
        readonly HttpClient _client;
        public HttpClient Client => _client;

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath, "*.jsonl") : [];

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            _spoolPath = Path.Combine(_tmpHome, "spool");
            Spool = new CursorHookSpool(_spoolPath);
            var handler = new StubHandler(async req => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/")) {
                    RouteOrder.Add(path.Replace("/hooks/", ""));
                }
                // GET watermark — return 404 so transcript backfill is a no-op without
                // tripping the fail-open path.
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(postStatus);
            });
            _client = new HttpClient(handler);
        }

        public Task<int> HandleAsync(string stdin) =>
            CursorHookCommand.HandleCore(
                _client, baseUrl: "http://localhost", stdin: new StringReader(stdin),
                spool: Spool, budgetTotal: TimeSpan.FromSeconds(2));

        public string SentToHook(string segment) =>
            Sent.First(s => s.StartsWith($"/hooks/{segment}")).Split('|', 2)[1];

        public IEnumerable<string> AllSentTo(string segment) =>
            Sent.Where(s => s.StartsWith($"/hooks/{segment}")).Select(s => s.Split('|', 2)[1]);

        public void Dispose() {
            _client.Dispose();
            try { Directory.Delete(_tmpHome, true); } catch { }
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }
}
