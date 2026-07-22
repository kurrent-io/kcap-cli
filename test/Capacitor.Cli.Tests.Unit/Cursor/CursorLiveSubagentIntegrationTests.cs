using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// end-to-end coverage of CursorHookCommand's subagent-linking divert —
/// CursorLiveSubagentLinkerTests covers the pure ResolveParent/marker/discovery pieces in
/// isolation; these tests exercise the actual hook dispatcher wiring (subagent-start/-stop
/// instead of the top-level lifecycle, transcript routed with agent_id, mid-lifecycle hooks
/// suppressed) against a realistic on-disk `agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl`
/// layout.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class CursorLiveSubagentIntegrationTests {
    [Test]
    public async Task linked_child_sessionStart_posts_subagent_start_not_session_start() {
        using var fx = new Fixture();
        var (parentId, childId, childPath) = fx.SetupLinkedPair("do the survey");

        await fx.HandleAsync(childId, "sessionStart", childPath);

        await Assert.That(fx.RouteOrder).DoesNotContain("session-start/cursor");
        await Assert.That(fx.RouteOrder).Contains("subagent-start");

        var body = JsonNode.Parse(fx.SentToHook("subagent-start"))!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
        await Assert.That(body["agent_type"]!.GetValue<string>()).IsEqualTo("task");
        await Assert.That(body["strict"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task linked_child_transcript_is_routed_under_the_parent_with_agent_id() {
        using var fx = new Fixture();
        var (parentId, childId, childPath) = fx.SetupLinkedPair("investigate the bug");

        await fx.HandleAsync(childId, "sessionStart", childPath);

        var batch = JsonNode.Parse(fx.SentToHook("transcript"))!;
        await Assert.That(batch["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(batch["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
    }

    [Test]
    public async Task linked_child_mid_lifecycle_hook_is_suppressed_but_transcript_still_backfills() {
        using var fx = new Fixture();
        var (parentId, childId, childPath) = fx.SetupLinkedPair("write the report");

        // First hook establishes + persists the link.
        await fx.HandleAsync(childId, "sessionStart", childPath);
        fx.Sent.Clear();
        fx.RouteOrder.Clear();

        // A later mid-lifecycle hook for the SAME child must not forward the raw event...
        await fx.HandleAsync(childId, "afterAgentThought", childPath, extraFields: ",\"generation_id\":\"g1\",\"text\":\"thinking\"");

        await Assert.That(fx.RouteOrder).DoesNotContain("agent-thought/cursor");
        // ...but the transcript watermark is still (re)checked under the parent + agent_id.
        await Assert.That(fx.RouteOrder).Contains("transcript");
        var batch = JsonNode.Parse(fx.SentToHook("transcript"))!;
        await Assert.That(batch["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(batch["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
    }

    [Test]
    public async Task linked_child_sessionEnd_posts_subagent_stop_not_session_end() {
        using var fx = new Fixture();
        var (parentId, childId, childPath) = fx.SetupLinkedPair("clean up the repo");

        await fx.HandleAsync(childId, "sessionStart", childPath);
        fx.Sent.Clear();
        fx.RouteOrder.Clear();

        await fx.HandleAsync(childId, "sessionEnd", childPath);

        await Assert.That(fx.RouteOrder).DoesNotContain("session-end/cursor");
        await Assert.That(fx.RouteOrder).Contains("subagent-stop");

        var body = JsonNode.Parse(fx.SentToHook("subagent-stop"))!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(parentId);
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo(childId);
    }

    [Test]
    public async Task unlinked_session_still_posts_top_level_session_start() {
        // Regression guard: an ordinary (non-subagent) session must behave exactly as before —
        // no sibling transcript happens to match, so ResolveParent returns null and the normal
        // top-level flow runs unmodified.
        using var fx = new Fixture();
        var soloId  = Guid.NewGuid().ToString();
        var soloDir = Path.Combine(fx.TranscriptsRoot, soloId);
        Directory.CreateDirectory(soloDir);
        var soloPath = Path.Combine(soloDir, soloId + ".jsonl");
        File.WriteAllText(soloPath,
            """{"role":"user","message":{"content":[{"type":"text","text":"hello, nothing to correlate"}]}}""" + "\n");

        await fx.HandleAsync(soloId, "sessionStart", soloPath);

        await Assert.That(fx.RouteOrder).Contains("session-start/cursor");
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-start");
    }

    [Test]
    public async Task linked_child_subagent_stop_is_not_delivered_ahead_of_a_spooled_subagent_start() {
        // Ordering guard: a child whose subagent-start is still spooled (a prior transient
        // failure) must NOT get subagent-stop delivered ahead of it. With a 500 stub the
        // HandleCore drain can't clear the backlog, so HasBacklog stays true and the divert
        // must spool subagent-stop behind the start rather than posting it.
        using var fx = new Fixture(postStatus: HttpStatusCode.InternalServerError);
        var (parentId, childId, childPath) = fx.SetupLinkedPair("ordering guard");

        // Establish the link marker (as the child's first hook would have) and seed the spool
        // with an undelivered subagent-start, simulating a prior transient POST failure.
        CursorLiveSubagentLinker.SaveLink(childId, parentId, "task");
        fx.Spool.Append(childId, "subagent-start", $$"""{"hook_event_name":"subagent_start","session_id":"{{parentId}}","agent_id":"{{childId}}"}""");

        // sessionEnd under a still-failing server: the HandleCore drain re-attempts the
        // spooled subagent-start (500 → stays queued), so HasBacklog is true and the divert
        // must NOT post subagent-stop ahead of it.
        await fx.HandleAsync(childId, "sessionEnd", childPath);
        await Assert.That(fx.RouteOrder).DoesNotContain("subagent-stop");

        // Now the server recovers. Any later hook for the child drains the spool in order —
        // subagent-start (the recovered .draining temp, oldest-first) BEFORE subagent-stop
        // (the just-queued live file) — proving the stop never overtakes its start.
        fx.RouteOrder.Clear();
        fx.PostStatus = HttpStatusCode.OK;
        await fx.HandleAsync(childId, "afterAgentResponse", childPath);

        var startIdx = fx.RouteOrder.IndexOf("subagent-start");
        var stopIdx  = fx.RouteOrder.IndexOf("subagent-stop");
        await Assert.That(startIdx).IsGreaterThanOrEqualTo(0);
        await Assert.That(stopIdx).IsGreaterThan(startIdx);
    }

    /// <summary>
    /// the agent_id the LIVE path uses (child session id, dashless) must
    /// be byte-identical to what the IMPORT path (CursorImportSource.SendSubagentLifecycleAsync)
    /// would use for the same child — otherwise a live-then-import of the same session would
    /// duplicate the subagent's AgentSubsession stream instead of converging on it.
    /// </summary>
    [Test]
    public async Task live_agent_id_matches_the_dashless_id_the_import_path_would_use() {
        using var fx = new Fixture();
        var (_, childId, childPath) = fx.SetupLinkedPair("parity check");

        await fx.HandleAsync(childId, "sessionStart", childPath);

        var startBody = JsonNode.Parse(fx.SentToHook("subagent-start"))!;
        var liveAgentId = startBody["agent_id"]!.GetValue<string>();

        // Mirrors CursorImportSource.NormalizeCursorSessionId, the import path's own
        // dashless-id convention (CursorImportSource.cs:91,468).
        var importAgentId = childId; // fx already hands back the dashless id (see SetupLinkedPair)
        await Assert.That(liveAgentId).IsEqualTo(importAgentId);
        await Assert.That(liveAgentId.Contains('-')).IsFalse();
    }

    sealed class Fixture : IDisposable {
        readonly string _root = Path.Combine(Path.GetTempPath(), $"kcap-live-subagent-{Guid.NewGuid():N}");

        public string TranscriptsRoot { get; }
        public string SpoolDir        { get; }
        public List<string> Sent       { get; } = [];
        public List<string> RouteOrder { get; } = [];
        public HookSpool    Spool      { get; }
        public HttpClient   Client     { get; }
        public HttpStatusCode PostStatus { get; set; } = HttpStatusCode.OK;

        readonly List<string> _markersToClean = [];

        public Fixture(HttpStatusCode postStatus = HttpStatusCode.OK) {
            PostStatus = postStatus;
            Directory.CreateDirectory(_root);
            TranscriptsRoot = Path.Combine(_root, "agent-transcripts");
            Directory.CreateDirectory(TranscriptsRoot);
            SpoolDir = Path.Combine(_root, "spool");
            Spool = new HookSpool(SpoolDir);

            var handler = new StubHandler(async req => {
                var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
                var path = req.RequestUri!.AbsolutePath;
                Sent.Add($"{path}|{body}");
                if (path.StartsWith("/hooks/")) RouteOrder.Add(path.Replace("/hooks/", ""));

                // Watermark GET — always 404 so the backfill always resumes from 0 and posts
                // whatever's on disk right now.
                if (req.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.NotFound);
                return new HttpResponseMessage(PostStatus);
            });
            Client = new HttpClient(handler);
        }

        /// <summary>
        /// Builds a realistic Cursor `agent-transcripts/&lt;sid&gt;/&lt;sid&gt;.jsonl` parent +
        /// child pair whose child's first user_query matches the parent's Task prompt — the
        /// exact shape CursorSubagentCorrelator/CursorLiveSubagentLinker key off of. Session ids
        /// are dashed (mirroring real Cursor ids); the returned ids are dashless (post-normalize,
        /// matching what CursorHookCommand's own NormalizeGuidField produces).
        /// </summary>
        public (string ParentId, string ChildId, string ChildPath) SetupLinkedPair(string prompt) {
            var parentRaw = Guid.NewGuid().ToString();
            var childRaw  = Guid.NewGuid().ToString();

            var parentDir = Path.Combine(TranscriptsRoot, parentRaw);
            Directory.CreateDirectory(parentDir);
            var parentLine1 = """{"role":"user","message":{"content":[{"type":"text","text":"kick it off"}]}}""";
            var parentLine2 = System.Text.Json.JsonSerializer.Serialize(new {
                role = "assistant",
                message = new { content = new object[] { new { type = "tool_use", name = "Task", input = new { prompt } } } },
            });
            File.WriteAllText(Path.Combine(parentDir, parentRaw + ".jsonl"), parentLine1 + "\n" + parentLine2 + "\n");

            var childDir = Path.Combine(TranscriptsRoot, childRaw);
            Directory.CreateDirectory(childDir);
            var childPath  = Path.Combine(childDir, childRaw + ".jsonl");
            var childLine1 = System.Text.Json.JsonSerializer.Serialize(new {
                role = "user",
                message = new { content = new object[] { new { type = "text", text = $"<user_query>\n{prompt}\n</user_query>" } } },
            });
            File.WriteAllText(childPath, childLine1 + "\n");

            _markersToClean.Add(childRaw.Replace("-", ""));

            return (parentRaw.Replace("-", ""), childRaw.Replace("-", ""), childPath);
        }

        public Task<int> HandleAsync(string sessionId, string eventName, string? transcriptPath, string extraFields = "") =>
            CursorHookCommand.HandleCore(
                Client,
                baseUrl: "http://localhost",
                stdin: new StringReader(
                    $$"""{"hook_event_name":"{{eventName}}","session_id":"{{sessionId}}","transcript_path":"{{transcriptPath?.Replace(@"\", @"\\")}}"{{extraFields}}}"""
                ),
                spool: Spool,
                budgetTotal: TimeSpan.FromSeconds(2)
            );

        public string SentToHook(string segment) =>
            Sent.Last(s => s.StartsWith($"/hooks/{segment}")).Split('|', 2)[1];

        public void Dispose() {
            Client.Dispose();
            foreach (var m in _markersToClean) {
                try { File.Delete(Path.Combine(PathHelpers.ConfigPath("cursor-subagent-links"), m)); } catch { }
            }
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            impl(request);
    }
}
