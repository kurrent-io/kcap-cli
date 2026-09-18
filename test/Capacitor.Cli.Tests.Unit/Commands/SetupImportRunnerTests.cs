using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SetupImportRunnerTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    SetupImportRunner Runner(ProfileContext profiles) => new(
        Config.Root, Home, TestHarnesses.Under(Home),
        new ChosenServerHttp(Config.Root, profiles, ProfileOverrides.None, MachineAuth.None),
        new GitProviderRouter(), TimeProvider.System);

    /// Seven solo Claude sessions with ascending timestamps under the real Claude harness path
    /// (mirrors ImportSelectionReportingTests.Projects, rooted at Home's actual `.claude/projects`
    /// since the real runner always builds its own sources from the harness registry).
    static void SeedClaudeSessions(string projectsRoot) {
        var dir = Path.Combine(projectsRoot, "-tmp-sel-proj");
        Directory.CreateDirectory(dir);

        for (var i = 0; i < 7; i++) {
            File.WriteAllLines(Path.Combine(dir, $"sess{i}.jsonl"),
                Enumerable.Range(0, 20).Select(n =>
                    $$$"""{"type":"user","timestamp":"2026-03-{{{(i + 1):00}}}T10:00:00Z","cwd":"/tmp/sel-proj","message":{"content":"line {{{n}}}"}}"""));
        }
    }

    static void StubServer(WireMockServer server) {
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"provider":"None"}"""));
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/session-start*", "/hooks/subagent-start", "/hooks/subagent-stop", "/hooks/session-title" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
    }

    [Test]
    public async Task Discovery_over_an_empty_home_reports_a_result_and_no_fault() {
        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);

        var discovery = await Runner(profiles).DiscoverAsync(profiles);

        await Assert.That(discovery.Fault).IsNull();
        await Assert.That(discovery.Result).IsNotNull();
        await Assert.That(discovery.Result!.Summary.Repos).IsEmpty();
    }

    [Test]
    public async Task A_run_over_an_empty_home_is_complete_with_an_empty_selection() {
        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);

        var run = await Runner(profiles).RunAsync(new ImportInvocation(
            new ImportScope.All(), MaxSessions: 5, CurrentRepo: null, DefaultVisibility: "private",
            AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: false, Profiles: profiles));

        await Assert.That(run.Fault).IsNull();
        await Assert.That(run.Selection).IsEqualTo(ImportRunSelection.Empty);
        await Assert.That(run.Outcome!.Partition).IsEqualTo(ImportRunPartition.Empty);
    }

    // Claude's per-file and per-project-dir scans all fail open (a hostile transcript or a locked
    // sub-directory is swallowed so one bad file cannot abort the whole scan) — so the only
    // unguarded read left is the top-level `Directory.GetDirectories` in `DiscoverTranscripts`,
    // which is what this test locks out.
    [Test]
    public async Task A_run_that_cannot_scan_its_projects_directory_surfaces_the_fault_without_propagating() {
        if (OperatingSystem.IsWindows()) return;

        var profiles = Resolutions.At("http://127.0.0.1:1", Config.Root);
        var projects = Path.Combine(Home.Path, ".claude", "projects");
        Directory.CreateDirectory(projects);
        File.SetUnixFileMode(projects, UnixFileMode.None);

        try {
            var run = await Runner(profiles).RunAsync(new ImportInvocation(
                new ImportScope.All(), MaxSessions: null, CurrentRepo: null, DefaultVisibility: "private",
                AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: true, Profiles: profiles));

            await Assert.That(run.Fault).IsNotNull();
            await Assert.That(run.Selection).IsNull();
            await Assert.That(run.Outcome).IsNull();
        } finally {
            File.SetUnixFileMode(projects,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Test]
    public async Task A_capped_run_over_a_real_corpus_completes_with_a_populated_outcome_and_partition() {
        using var server = WireMockServer.Start();
        StubServer(server);
        SeedClaudeSessions(Path.Combine(Home.Path, ".claude", "projects"));

        var profiles = Resolutions.At(server.Url!, Config.Root);

        var run = await Runner(profiles).RunAsync(new ImportInvocation(
            new ImportScope.All(), MaxSessions: 5, CurrentRepo: null, DefaultVisibility: null,
            AutoSkipExclusions: true, ForcePrivate: false, SkipTitle: true, Profiles: profiles));

        await Assert.That(run.Fault).IsNull();
        await Assert.That(run.Selection).IsNotNull();
        await Assert.That(run.Selection!.SelectedIds.Count).IsEqualTo(5);
        await Assert.That(run.Selection.RunCandidateIds.Count).IsEqualTo(7);
        await Assert.That(run.Selection.RemainderExists).IsTrue();
        await Assert.That(run.Outcome).IsNotNull();
        await Assert.That(run.Outcome!.Partition).IsNotNull();
        await Assert.That(run.Outcome.Partition!.SucceededIds).IsEquivalentTo(run.Selection.SelectedIds);
        await Assert.That(run.Outcome.Partition.FailedIds).IsEmpty();

        // The Complete invariant from the spec: every selected id landed in exactly one bucket.
        var accounted = run.Outcome.Partition.SucceededIds.Count
                       + run.Outcome.Partition.SkippedIds.Count
                       + run.Outcome.Partition.FailedIds.Count;
        await Assert.That(accounted).IsEqualTo(run.Selection.SelectedIds.Count);
    }
}
