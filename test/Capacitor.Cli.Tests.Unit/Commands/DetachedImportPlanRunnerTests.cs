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

    string WritePlan(params ImportPlanLevel[] levels) {
        var path = ImportPlan.PathFor(Config.Root, "run1");
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
        var garbage = Config.CreateFile("import-plan-run1.json", "not a plan");

        var missing = await Runner(Resolutions.None(Config.Root)).RunAsync(Config.PathTo("import-plan-none.json"));
        var invalid = await Runner(Resolutions.None(Config.Root)).RunAsync(garbage);

        await Assert.That(missing).IsEqualTo(1);
        await Assert.That(invalid).IsEqualTo(1);
        await Assert.That(_seen).IsEmpty();
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
