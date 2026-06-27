using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Routed-import wire-contract tests for <see cref="OpenCodeImportSource"/>: the full
/// discover → classify → import path against a stub server, covering lifecycle order,
/// strict-send abort, subagent routing, repair-above-HWM, and ledger skip-on-rerun.
/// </summary>
public class OpenCodeImportSourceImportTests : IDisposable {
    readonly WireMockServer    _server = WireMockServer.Start();
    readonly OpenCodeDbFixtureIt _fix  = new();

    public void Dispose() { _server.Stop(); _fix.Dispose(); }

    void StubOk(params string[] paths) {
        foreach (var p in paths)
            _server.Given(Request.Create().WithPath(p).UsingPost())
                   .RespondWith(Response.Create().WithStatusCode(200));
    }

    [Test]
    public async Task ImportSession_posts_parent_lifecycle_transcript_and_title() {
        _fix.AddSession("ses_root", null, "/work/a", "Repo overview", 1782241513759);
        _fix.AddMessageWithText("ses_root", "msg_1", "hello", 1782241513760);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        StubOk("/hooks/session-start/opencode", "/hooks/transcript", "/hooks/set-title", "/hooks/session-end/opencode");

        using var client = new HttpClient();
        var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);

        var outcome = await source.ImportSessionAsync(classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var logs = _server.LogEntries.Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(logs).Contains("/hooks/session-start/opencode");
        await Assert.That(logs).Contains("/hooks/transcript");
        await Assert.That(logs).Contains("/hooks/set-title");
        await Assert.That(logs).Contains("/hooks/session-end/opencode");
    }

    [Test]
    public async Task ImportSession_fails_and_withholds_session_end_when_transcript_rejected() {
        _fix.AddSession("ses_root", null, "/work/a", "T", 100);
        _fix.AddMessageWithText("ses_root", "msg_1", "hello", 110);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        StubOk("/hooks/session-start/opencode", "/hooks/session-end/opencode");
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(500));

        using var client = new HttpClient();
        var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);

        var outcome = await source.ImportSessionAsync(classified[0],
            new ImportContext(client, _server.Url!, false), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(ImportOutcome.Failed);
        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).DoesNotContain("/hooks/session-end/opencode");
    }

    [Test]
    public async Task ImportSession_routes_children_as_subagents_before_session_end() {
        _fix.AddSession("ses_root", null, "/work/a", "Parent", 100);
        _fix.AddMessageWithText("ses_root", "msg_p", "parent says hi", 110);
        _fix.AddSession("ses_kid", "ses_root", "/work/a", "Child", 120);
        _fix.AddMessageWithTextAndAgent("ses_kid", "msg_c", "child work", 130, agent: "general");

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        StubOk("/hooks/session-start/opencode", "/hooks/transcript", "/hooks/set-title",
               "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-end/opencode");

        using var client = new HttpClient();
        var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Select(d => d.SessionId)).IsEquivalentTo(new[] { "ses_root" });
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);

        var outcome = await source.ImportSessionAsync(classified[0],
            new ImportContext(client, _server.Url!, false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Loaded);

        var entries = _server.LogEntries.OrderBy(e => e.RequestMessage.DateTime).ToList();
        var paths   = entries.Select(e => e.RequestMessage.Path).ToList();

        var startIdx = paths.IndexOf("/hooks/session-start/opencode");
        var subStart = paths.IndexOf("/hooks/subagent-start");
        var subStop  = paths.LastIndexOf("/hooks/subagent-stop");
        var endIdx   = paths.IndexOf("/hooks/session-end/opencode");
        await Assert.That(startIdx >= 0 && subStart > startIdx && subStop > subStart && endIdx > subStop).IsTrue();

        var startBody = entries.First(e => e.RequestMessage.Path == "/hooks/subagent-start").RequestMessage.Body!;
        await Assert.That(startBody).Contains("\"agent_id\":\"ses_kid\"");
        await Assert.That(startBody).Contains("\"agent_type\":\"general\"");

        var childTranscriptIdx = -1;
        for (var i = 0; i < entries.Count; i++) {
            var e = entries[i];
            if (e.RequestMessage.Path == "/hooks/transcript"
             && e.RequestMessage.Body!.Contains("\"opencode\"")
             && e.RequestMessage.Body!.Contains("ses_kid")) { childTranscriptIdx = i; break; }
        }
        await Assert.That(childTranscriptIdx).IsGreaterThan(subStart);
        await Assert.That(childTranscriptIdx).IsLessThan(subStop);
    }

    [Test]
    public async Task rerun_repairs_unrecorded_session_by_replaying_above_hwm() {
        _fix.AddSession("ses_root", null, "/work/a", "T", 100);
        _fix.AddMessageWithText("ses_root", "msg_1", "hello", 110);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":42}"""));
        StubOk("/hooks/session-start/opencode", "/hooks/transcript", "/hooks/set-title", "/hooks/session-end/opencode");

        using var client = new HttpClient();
        var source     = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, _server.Url!, 0, null, null), CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);

        var outcome = await source.ImportSessionAsync(classified[0],
            new ImportContext(client, _server.Url!, false), CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(ImportOutcome.Resumed);

        var body = _server.LogEntries.First(e => e.RequestMessage.Path == "/hooks/transcript").RequestMessage.Body!;
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        foreach (var n in doc.RootElement.GetProperty("line_numbers").EnumerateArray())
            await Assert.That(n.GetInt32() > 42).IsTrue();

        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).Contains("/hooks/session-end/opencode");
    }

    [Test]
    public async Task second_run_skips_via_ledger_and_does_not_resend() {
        _fix.AddSession("ses_root", null, "/work/a", "T", 100);
        _fix.AddMessageWithText("ses_root", "msg_1", "hello", 110);

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        StubOk("/hooks/session-start/opencode", "/hooks/transcript", "/hooks/set-title", "/hooks/session-end/opencode");

        using var client = new HttpClient();
        var ctx       = new ClassifyContext(client, _server.Url!, 0, null, null);
        var importCtx = new ImportContext(client, _server.Url!, false);

        var s1 = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var c1 = await s1.ClassifyAsync(
            await s1.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c1[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(await s1.ImportSessionAsync(c1[0], importCtx, CancellationToken.None)).IsEqualTo(ImportOutcome.Loaded);

        var transcriptCountAfterRun1 = _server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/transcript");

        var s2 = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var c2 = await s2.ClassifyAsync(
            await s2.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c2[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(await s2.ImportSessionAsync(c2[0], importCtx, CancellationToken.None)).IsEqualTo(ImportOutcome.Skipped);

        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/transcript"))
            .IsEqualTo(transcriptCountAfterRun1);
    }

    [Test]
    public async Task batch2_failure_then_rerun_repairs_above_hwm() {
        _fix.AddSession("ses_root", null, "/work/a", "T", 100);
        for (var i = 0; i < 150; i++)
            _fix.AddMessageWithText("ses_root", $"msg_{i:D3}", $"line {i}", 100 + i);

        using var client = new HttpClient();
        var ctx       = new ClassifyContext(client, _server.Url!, 0, null, null);
        var importCtx = new ImportContext(client, _server.Url!, false);
        var source    = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);

        // Run 1: batch 1 → 200, batch 2 → 500 (WireMock scenario state machine).
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start/opencode").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
               .InScenario("b").WillSetStateTo("after1")
               .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
               .InScenario("b").WhenStateIs("after1")
               .RespondWith(Response.Create().WithStatusCode(500));

        var c1 = await source.ClassifyAsync(
            await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c1[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(await source.ImportSessionAsync(c1[0], importCtx, CancellationToken.None)).IsEqualTo(ImportOutcome.Failed);
        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).DoesNotContain("/hooks/session-end/opencode");

        // Run 2: server now reports the HWM left by batch 1 (line 99); all POSTs OK.
        _server.Reset();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":99}"""));
        StubOk("/hooks/session-start/opencode", "/hooks/transcript", "/hooks/set-title", "/hooks/session-end/opencode");

        var source2 = new OpenCodeImportSource(_fix.DbPath, _fix.LedgerPath);
        var c2 = await source2.ClassifyAsync(
            await source2.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c2[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(await source2.ImportSessionAsync(c2[0], importCtx, CancellationToken.None)).IsEqualTo(ImportOutcome.Resumed);

        await Assert.That(_server.LogEntries.Select(e => e.RequestMessage.Path)).Contains("/hooks/session-end/opencode");
        foreach (var e in _server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")) {
            using var doc = System.Text.Json.JsonDocument.Parse(e.RequestMessage.Body!);
            foreach (var n in doc.RootElement.GetProperty("line_numbers").EnumerateArray())
                await Assert.That(n.GetInt32() > 99).IsTrue();
        }
    }
}
