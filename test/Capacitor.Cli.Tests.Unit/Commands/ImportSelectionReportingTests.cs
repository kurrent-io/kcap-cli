using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ParallelLimiter<SubprocessLimit>]
public class ImportSelectionReportingTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    readonly TempDir        _tmp    = new();

    public void Dispose() { _server.Dispose(); _tmp.Dispose(); }

    /// Seven solo Claude sessions with ascending timestamps; the newest five are selected.
    TempDirHandle Projects() {
        var projects = _tmp.CreateDir("projects");
        var dir = projects.CreateDir("-tmp-sel-proj");
        for (var i = 0; i < 7; i++)
            dir.CreateFile($"sess{i}.jsonl",
                [.. Enumerable.Range(0, 20).Select(n =>
                    $$$"""{"type":"user","timestamp":"2026-03-{{{(i + 1):00}}}T10:00:00Z","cwd":"/tmp/sel-proj","message":{"content":"line {{{n}}}"}}""")]);
        return projects;
    }

    void StubHooks() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-title" })
            _server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    ImportCommand Import() => new(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home,
        TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: new TestImportTerminal().Console);

    [Test]
    public async Task Capped_run_reports_selection_before_the_first_upload_and_a_partition_at_the_end() {
        StubHooks();
        var projects = Projects();
        ImportRunSelection? selected = null;
        var uploadsAtSelection = -1;
        ImportCommand.ImportRunOutcome? finished = null;

        var exit = await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            maxSessions: 5,
            onSelected: s => { selected = s; uploadsAtSelection = _server.LogEntries.Count(e => e.RequestMessage.Path.StartsWith("/hooks/session-start", StringComparison.Ordinal)); },
            onFinished: o => finished = o);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(selected).IsNotNull();
        await Assert.That(uploadsAtSelection).IsEqualTo(0);
        await Assert.That(selected!.SelectedIds.Count).IsEqualTo(5);
        await Assert.That(selected.RunCandidateIds.Count).IsEqualTo(7);
        await Assert.That(selected.RemainderExists).IsTrue();
        await Assert.That(finished!.Partition).IsNotNull();
        await Assert.That(finished.Partition!.SucceededIds).IsEquivalentTo(selected.SelectedIds);
        await Assert.That(finished.Partition.FailedIds).IsEmpty();
        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path.StartsWith("/hooks/session-start", StringComparison.Ordinal))).IsEqualTo(5);
    }

    /// <summary>Seven found, five imported now: the two the cap left are queued, so the Done totals account
    /// for all seven, and the output says how the work was divided before and after it ran.</summary>
    [Test]
    public async Task Capped_run_counts_and_names_the_sessions_left_for_the_background_import() {
        StubHooks();
        var projects = Projects();
        var terminal = new TestImportTerminal();
        ImportCommand.ImportRunOutcome? finished = null;

        await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: terminal.Console)
            .HandleImport(
                filterCwd: null, minLines: 1,
                sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
                scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
                maxSessions: 5, onFinished: o => finished = o);

        await Assert.That(finished!.Counts.Imported).IsEqualTo(5);
        await Assert.That(finished.Counts.Deferred).IsEqualTo(2);
        await Assert.That(finished.Counts.Failed).IsEqualTo(0);
        await Assert.That(finished.Counts.Total).IsEqualTo(7);

        var output = terminal.Plain.ToString();
        await Assert.That(output).Contains("Of 7 sessions found, 7 need importing: the newest 5 now, and 2 queued for the background import.");
        await Assert.That(output).Contains("5 imported · 0 skipped · 0 failed\n");
        await Assert.That(output).Contains(ImportCommand.DeferredNote(2));
    }

    [Test]
    public async Task Uncapped_run_reports_no_selection_and_no_partition() {
        StubHooks();
        var projects = Projects();
        var selectedFired = false;
        ImportCommand.ImportRunOutcome? finished = null;

        await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            onSelected: _ => selectedFired = true,
            onFinished: o => finished = o);

        await Assert.That(selectedFired).IsFalse();
        await Assert.That(finished!.Partition).IsNull();
    }

    [Test]
    public async Task Capped_run_over_an_empty_corpus_reports_an_empty_selection_and_partition() {
        StubHooks();
        var empty = _tmp.CreateDir("empty");
        ImportRunSelection? selected = null;
        ImportCommand.ImportRunOutcome? finished = null;

        await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, empty.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            maxSessions: 5, onSelected: s => selected = s, onFinished: o => finished = o);

        await Assert.That(selected).IsEqualTo(ImportRunSelection.Empty);
        await Assert.That(finished!.Partition).IsEqualTo(ImportRunPartition.Empty);
    }

    [Test]
    public async Task A_failed_upload_lands_in_FailedIds() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost()).RespondWith(Response.Create().WithStatusCode(400));
        var projects = Projects();
        ImportCommand.ImportRunOutcome? finished = null;

        await Import().HandleImport(
            filterCwd: null, minLines: 1,
            sources: [new ClaudeImportSource(Config.Root, projects.Path, router: new GitProviderRouter(), time: TimeProvider.System)],
            scope: new ImportScope.All(), skipConfirmation: true, skipTitle: true,
            maxSessions: 2, onFinished: o => finished = o);

        await Assert.That(finished!.Partition!.FailedIds.Count).IsEqualTo(2);
        await Assert.That(finished.Partition.SucceededIds).IsEmpty();
    }
}
