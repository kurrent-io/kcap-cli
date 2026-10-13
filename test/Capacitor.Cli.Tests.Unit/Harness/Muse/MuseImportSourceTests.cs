using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Harness.Muse;
using TUnit.Assertions.Enums;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Muse;

public class MuseImportSourceTests : IDisposable {
    const string ParentId    = "1d8166bf-0f96-48cf-b6ff-ee03c8b9e69c";
    const string GoalChild   = "43b7956d-b5c5-422c-b9ca-80c98e8dff28";
    const string VerifyChild = "8f8a0462-ac0a-45d2-9fed-e24aff5743d4";

    // recorded_at (microseconds since the epoch) of the fixture's session.opened.observed / session.end.
    const long OpenedMicros = 1791916401995572;
    const long EndedMicros  = 1791916437749511;

    // The resumed fixture's second run: its end is the newest, and a blank line sits before it.
    const long ResumedEndedMicros = 1791916455926519;
    const int  ResumedLastIndex   = 6;
    const string ResumedId        = "01a13694-2560-71d3-a276-032c42f78ab9";

    const string WorkspaceRoot = "/home/dev/project";

    [TempDir]  public required TempDir  Tmp  { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static DiscoveryFilters NoFilters => new(FilterCwd: null, FilterSession: null, Since: null, MinLines: 1);

    MusePaths Paths => new(new UserHome(Tmp.PathTo("home")), null, Tmp.PathTo("data"));

    MuseImportSource Source() => new(Paths, TimeProvider.System);

    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Harness", "Muse", "fixtures", name));

    static DateTimeOffset FromMicros(long micros) => DateTimeOffset.UnixEpoch.AddTicks(micros * 10);

    string SessionDir(string id) => Path.Combine(Paths.SessionsDir, "2026", "10", "13", id);

    string LayOutProbe() {
        var parent = SessionDir(ParentId);
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(parent, "session.jsonl")), Fixture("parent.session.jsonl"));
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(parent, "subagent", GoalChild, "session.jsonl")), Fixture("child-goal.session.jsonl"));
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(parent, "subagent", VerifyChild, "session.jsonl")), Fixture("child-verify.session.jsonl"));
        return Path.Combine(parent, "session.jsonl");
    }

    void LayOutApprovalOnly() => LayOut(SessionDir("01a13694-2560-71d3-a276-032c42f78ab9"), "approval-only.session.jsonl");

    string LayOut(string sessionDir, string fixture) {
        var log = Path.Combine(sessionDir, "session.jsonl");
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, log), Fixture(fixture));
        return log;
    }

    void StubServer(int lastLine = -1) {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(lastLine < 0
                ? Response.Create().WithStatusCode(404)
                : Response.Create().WithStatusCode(200).WithBody($$"""{"last_line_number":{{lastLine}}}"""));
        _server.Given(Request.Create().WithPath("/hooks/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/*/*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    async Task<(IReadOnlyList<ImportCommand.SessionClassification> Classified, ImportSessionResult Result)> ImportProbeAsync() {
        using var client = new HttpClient();

        var source     = Source();
        var discovered = await source.DiscoverAsync(NoFilters, CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 1, Home: Home), CancellationToken.None);
        var result     = await source.ImportSessionAsync(classified[0], new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        return (classified, result);
    }

    string[] PostedRoutes() => _server.LogEntries
        .Where(e => e.RequestMessage.Method == "POST")
        .Select(e => e.RequestMessage.Path)
        .ToArray();

    JsonNode PostedBody(string path, int index = 0) =>
        JsonNode.Parse(_server.LogEntries.Where(e => e.RequestMessage.Path == path).ElementAt(index).RequestMessage.Body!)!;

    [Test]
    public async Task Discovery_keeps_a_session_with_a_workspace_root_and_skips_the_one_without() {
        LayOutProbe();
        LayOutApprovalOnly();

        var found = await Source().DiscoverAsync(NoFilters, CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo(ParentId.Replace("-", ""));
        await Assert.That(found[0].Vendor).IsEqualTo(HarnessId.Muse);
        await Assert.That(found[0].Cwd).IsEqualTo(WorkspaceRoot);
    }

    [Test]
    public async Task Discovery_skips_a_non_guid_directory_and_the_view_index() {
        LayOutProbe();
        LayOut(SessionDir("not-a-session"), "parent.session.jsonl");
        LayOut(Path.Combine(Paths.SessionsDir, ".msp-view-v1", "x", "y", "99999999-0f96-48cf-b6ff-ee03c8b9e69c"), "parent.session.jsonl");

        var found = await Source().DiscoverAsync(NoFilters, CancellationToken.None);

        await Assert.That(found.Select(f => f.SessionId)).IsEquivalentTo(new[] { ParentId.Replace("-", "") });
    }

    [Test]
    public async Task Discovery_dates_a_session_by_its_opened_record() {
        LayOutProbe();

        var found  = await Source().DiscoverAsync(NoFilters, CancellationToken.None);
        var opened = FromMicros(OpenedMicros);

        await Assert.That(found[0].FirstTimestamp).IsEqualTo(opened);
        await Assert.That(((IImportSource)Source()).DiscoveryAge(found[0])).IsEqualTo(opened);
    }

    [Test]
    public async Task Discovery_never_returns_a_child_log_as_a_session() {
        LayOutProbe();

        var found = await Source().DiscoverAsync(NoFilters, CancellationToken.None);

        await Assert.That(found.Select(f => f.SessionId)).DoesNotContain(GoalChild.Replace("-", ""));
    }

    [Test]
    public async Task A_session_filter_and_a_since_cutoff_narrow_discovery() {
        LayOutProbe();

        var other = await Source().DiscoverAsync(NoFilters with { FilterSession = new string('a', 32) }, CancellationToken.None);
        var late  = await Source().DiscoverAsync(NoFilters with { Since = new DateOnly(2026, 10, 14) }, CancellationToken.None);
        var hit   = await Source().DiscoverAsync(NoFilters with { FilterSession = ParentId.Replace("-", "") }, CancellationToken.None);

        await Assert.That(other).IsEmpty();
        await Assert.That(late).IsEmpty();
        await Assert.That(hit.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Is_available_only_when_the_sessions_dir_exists() {
        await Assert.That(Source().IsAvailable).IsFalse();

        LayOutProbe();

        await Assert.That(Source().IsAvailable).IsTrue();
    }

    [Test]
    public async Task The_source_declares_its_capabilities() {
        IImportSource source = Source();

        await Assert.That(source.Vendor).IsEqualTo(HarnessId.Muse);
        await Assert.That(source.SupportsTitleGeneration).IsFalse();
        await Assert.That(source.AttachesChildContentOnReplay).IsTrue();
    }

    [Test]
    public async Task Import_posts_each_childs_start_before_its_transcript_and_its_stop_after() {
        LayOutProbe();
        StubServer();

        var (classified, result) = await ImportProbeAsync();

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(result.SentChildContent).IsTrue();

        await Assert.That(PostedRoutes()).IsEquivalentTo(new[] {
            "/hooks/session-start/muse",
            "/hooks/transcript",
            "/hooks/subagent-start", "/hooks/transcript", "/hooks/subagent-stop",
            "/hooks/subagent-start", "/hooks/transcript", "/hooks/subagent-stop",
            "/hooks/session-end/muse",
        }, CollectionOrdering.Matching);

        var starts = new[] { PostedBody("/hooks/subagent-start", 0), PostedBody("/hooks/subagent-start", 1) };
        await Assert.That(starts.Select(s => (string)s["agent_id"]!)).IsEquivalentTo(new[] { GoalChild.Replace("-", ""), VerifyChild.Replace("-", "") });
        await Assert.That(starts.Select(s => (string)s["agent_type"]!)).IsEquivalentTo(new[] { "goal-reminder", "verify-reminder" });
        await Assert.That((string?)starts[0]["session_id"]).IsEqualTo(ParentId.Replace("-", ""));
        await Assert.That((string?)starts[0]["transcript_path"]).EndsWith("session.jsonl");
    }

    [Test]
    public async Task Import_stamps_the_lifecycle_posts_as_historical_with_the_logs_own_times() {
        var log = LayOutProbe();
        StubServer();

        await ImportProbeAsync();

        var start = PostedBody("/hooks/session-start/muse");
        await Assert.That((string?)start["session_id"]).IsEqualTo(ParentId.Replace("-", ""));
        await Assert.That((string?)start["transcript_path"]).IsEqualTo(log);
        await Assert.That((string?)start["source"]).IsEqualTo("startup");
        await Assert.That((string?)start["origin"]).IsEqualTo(ImportOrigins.Historical);
        await Assert.That(DateTimeOffset.Parse((string)start["started_at"]!, CultureInfo.InvariantCulture)).IsEqualTo(FromMicros(OpenedMicros));
        await Assert.That((string?)start["cwd"]).IsEqualTo(WorkspaceRoot);

        var end = PostedBody("/hooks/session-end/muse");
        await Assert.That((string?)end["origin"]).IsEqualTo(ImportOrigins.Historical);
        await Assert.That((string?)end["reason"]).IsEqualTo("Other");
        await Assert.That(DateTimeOffset.Parse((string)end["ended_at"]!, CultureInfo.InvariantCulture)).IsEqualTo(FromMicros(EndedMicros));
    }

    [Test]
    public async Task Import_tags_the_transcript_with_the_muse_vendor_and_each_child_with_its_agent_id() {
        LayOutProbe();
        StubServer();

        await ImportProbeAsync();

        var bodies = _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/hooks/transcript")
            .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)
            .ToArray();

        await Assert.That(bodies.All(b => (string?)b["vendor"] == "muse")).IsTrue();
        await Assert.That(bodies.Select(b => (string?)b["agent_id"])).IsEquivalentTo(new string?[] {
            null, GoalChild.Replace("-", ""), VerifyChild.Replace("-", ""),
        });
    }

    [Test]
    public async Task A_session_the_server_already_holds_in_full_replays_only_its_children() {
        LayOutProbe();
        StubServer(lastLine: 100);

        var (classified, result) = await ImportProbeAsync();

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(result.SentChildContent).IsTrue();
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/transcript")).IsEqualTo(2);
    }

    [Test]
    public async Task A_refused_subagent_start_leaves_that_child_without_content_or_stop() {
        LayOutProbe();
        StubServer();
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .AtPriority(-1)
            .RespondWith(Response.Create().WithStatusCode(400));

        var (_, result) = await ImportProbeAsync();

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(result.SentChildContent).IsFalse();
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/subagent-stop");
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/transcript")).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_session_start_fails_the_import_before_any_transcript() {
        LayOutProbe();
        StubServer();
        _server.Given(Request.Create().WithPath("/hooks/session-start/muse").UsingPost())
            .AtPriority(-1)
            .RespondWith(Response.Create().WithStatusCode(400));

        var (_, result) = await ImportProbeAsync();

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Failed);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/transcript");
    }

    async Task<ImportCommand.SessionClassification> ClassifyOnlyAsync(int minLines = 1) {
        using var client = new HttpClient();

        var source     = Source();
        var discovered = await source.DiscoverAsync(NoFilters, CancellationToken.None);

        return (await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: minLines, Home: Home), CancellationToken.None))[0];
    }

    [Test]
    public async Task A_resumed_session_ends_at_its_newest_end() {
        LayOut(SessionDir(ResumedId), "resumed.session.jsonl");
        StubServer();

        await ImportProbeAsync();

        var end = PostedBody("/hooks/session-end/muse");
        await Assert.That(DateTimeOffset.Parse((string)end["ended_at"]!, CultureInfo.InvariantCulture)).IsEqualTo(FromMicros(ResumedEndedMicros));
    }

    [Test]
    public async Task A_server_watermark_at_the_last_raw_index_is_already_loaded_and_sends_no_lines() {
        LayOut(SessionDir(ResumedId), "resumed.session.jsonl");
        StubServer(lastLine: ResumedLastIndex);

        var (classified, result) = await ImportProbeAsync();

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/transcript");
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);
    }

    [Test]
    public async Task A_watermark_one_short_of_the_last_index_is_partial_and_resumes_after_it() {
        LayOut(SessionDir(ResumedId), "resumed.session.jsonl");
        StubServer(lastLine: ResumedLastIndex - 1);

        var (classified, _) = await ImportProbeAsync();

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(ResumedLastIndex);
        await Assert.That(classified[0].TotalLines).IsEqualTo(6);
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/transcript")).IsEqualTo(1);
    }

    [Test]
    public async Task A_resume_still_running_posts_no_session_end() {
        LayOut(SessionDir(ResumedId), "resumed-open.session.jsonl");
        StubServer();

        var (classified, result) = await ImportProbeAsync();

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(PostedRoutes()).Contains("/hooks/session-start/muse");
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/session-end/muse");
    }

    [Test]
    public async Task A_log_that_never_ended_posts_no_session_end() {
        LayOut(SessionDir(ResumedId), "never-ended.session.jsonl");
        StubServer();

        var (_, result) = await ImportProbeAsync();

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/session-end/muse");
    }

    [Test]
    public async Task A_log_shorter_than_the_minimum_is_too_short() {
        LayOutProbe();
        StubServer();

        var classified = await ClassifyOnlyAsync(minLines: 1000);

        await Assert.That(classified.Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }

    [Test]
    public async Task A_log_that_vanishes_after_discovery_is_a_probe_error() {
        var log = LayOutProbe();
        StubServer();

        using var client = new HttpClient();
        var source     = Source();
        var discovered = await source.DiscoverAsync(NoFilters, CancellationToken.None);
        File.Delete(log);

        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 1, Home: Home), CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
        await Assert.That(classified[0].ProbeErrorReason).IsEqualTo("transcript read failed");
    }

    [Test]
    public async Task A_rejected_watermark_probe_is_a_probe_error() {
        LayOutProbe();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(400));

        var classified = await ClassifyOnlyAsync();

        await Assert.That(classified.Status).IsEqualTo(ImportCommand.ClassificationStatus.ProbeError);
        await Assert.That(classified.ProbeErrorReason).IsEqualTo("watermark probe failed");
    }
}
