using Capacitor.Cli.Commands;
using TUnit.Assertions.Enums;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DetachedImportPlanRunnerTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly List<(ImportPlanLevel Level, ProfileContext Context)> _seen = [];

    static SetupImportRun Reported() => new(0, ImportRunSelection.Empty, new(
        new ImportCommand.FinalCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false), 0), null);

    DetachedImportPlanRunner Runner(ProfileContext profiles, Func<ImportPlanLevel, SetupImportRun>? result = null) => new(
        Config.Root, profiles, Home, TestHarnesses.Under(Home),
        new ChosenServerHttp(Config.Root, profiles, ProfileOverrides.None, MachineAuth.None),
        new GitProviderRouter(), TimeProvider.System,
        (level, context) => {
            _seen.Add((level, context));

            return Task.FromResult(result?.Invoke(level) ?? Reported());
        });

    static ImportPlanLevel Level(FirstRunImportLevel level, string owner, string name) =>
        new(level, [new FirstRunImportChoice(owner, name, level)], null, null, true);

    string WritePlan(params ImportPlanLevel[] levels) => WritePlanAs("run1", levels);

    string WritePlanAs(string runId, params ImportPlanLevel[] levels) {
        var path = ImportPlan.PathFor(Config.Root, runId);
        new ImportPlan("https://new.example", levels).Write(path);

        return path;
    }

    [Test]
    public async Task Runs_levels_in_plan_order() {
        var path = WritePlan(Level(FirstRunImportLevel.Shared, "acme", "api"), Level(FirstRunImportLevel.OnlyMe, "acme", "secret"));

        var exit = await Runner(Resolutions.At("https://old.example", Config.Root)).RunAsync(path);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_seen.Select(s => s.Level.Level)).IsEquivalentTo(
            [FirstRunImportLevel.Shared, FirstRunImportLevel.OnlyMe], CollectionOrdering.Matching);
        await Assert.That(_seen[0].Level.Repos[0].Slug).IsEqualTo("acme/api");
    }

    [Test]
    public async Task Each_pass_gets_the_plans_server_not_the_profiles() {
        var path = WritePlan(Level(FirstRunImportLevel.OnlyMe, "a", "b"), Level(FirstRunImportLevel.Shared, "a", "c"));

        await Runner(Resolutions.At("https://old.example", Config.Root)).RunAsync(path);

        await Assert.That(_seen.Select(s => s.Context.Resolution.ServerUrl ?? "")).IsEquivalentTo(["https://new.example", "https://new.example"]);
    }

    [Test]
    public async Task Plan_mode_runs_with_no_configured_server() {
        var path = WritePlan(Level(FirstRunImportLevel.Shared, "a", "b"));

        var exit = await Runner(Resolutions.None(Config.Root)).RunAsync(path);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_seen.Single().Context.Resolution.ServerUrl).IsEqualTo("https://new.example");
    }

    [Test]
    public async Task An_unreadable_plan_fails_without_importing() {
        var garbage  = Config.CreateFile("import-plan-run1.json", "not a plan");
        var insecure = Config.PathTo("import-plan-run2.json");
        new ImportPlan("http://new.example", [Level(FirstRunImportLevel.Shared, "a", "b")]).Write(insecure);

        var missing = await Runner(Resolutions.None(Config.Root)).RunAsync(Config.PathTo("import-plan-none.json"));
        var invalid = await Runner(Resolutions.None(Config.Root)).RunAsync(garbage);
        var cleartext = await Runner(Resolutions.None(Config.Root)).RunAsync(insecure);

        await Assert.That(missing).IsEqualTo(1);
        await Assert.That(invalid).IsEqualTo(1);
        await Assert.That(cleartext).IsEqualTo(1);
        await Assert.That(_seen).IsEmpty();
    }

    [Test]
    public async Task A_rejected_plan_file_in_the_config_directory_is_removed() {
        var garbage = Config.CreateFile("import-plan-run1.json", "not a plan");

        await Runner(Resolutions.None(Config.Root)).RunAsync(garbage);

        await Assert.That(File.Exists(garbage)).IsFalse();
    }

    [Test]
    public async Task A_path_that_is_not_a_setup_plan_file_is_left_alone() {
        using var elsewhere = new TempDir();
        var outside   = elsewhere.CreateFile("import-plan-run1.json", "not a plan");
        var misnamed  = Config.CreateFile("config.json", "not a plan");
        var nested    = Config.CreateFile(Path.Combine("sub", "import-plan-run1.json"), "not a plan");

        await Runner(Resolutions.None(Config.Root)).RunAsync(outside);
        await Runner(Resolutions.None(Config.Root)).RunAsync(misnamed);
        await Runner(Resolutions.None(Config.Root)).RunAsync(nested);

        await Assert.That(File.Exists(outside)).IsTrue();
        await Assert.That(File.Exists(misnamed)).IsTrue();
        await Assert.That(File.Exists(nested)).IsTrue();
    }

    [Test]
    public async Task A_level_that_reports_failures_fails_the_run_and_the_next_still_runs() {
        var path = WritePlan(Level(FirstRunImportLevel.OnlyMe, "a", "b"), Level(FirstRunImportLevel.Shared, "a", "c"));
        var failedSessions = new SetupImportRun(0, ImportRunSelection.Empty, new(
            new ImportCommand.FinalCounts(0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false), 0), null);
        var failedVisibility = new SetupImportRun(0, ImportRunSelection.Empty, new(
            new ImportCommand.FinalCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, RanBackground: false, RequestedSummaries: false), 1), null);

        var sessions = await Runner(Resolutions.None(Config.Root),
            l => l.Level is FirstRunImportLevel.OnlyMe ? failedSessions : Reported()).RunAsync(path);
        var ran = _seen.Count;
        var visibility = await Runner(Resolutions.None(Config.Root),
            l => l.Level is FirstRunImportLevel.OnlyMe ? failedVisibility : Reported()).RunAsync(WritePlanAs("run2",
                Level(FirstRunImportLevel.OnlyMe, "a", "b"), Level(FirstRunImportLevel.Shared, "a", "c")));

        await Assert.That(sessions).IsEqualTo(1);
        await Assert.That(ran).IsEqualTo(2);
        await Assert.That(visibility).IsEqualTo(1);
        await Assert.That(_seen.Count).IsEqualTo(4);
    }

    [Test]
    public async Task A_failed_level_does_not_stop_the_next_and_fails_the_run() {
        var path = WritePlan(Level(FirstRunImportLevel.OnlyMe, "a", "b"), Level(FirstRunImportLevel.Shared, "a", "c"));

        var exit = await Runner(Resolutions.None(Config.Root),
            l => l.Level is FirstRunImportLevel.OnlyMe ? new SetupImportRun(1, null, null, new IOException("disk")) : Reported())
            .RunAsync(path);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_seen.Count).IsEqualTo(2);
    }

    [Test]
    public async Task The_plan_file_is_deleted_after_the_run() {
        var path = WritePlan(Level(FirstRunImportLevel.Shared, "a", "b"));

        await Runner(Resolutions.None(Config.Root)).RunAsync(path);

        await Assert.That(File.Exists(path)).IsFalse();
    }
}
