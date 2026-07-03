using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Wire-contract test for Antigravity historical import (AI-1160). Drives the full
/// discover → classify → import path over an on-disk brain tree (a root conversation plus
/// a subagent conversation linked via the parent's messages/*.json) and asserts the
/// lifecycle the server expects: session-start/antigravity → parent transcript →
/// subagent-start → child transcript (routed by agent_id) → subagent-stop →
/// session-end/antigravity, all tagged vendor=antigravity.
/// </summary>
public class AntigravityImportTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    readonly string         _home   = Directory.CreateTempSubdirectory("kcap-ag-import-it").FullName;

    const string Root  = "11110000-0000-4000-8000-000000000001";
    const string Child = "22220000-0000-4000-8000-000000000002";

    public void Dispose() {
        _server.Stop();
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    string BrainDir(string convId) => Path.Combine(_home, ".gemini", "antigravity", "brain", convId);

    void WriteTranscript(string convId, string firstUserText) {
        var dir = Path.Combine(BrainDir(convId), ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "transcript_full.jsonl"), new[] {
            $$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"2026-07-02T19:00:00Z","content":"<USER_REQUEST>{{firstUserText}}</USER_REQUEST>"}""",
            """{"step_index":1,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","created_at":"2026-07-02T19:00:05Z","content":"done"}"""
        });
    }

    void WriteLinkage() {
        var dir = Path.Combine(BrainDir(Root), ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Child + ".json"),
            new JsonObject { ["sender"] = Child, ["recipient"] = Root }.ToJsonString());
    }

    [Test]
    public async Task ImportSession_imports_root_with_nested_subagent() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        // The subagent conversation must NOT be discovered as its own top-level session.
        await Assert.That(discovered.Count).IsEqualTo(1);
        await Assert.That(discovered[0].SessionId).IsEqualTo(Root);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var posts = _server.LogEntries
            .Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path)
            .ToList();

        await Assert.That(posts).IsEquivalentTo(new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript", "/hooks/subagent-start",
            "/hooks/transcript", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        });
        // Fail-closed ordering: subagent registered before its content, stopped after.
        await Assert.That(posts.IndexOf("/hooks/subagent-start")).IsLessThan(posts.IndexOf("/hooks/subagent-stop"));

        // subagent-start carries the child conversation id as agent_id.
        var startBody = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(startBody).Contains($"\"agent_id\":\"{Child}\"");

        // The child transcript batch is tagged vendor=antigravity and routed by the agent_id.
        var subTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains(Child));
        await Assert.That(subTranscript).Contains("\"vendor\":\"antigravity\"");
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_root_repairs_a_missing_child() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Parent (no agentId) fully ingested → AlreadyLoaded; the child (agentId=Child) was never
        // imported (404). The AlreadyLoaded branch must STILL run the child-repair pass, else a
        // once-skipped subagent is lost forever (AI-1160 review, finding 1).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", Child).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Skipped);

        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();

        // The parent is not re-imported (no session-start/-end), but the missing child IS repaired:
        // subagent-start → child transcript → subagent-stop.
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsTrue();
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsTrue();
        await Assert.That(posts.Contains("/hooks/session-start/antigravity")).IsFalse();
        await Assert.That(posts.Contains("/hooks/session-end/antigravity")).IsFalse();

        var childTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!).Single();
        await Assert.That(childTranscript).Contains($"\"agent_id\":\"{Child}\"");
    }

    [Test]
    public async Task ImportChildren_fullyIngestedChild_reposts_idempotent_stop_without_resending_content() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Both parent and child are fully ingested (high HWM). The child's CONTENT must not be
        // re-sent, but subagent-stop is re-posted (idempotent) in case a prior run's stop failed
        // after content — otherwise the subagent would be left without a completion event forever
        // (AI-1160 review, finding at :240).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST")
            .Select(e => e.RequestMessage.Path).ToList();

        // Completion event repaired, but no content re-send and no re-registration.
        await Assert.That(posts.Contains("/hooks/subagent-stop")).IsTrue();
        await Assert.That(posts.Contains("/hooks/transcript")).IsFalse();
        await Assert.That(posts.Contains("/hooks/subagent-start")).IsFalse();
    }

    [Test]
    public async Task ImportChildren_resumes_by_line_position_without_shifting_numbers() {
        WriteTranscript(Root, "build it");
        WriteTranscript(Child, "sub task");
        WriteLinkage();

        // Parent not yet imported (404) → New; child partially imported up to line 0 → resume from
        // file line 1, numbered by TRUE position (1). The pre-fix code fed the HWM as
        // lineNumberOffset and re-sent the whole file as [1,2] (AI-1160 review, finding 2).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").WithParam("agentId", Child).UsingGet())
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":0}"""));
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var route in new[] {
            "/hooks/session-start/antigravity", "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/antigravity"
        }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);

        await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        var childTranscript = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => e.RequestMessage.Body!)
            .Single(b => b.Contains($"\"agent_id\":\"{Child}\""));

        // Only file line 1 is re-sent, numbered 1 — not the whole file shifted to [1,2].
        await Assert.That(childTranscript).Contains("\"line_numbers\":[1]");
    }

    [Test]
    public async Task ImportSession_second_run_is_AlreadyLoaded() {
        WriteTranscript(Root, "build it");

        // Server reports a high watermark → the transcript's last relevant line is already ingested.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));

        using var client = new HttpClient();
        var source = new AntigravityImportSource(home: _home, geminiCliHome: "");

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var outcome = await source.ImportSessionAsync(
            classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Skipped);
    }
}
