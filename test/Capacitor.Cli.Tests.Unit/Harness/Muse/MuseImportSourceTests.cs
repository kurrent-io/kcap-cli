using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Harness.Muse;
using Microsoft.Extensions.Time.Testing;
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

    WatcherPaths Watchers => new(Tmp.PathTo("watchers"));

    MuseImportSource Source(TimeProvider? time = null) => new(Paths, Watchers, time ?? TimeProvider.System);

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

    async Task<(IReadOnlyList<ImportCommand.SessionClassification> Classified, ImportSessionResult Result)> ImportProbeAsync(
            TimeProvider? time = null) {
        using var client = new HttpClient();

        var source     = Source(time);
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

    /// <summary>Directory order is the filesystem's, so the readable copy takes each of the two places in
    /// turn: whichever is enumerated first, only the readable copy may claim the id.</summary>
    [Test]
    public async Task Discovery_keeps_a_readable_copy_of_a_session_whose_other_copy_is_not() {
        var a = LayOut(Path.Combine(Paths.SessionsDir, "2026", "09", "01", ResumedId), "approval-only.session.jsonl");
        var b = LayOut(SessionDir(ResumedId), "resumed.session.jsonl");

        foreach (var readable in new[] { b, a }) {
            File.WriteAllText(a, Fixture(readable == a ? "resumed.session.jsonl" : "approval-only.session.jsonl"));
            File.WriteAllText(b, Fixture(readable == b ? "resumed.session.jsonl" : "approval-only.session.jsonl"));

            var found = await Source().DiscoverAsync(NoFilters, CancellationToken.None);

            await Assert.That(found.Count).IsEqualTo(1);
            await Assert.That((string?)found[0].SourceMeta!["LogPath"]).IsEqualTo(readable);
        }
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

    /// <summary>The import fails rather than reporting a loaded parent, and holds the parent's end
    /// back, so a re-import retries the children inside the parent's lifecycle.</summary>
    [Test]
    public async Task A_refused_subagent_start_fails_the_import_without_that_childs_content_stop_or_the_end() {
        LayOutProbe();
        StubServer();
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .AtPriority(-1)
            .RespondWith(Response.Create().WithStatusCode(500));

        var (_, result) = await ImportProbeAsync();

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Failed);
        await Assert.That(result.SentChildContent).IsFalse();
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/subagent-stop");
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/session-end/muse");
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/transcript")).IsEqualTo(1);
    }

    [Test]
    public async Task A_refused_subagent_stop_fails_the_import_and_holds_back_the_end() {
        LayOutProbe();
        StubServer();
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .AtPriority(-1)
            .RespondWith(Response.Create().WithStatusCode(500));

        var (_, result) = await ImportProbeAsync();

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Failed);
        await Assert.That(result.SentChildContent).IsTrue();
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/session-end/muse");
    }

    const string DoneWorker    = "01a13565-a626-7f70-bda0-1c19ba4deadf";
    const string RunningWorker = "01a13565-b000-7000-8000-000000000001";

    static string BoundLine(string subagentId, string childId) =>
        """{"schema_version":1,"id":"RECORD","stream":{"kind":"session","id":"PARENT"},"sequence":300,"recorded_at":1791916430000000,"record_type":"event","payload_type":"subagent.control.child_session_bound","payload_schema_version":1,"payload":{"kind":"subagent_control","record":{"kind":"child_session_bound","schema_version":1,"subagent_id":"SUBAGENT","child_session_id":"CHILD","storage_layout":"nested_session_v1"}}}""".Replace("RECORD", Guid.NewGuid().ToString()).Replace("PARENT", ParentId).Replace("SUBAGENT", subagentId).Replace("CHILD", childId);

    static string ReadyLine(string subagentId) =>
        """{"schema_version":1,"id":"RECORD","stream":{"kind":"session","id":"PARENT"},"sequence":301,"recorded_at":1791916431000000,"record_type":"event","payload_type":"subagent.control.result_ready","payload_schema_version":2,"payload":{"kind":"subagent_control","record":{"kind":"result_ready","subagent_id":"SUBAGENT","summary":"done","evidence_refs":[]}}}""".Replace("RECORD", Guid.NewGuid().ToString()).Replace("PARENT", ParentId).Replace("SUBAGENT", subagentId);

    static string[] ParentLines => Fixture("parent.session.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    static string EndLine => ParentLines.Single(l => l.Contains("\"session.end\"", StringComparison.Ordinal));

    /// <summary>The probe with its end cut off, plus two workers: one whose result the parent reports
    /// and one still running.</summary>
    string LayOutUnfinishedProbeWithWorkers() {
        var log = LayOutProbe();
        File.WriteAllLines(log, ParentLines.Where(l => l != EndLine).Concat([
            BoundLine("01a13565-a5b7-7071-874a-8c22f461fc24", DoneWorker),
            BoundLine("01a13565-b001-7000-8000-000000000002", RunningWorker),
            ReadyLine("01a13565-a5b7-7071-874a-8c22f461fc24"),
        ]));

        var parent = SessionDir(ParentId);
        foreach (var worker in new[] { DoneWorker, RunningWorker })
            Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(parent, "subagent", worker, "session.jsonl")), Fixture("child-goal.session.jsonl"));

        return log;
    }

    // The last record LayOutUnfinishedProbeWithWorkers writes.
    static readonly DateTimeOffset UnfinishedLastRecord = FromMicros(1791916431000000);

    static void LastWrittenAt(string parentLog, DateTimeOffset at) {
        File.SetLastWriteTimeUtc(parentLog, at.UtcDateTime);
        foreach (var child in MuseSubagentDiscovery.EnumerateChildLogs(parentLog)) File.SetLastWriteTimeUtc(child, at.UtcDateTime);
    }

    /// <summary>A worker can be re-engaged after its result, so a stop from the import would end the
    /// live child watcher early: under a parent active within the server's stale window, not even a
    /// reported worker is stopped.</summary>
    [Test]
    public async Task Under_a_recently_active_unfinished_parent_no_child_is_stopped_even_one_whose_result_is_in() {
        var log = LayOutUnfinishedProbeWithWorkers();
        LastWrittenAt(log, UnfinishedLastRecord);
        StubServer();

        var (_, result) = await ImportProbeAsync(new FakeTimeProvider(UnfinishedLastRecord + MuseImportSource.AbandonedAfter));

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/subagent-start")).IsEqualTo(4);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/subagent-stop");
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/session-end/muse");
    }

    /// <summary>An unfinished log idle past the server's stale window with no watcher left on this machine
    /// has nothing to stop its children, and the server's stale close completes none, so the import stops
    /// every one. The session's own end stays with that stale close.</summary>
    [Test]
    public async Task Under_an_idle_unfinished_parent_with_no_live_watcher_every_child_is_stopped_but_the_session_is_not_ended() {
        var log = LayOutUnfinishedProbeWithWorkers();
        LastWrittenAt(log, UnfinishedLastRecord);
        StubServer();

        var now = UnfinishedLastRecord + MuseImportSource.AbandonedAfter + TimeSpan.FromSeconds(1);
        var (_, result) = await ImportProbeAsync(new FakeTimeProvider(now));

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);

        var childRoutes = PostedRoutes().Where(r => r.StartsWith("/hooks/subagent-", StringComparison.Ordinal)).ToArray();
        await Assert.That(childRoutes).IsEquivalentTo(
            Enumerable.Repeat(new[] { "/hooks/subagent-start", "/hooks/subagent-stop" }, 4).SelectMany(r => r), CollectionOrdering.Matching);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/session-end/muse");
    }

    /// <summary>A live session can wait on its user past the stale window. While its watcher still runs,
    /// that watcher owns the children's stops, and one posted now would complete a worker the user may
    /// re-engage.</summary>
    [Test]
    public async Task Under_an_idle_unfinished_parent_whose_watcher_still_runs_no_child_is_stopped() {
        var log = LayOutUnfinishedProbeWithWorkers();
        LastWrittenAt(log, UnfinishedLastRecord);
        StubServer();

        using var watcher = DummyProcess.StartSleep(30);
        Directory.CreateDirectory(Watchers.Directory);
        await File.WriteAllTextAsync(Watchers.PidFile(Guid.Parse(ParentId).ToString("N")),
            $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        var now = UnfinishedLastRecord + MuseImportSource.AbandonedAfter + TimeSpan.FromHours(2);
        var (_, result) = await ImportProbeAsync(new FakeTimeProvider(now));

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/subagent-start")).IsEqualTo(4);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/subagent-stop");
    }

    /// <summary>A parent watcher killed outright leaves its child watchers running; any one of them
    /// alive means the stops are still the live scan's, for every child.</summary>
    [Test]
    public async Task Under_an_idle_unfinished_parent_with_a_child_watcher_still_running_no_child_is_stopped() {
        var log = LayOutUnfinishedProbeWithWorkers();
        LastWrittenAt(log, UnfinishedLastRecord);
        StubServer();

        var childLog   = MuseSubagentDiscovery.EnumerateChildLogs(log)[0];
        var childLabel = MuseSubagentDiscovery.LabelFor(
            Path.GetFileName(Path.GetDirectoryName(childLog))!, MuseSubagentDiscovery.ResolveChildLabels(log));

        using var watcher = DummyProcess.StartSleep(30);
        Directory.CreateDirectory(Watchers.Directory);
        await File.WriteAllTextAsync(Watchers.PidFile($"{Guid.Parse(ParentId):N}-{childLabel.AgentId}"),
            $"{watcher.Pid}\n{PidIdentity.Capture(watcher.Pid)}");

        var now = UnfinishedLastRecord + MuseImportSource.AbandonedAfter + TimeSpan.FromHours(2);
        var (_, result) = await ImportProbeAsync(new FakeTimeProvider(now));

        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(PostedRoutes().Count(r => r == "/hooks/subagent-start")).IsEqualTo(4);
        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/subagent-stop");
    }

    /// <summary>A worker still writing its own log keeps an otherwise idle parent from reading as abandoned.</summary>
    [Test]
    public async Task A_child_written_within_the_stale_window_keeps_an_idle_parent_from_being_abandoned() {
        var log = LayOutUnfinishedProbeWithWorkers();
        LastWrittenAt(log, UnfinishedLastRecord);
        var now = UnfinishedLastRecord + MuseImportSource.AbandonedAfter + TimeSpan.FromMinutes(10);
        File.SetLastWriteTimeUtc(MuseSubagentDiscovery.EnumerateChildLogs(log)[0], (now - TimeSpan.FromMinutes(1)).UtcDateTime);
        StubServer();

        await ImportProbeAsync(new FakeTimeProvider(now));

        await Assert.That(PostedRoutes()).DoesNotContain("/hooks/subagent-stop");
    }

    [Test]
    public async Task A_reimport_after_the_parent_ends_stops_every_child_before_the_end() {
        var log = LayOutUnfinishedProbeWithWorkers();
        StubServer();
        var (first, _) = await ImportProbeAsync();

        File.AppendAllLines(log, [EndLine]);
        _server.Reset();
        StubServer(lastLine: (int)first[0].SourceMeta!["LastLine"]!);

        var (classified, result) = await ImportProbeAsync();

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(result.Outcome).IsEqualTo(ImportOutcome.Resumed);

        var routes = PostedRoutes();
        await Assert.That(routes.Count(r => r == "/hooks/subagent-stop")).IsEqualTo(4);
        await Assert.That(routes.Last()).IsEqualTo("/hooks/session-end/muse");
    }

    /// <summary>Only a strict request makes the server refuse a child lifecycle write it failed to record.</summary>
    [Test]
    public async Task Child_lifecycle_posts_are_strict() {
        LayOutProbe();
        StubServer();

        await ImportProbeAsync();

        foreach (var route in new[] { "/hooks/subagent-start", "/hooks/subagent-stop" })
            for (var i = 0; i < 2; i++)
                await Assert.That((bool?)PostedBody(route, i)["strict"]).IsTrue();
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

    /// <summary>A 410 is the server telling the owner they deleted the session: counted, never retried as
    /// a failure and never loaded again as new.</summary>
    [Test]
    public async Task A_session_deleted_on_the_server_is_deleted_not_a_probe_error() {
        LayOutProbe();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410).WithBody("""{"error":"session_deleted"}"""));

        var classified = await ClassifyOnlyAsync();

        await Assert.That(classified.Status).IsEqualTo(ImportCommand.ClassificationStatus.Deleted);
        await Assert.That(classified.ProbeErrorReason).IsNull();
    }
}
