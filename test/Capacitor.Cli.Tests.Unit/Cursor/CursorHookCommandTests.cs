using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

// Several tests here read HOME-derived paths (DisabledSessions marker dir,
// PathHelpers.HomeDirectory injection into the outgoing payload) and one
// mutates KCAP_AGENT_ID. Serialise against every other test that
// mutates HOME so a racing HOME-setter from PluginCommand* tests can't
// land our marker writes in the wrong directory.
[NotInParallel("HomeEnvVarMutation")]
public class CursorHookCommandTests {
    const string Sid = "8c3276c2c8f743ce98898c2becf5240a";

    [Test]
    public async Task malformed_stdin_returns_zero() {
        using var fx   = new Fixture();
        var       exit = await fx.HandleAsync("not a json payload");
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(fx.Sent).IsEmpty();
    }

    [Test]
    public async Task missing_hook_event_name_returns_zero() {
        using var fx   = new Fixture();
        var       exit = await fx.HandleAsync("""{"session_id":"abc"}""");
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
    [NotInParallel("CapacitorAgentIdEnvVar")]
    public async Task home_dir_and_agent_host_id_are_injected() {
        Environment.SetEnvironmentVariable("KCAP_AGENT_ID", "host-42");

        try {
            using var fx = new Fixture();
            await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc"}""");
            var sent = fx.SentToHook("session-start/cursor");
            var node = JsonNode.Parse(sent)!;
            await Assert.That(node["home_dir"]?.GetValue<string>()).IsNotNull();
            await Assert.That(node["agent_host_id"]?.GetValue<string>()).IsEqualTo("host-42");
        } finally {
            Environment.SetEnvironmentVariable("KCAP_AGENT_ID", null);
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
        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");
        var files = fx.SpoolFiles.ToList();
        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith(Sid + ".jsonl");
    }

    [Test]
    public async Task spool_drain_runs_before_current_event_under_budget() {
        using var fx = new Fixture();
        fx.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");
        await fx.HandleAsync($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}""");
        await Assert.That(fx.RouteOrder).IsEquivalentTo(["session-start/cursor", "session-end/cursor"]);
    }

    [Test]
    public async Task afterAgentThought_canonical_id_is_stable_across_replays() {
        using var fx   = new Fixture();
        var       body = """{"hook_event_name":"afterAgentThought","session_id":"abc","generation_id":"gen1","text":"hello"}""";
        await fx.HandleAsync(body);
        await fx.HandleAsync(body);

        var ids = fx.AllSentTo("agent-thought/cursor")
            .Select(b => JsonNode.Parse(b)!["canonical_event_id"]!.GetValue<string>())
            .Distinct()
            .ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
    }

    [Test]
    public async Task sessionEnd_drains_transcript_before_posting_terminal_hook() {
        // Server's HandleSessionEnd clears the CursorAttachmentsFifo as soon
        // as it accepts the /hooks/session-end/cursor POST. If the CLI posted
        // sessionEnd first and only then ran the transcript backfill, the
        // final user line in the transcript would be normalized AFTER the
        // FIFO was wiped and any queued beforeSubmitPrompt attachments would
        // be lost. Verify the order is: transcript batch → session-end.
        using var fx = new Fixture();

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"final prompt"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"sessionEnd","session_id":"{{Sid}}","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var sessionEndIdx = fx.RouteOrder.FindIndex(r => r == "session-end/cursor");

        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(sessionEndIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsLessThan(sessionEndIdx);
    }

    [Test]
    public async Task non_sessionEnd_events_still_post_before_backfill() {
        // Regression guard: only sessionEnd swaps the order. Other events
        // (here: beforeSubmitPrompt) must keep the existing post-then-backfill
        // ordering so lifecycle metadata reaches the server before any new
        // transcript context.
        using var fx = new Fixture();

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"hello"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"beforeSubmitPrompt","session_id":"{{Sid}}","prompt":"hello","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var promptIdx     = fx.RouteOrder.FindIndex(r => r == "user-prompt/cursor");

        await Assert.That(promptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(promptIdx).IsLessThan(transcriptIdx);
    }

    [Test]
    public async Task telemetry_only_hook_touches_the_heartbeat_file() {
        // AI-1382 Task 8: even a telemetry-only hook (never spooled, lossy on failure) must
        // touch the per-session heartbeat — it reflects "Cursor is still firing hooks",
        // independent of whatever the transcript/spool machinery is doing.
        using var fx = new Fixture();
        var       sid = Guid.NewGuid().ToString("N");
        var       before = DateTimeOffset.UtcNow;

        await fx.HandleAsync($$"""{"hook_event_name":"postToolUse","session_id":"{{sid}}","tool_name":"Bash"}""");

        var heartbeat = WatcherHeartbeat.Read(CursorMarkers.HeartbeatPath(sid));
        await Assert.That(heartbeat).IsNotNull();
        await Assert.That(heartbeat!.Value).IsGreaterThanOrEqualTo(before);
    }

    [Test]
    public async Task beforeSubmitPrompt_clears_its_barrier_once_the_live_POST_succeeds() {
        using var fx  = new Fixture(); // defaults to HttpStatusCode.OK
        var       sid = Guid.NewGuid().ToString("N");

        await fx.HandleAsync($$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}","prompt":"hi"}""");

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task beforeSubmitPrompt_barrier_stays_pending_when_the_live_POST_fails() {
        using var fx  = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        var       sid = Guid.NewGuid().ToString("N");

        await fx.HandleAsync($$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}","prompt":"hi"}""");

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsTrue();
    }

    [Test]
    public async Task sessionEnd_drains_the_hook_spool_before_the_pre_end_transcript_drain_and_clears_the_barrier() {
        // AI-1382 Task 8: a beforeSubmitPrompt whose live POST previously failed left a barrier
        // + a spooled user-prompt/cursor entry behind. sessionEnd must deliver that spooled
        // entry (clearing the barrier) BEFORE running its pre-end transcript drain, so a
        // transcript line depending on the attachment is never normalized ahead of it.
        using var fx  = new Fixture();
        var       sid = Guid.NewGuid().ToString("N");

        CursorMarkers.CreateBarrier(sid, DateTimeOffset.UtcNow);
        fx.Spool.Append(sid, "user-prompt/cursor", $$"""{"hook_event_name":"beforeSubmitPrompt","session_id":"{{sid}}"}""");

        await fx.WriteTranscript(
            """{"role":"user","message":{"content":[{"type":"text","text":"final prompt"}]}}"""
        );

        await fx.HandleAsync(
            $$"""
               {"hook_event_name":"sessionEnd","session_id":"{{sid}}","transcript_path":"{{fx.TranscriptPathEscaped}}"}
               """
        );

        var promptIdx     = fx.RouteOrder.FindIndex(r => r == "user-prompt/cursor");
        var transcriptIdx = fx.RouteOrder.FindIndex(r => r == "transcript");
        var sessionEndIdx = fx.RouteOrder.FindIndex(r => r == "session-end/cursor");

        await Assert.That(promptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(transcriptIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(sessionEndIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(promptIdx).IsLessThan(transcriptIdx);
        await Assert.That(transcriptIdx).IsLessThan(sessionEndIdx);

        await Assert.That(CursorMarkers.BarrierPending(sid, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(60))).IsFalse();
    }

    [Test]
    public async Task null_transcript_path_does_not_trigger_backfill() {
        using var fx = new Fixture();
        await fx.HandleAsync("""{"hook_event_name":"sessionStart","session_id":"abc","transcript_path":null}""");
        await Assert.That(fx.AllSentTo("transcript")).IsEmpty();
    }

    [Test]
    public async Task expired_budget_returns_zero_not_throws() {
        using var fx = new Fixture();

        // budgetTotal=0 forces BudgetExpired() true on first check, which can also
        // propagate as OperationCanceledException from stdin/HTTP. Either way the
        // dispatcher must fail-open with return 0, never bubble the exception.
        var exit = await CursorHookCommand.HandleCore(
            fx.Client,
            "http://localhost",
            new StringReader("""{"hook_event_name":"sessionStart","session_id":"abc"}"""),
            fx.Spool,
            TimeSpan.Zero
        );
        await Assert.That(exit).IsEqualTo(0);
    }

    [Test]
    public async Task hard_cap_returns_zero_when_inner_ignores_cancellation() {
        // Simulates an uncancellable hang inside TokenStore.RefreshAsync's
        // HttpClient.PostAsync — no CT plumbed through, default 100s timeout.
        // The Task.WhenAny ceiling in CursorHookCommand.Handle must beat that.
        var inner = Task.Run(async () => {
                await Task.Delay(TimeSpan.FromSeconds(10));

                return 42;
            }
        );
        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var exit = await CursorHookCommand.WithHardCap(inner, TimeSpan.FromMilliseconds(50));
        sw.Stop();

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task hard_cap_returns_inner_result_when_inner_finishes_first() {
        var inner = Task.FromResult(7);
        var exit  = await CursorHookCommand.WithHardCap(inner, TimeSpan.FromSeconds(2));
        await Assert.That(exit).IsEqualTo(7);
    }

    [Test]
    public async Task fresh_canonical_event_is_spooled_when_drain_consumes_budget() {
        // Drain blocks past the budget by parking the POST handler. The
        // dispatcher must spool the fresh sessionEnd that hasn't been
        // POSTed yet instead of losing it.
        using var fx = new Fixture();
        fx.HoldOnPost = TimeSpan.FromMilliseconds(50);

        fx.Spool.Append(Sid, "session-start/cursor", $$"""{"hook_event_name":"sessionStart","session_id":"{{Sid}}"}""");

        // 30 ms budget — first drained POST eats most of it, BudgetExpired flips
        // before the fresh event can post. The fresh sessionEnd must land back
        // in the spool, replacing the just-delivered sessionStart line.
        var exit = await CursorHookCommand.HandleCore(
            fx.Client,
            "http://localhost",
            new StringReader($$"""{"hook_event_name":"sessionEnd","session_id":"{{Sid}}"}"""),
            fx.Spool,
            TimeSpan.FromMilliseconds(30)
        );

        await Assert.That(exit).IsEqualTo(0);

        var spoolPath = fx.SpoolFiles.SingleOrDefault();
        await Assert.That(spoolPath).IsNotNull();
        var spoolContent = await File.ReadAllTextAsync(spoolPath!);
        await Assert.That(spoolContent).Contains("sessionEnd");
    }

    [Test]
    public async Task legacy_cursor_spool_is_transformed_and_merged() {
        var dir       = Path.Combine(Path.GetTempPath(), $"kcap-mig-{Guid.NewGuid():N}");
        var legacyDir = Path.Combine(dir, "legacy");
        var spoolDir  = Path.Combine(dir, "spool");
        Directory.CreateDirectory(legacyDir);
        try {
            // Old format: {hook_event_name, body}
            await File.WriteAllTextAsync(Path.Combine(legacyDir, $"{Sid}.jsonl"),
                $"{{\"hook_event_name\":\"sessionEnd\",\"body\":\"{{\\\"session_id\\\":\\\"{Sid}\\\"}}\"}}\n");

            var spool = new HookSpool(spoolDir);
            CursorHookCommand.MigrateLegacyCursorSpool(spool, legacyDir);

            var migrated = await File.ReadAllTextAsync(Path.Combine(spoolDir, $"{Sid}.jsonl"));
            await Assert.That(migrated).Contains("\"route\":\"session-end/cursor\"");
            await Assert.That(File.Exists(Path.Combine(legacyDir, $"{Sid}.jsonl"))).IsFalse();
        } finally { try { Directory.Delete(dir, true); } catch { } }
    }

    sealed class Fixture : IDisposable {
        readonly string _tmpHome = Path.Combine(
            Path.GetTempPath(),
            $"kcap-cursor-hook-test-{Guid.NewGuid().ToString("N")[..8]}"
        );

        readonly string _spoolPath;
        readonly string _transcriptPath;

        public List<string> Sent       { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool    Spool      { get; }
        public TimeSpan     HoldOnPost { get; set; } = TimeSpan.Zero;

        public HttpClient Client                { get; }
        public string     TranscriptPathEscaped => _transcriptPath.Replace(@"\", @"\\");

        public Task WriteTranscript(string content) =>
            File.WriteAllTextAsync(_transcriptPath, content);

        public IEnumerable<string> SpoolFiles =>
            Directory.Exists(_spoolPath) ? Directory.EnumerateFiles(_spoolPath, "*.jsonl") : [];

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            Directory.CreateDirectory(_tmpHome);
            _spoolPath      = Path.Combine(_tmpHome, "spool");
            _transcriptPath = Path.Combine(_tmpHome, "transcript.jsonl");
            Spool           = new HookSpool(_spoolPath);

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

                    if (HoldOnPost > TimeSpan.Zero) {
                        await Task.Delay(HoldOnPost);
                    }

                    return new HttpResponseMessage(postStatus);
                }
            );
            Client = new HttpClient(handler);
        }

        public Task<int> HandleAsync(string stdin) =>
            CursorHookCommand.HandleCore(
                Client,
                baseUrl: "http://localhost",
                stdin: new StringReader(stdin),
                spool: Spool,
                budgetTotal: TimeSpan.FromSeconds(2)
            );

        public string SentToHook(string segment) =>
            Sent.First(s => s.StartsWith($"/hooks/{segment}")).Split('|', 2)[1];

        public IEnumerable<string> AllSentTo(string segment) =>
            Sent.Where(s => s.StartsWith($"/hooks/{segment}")).Select(s => s.Split('|', 2)[1]);

        public void Dispose() {
            Client.Dispose();
            try { Directory.Delete(_tmpHome, true); } catch { }
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }
}
