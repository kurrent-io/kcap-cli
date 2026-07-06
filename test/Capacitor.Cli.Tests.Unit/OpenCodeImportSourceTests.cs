using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class OpenCodeImportSourceTests {
    [Test]
    public async Task discovery_returns_roots_with_cwd_and_timestamp_excludes_children() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_root", null, "/work/a", "Root", 1782241513759);
        tmp.AddSession("ses_child", "ses_root", "/work/a", "Child", 1782241513761);
        tmp.AddMessageWithText("ses_root", "msg_1", "hello", 1782241513760);

        var source   = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo("ses_root");
        await Assert.That(sessions[0].Vendor).IsEqualTo("opencode");
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp)
            .IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1782241513759));
    }

    [Test]
    public async Task discovery_filters_by_cwd_and_session() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_a", null, "/work/a", "A", 100);
        tmp.AddSession("ses_b", null, "/work/b", "B", 200);
        tmp.AddMessageWithText("ses_a", "m1", "x", 100);
        tmp.AddMessageWithText("ses_b", "m2", "y", 200);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);

        var byCwd = await source.DiscoverAsync(new("/work/b", null, null, 0), CancellationToken.None);
        await Assert.That(byCwd.Select(s => s.SessionId)).IsEquivalentTo(["ses_b"]);

        var bySession = await source.DiscoverAsync(new(null, "ses_a", null, 0), CancellationToken.None);
        await Assert.That(bySession.Select(s => s.SessionId)).IsEquivalentTo(["ses_a"]);
    }

    [Test]
    public async Task IsAvailable_false_when_db_missing() {
        var source = new OpenCodeImportSource(Path.Combine(Path.GetTempPath(), "no-such-kcap.db"),
            ledgerPathOverride: Path.Combine(Path.GetTempPath(), $"kcap-ledger-{Guid.NewGuid():N}.json"));
        await Assert.That(source.IsAvailable).IsFalse();
    }

    [Test]
    public async Task discovery_treats_ms_as_ms_and_seconds_as_seconds() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_ms",  null, "/w", "ms",  1782241513759);
        tmp.AddSession("ses_sec", null, "/w", "sec", 1782241513);
        tmp.AddMessageWithText("ses_ms",  "m1", "x", 1782241513759);
        tmp.AddMessageWithText("ses_sec", "m2", "x", 1782241513);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);
        var byId = (await source.DiscoverAsync(new(null, null, null, 0), CancellationToken.None))
            .ToDictionary(s => s.SessionId, s => s.FirstTimestamp);

        await Assert.That(byId["ses_ms"]).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1782241513759));
        await Assert.That(byId["ses_sec"]).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1782241513));
    }

    [Test]
    public async Task discovery_handles_null_directory_and_excludes_it_under_cwd_filter() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_nodir", null, dir: null, "No dir", 100);
        tmp.AddMessageWithText("ses_nodir", "m1", "x", 100);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath);

        var all = await source.DiscoverAsync(new(null, null, null, 0), CancellationToken.None);
        await Assert.That(all.Single().Cwd).IsNull();

        var filtered = await source.DiscoverAsync(new("/work/a", null, null, 0), CancellationToken.None);
        await Assert.That(filtered.Count).IsEqualTo(0);
    }

    [Test]
    public async Task classify_new_when_server_has_no_watermark() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(classified[0].Vendor).IsEqualTo("opencode");
        await Assert.That(classified[0].FilePath).IsEqualTo("");
    }

    [Test]
    public async Task classify_already_loaded_after_a_full_import_records_the_ledger() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(p).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var ctx = new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null);

        // First run records the ledger (with the internally-computed fingerprint) on session-end.
        var s1 = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var c1 = await s1.ClassifyAsync(await s1.DiscoverAsync(new(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c1[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await s1.ImportSessionAsync(c1[0], new(client, server.Url!, false), CancellationToken.None);

        // Fresh source reloads the ledger → fingerprint matches → AlreadyLoaded.
        var s2 = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var c2 = await s2.ClassifyAsync(await s2.DiscoverAsync(new(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c2[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    [Test]
    public async Task classify_repair_when_not_in_ledger_and_watermark_present() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":42}"""));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(42);
    }

    [Test]
    public async Task classify_too_short_for_structural_only_session() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddStructuralMessage("ses_x", "m1", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }

    [Test]
    public async Task classify_too_short_for_zero_message_session() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_empty", null, "/w", "Empty", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath);
        var discovered = await source.DiscoverAsync(new(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new(client, server.Url!, MinLines: 1, ExcludedRepos: null, ExcludedPaths: null),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }
}
