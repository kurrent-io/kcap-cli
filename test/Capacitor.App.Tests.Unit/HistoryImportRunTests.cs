using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class HistoryImportRunTests {
    /// The totals line also counts sessions that never printed a line of their own, so it settles the
    /// processed count without counting the printed ones twice.
    [Test]
    public async Task The_totals_line_settles_the_processed_count() {
        var cli = new FakeKcapCli {
            ImportBehavior = (_, onLine, _) => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "  Loading s1 (claude)"));
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "  Loading s2 (claude)"));
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "  Skipping k1 (already current)"));
                onLine(new StreamedLine(ProcessStreamKind.Stdout, "  5 imported · 2 skipped · 1 failed"));
                return Task.FromResult(new StreamingResult(0, false, []));
            },
        };
        var run = new HistoryImportRun(0, 1, "from the last 90 days", 1, action => action());
        run.Start(cli, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
        await run.Completion;

        await Assert.That(run.Processed).IsEqualTo(8);
        await Assert.That(run.Imported).IsEqualTo(5);
    }

    /// The count reads against the selection's total only while that total is honest; with none, the bar
    /// measures passes instead.
    [Test]
    [Arguments(100, "25 of 100", 25.0)]
    [Arguments(0, "25", 0.0)]
    [Arguments(20, "25", 100.0)]
    public async Task A_running_import_counts_and_measures_against_the_selection(int expected, string count, double progress) {
        var gate = new TaskCompletionSource<StreamingResult>();
        Action<StreamedLine>? emit = null;
        var cli = new FakeKcapCli {
            ImportBehavior = (_, onLine, _) => {
                emit = onLine;
                return gate.Task;
            },
        };
        var run = new HistoryImportRun(expected, 1, "from the last 90 days", 1, action => action());
        run.Start(cli, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
        for (var i = 0; i < 25; i++) emit!(new StreamedLine(ProcessStreamKind.Stdout, $"  Loading s{i} (claude)"));

        var (shown, measured) = (run.ProcessedOfExpected, run.Progress);
        gate.SetResult(new StreamingResult(0, false, []));
        await run.Completion;

        await Assert.That(shown).IsEqualTo(count);
        await Assert.That(measured).IsEqualTo(progress);
    }

    /// With no count to measure against, progress is the share of passes that have completed.
    [Test]
    public async Task Without_a_count_progress_follows_completed_passes() {
        var gate = new TaskCompletionSource<StreamingResult>();
        var calls = 0;
        var cli = new FakeKcapCli {
            ImportBehavior = (_, _, _) => ++calls < 2 ? Task.FromResult(new StreamingResult(0, false, [])) : gate.Task,
        };
        var run = new HistoryImportRun(0, 1, "from the last 90 days", 2, action => action());
        run.Start(cli, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"]), new ImportRequest(ImportScopeChoice.Repo, null, [], ["c/d"])]);

        var afterFirst = run.Progress;
        gate.SetResult(new StreamingResult(0, false, []));
        await run.Completion;

        await Assert.That(afterFirst).IsEqualTo(50.0);
        await Assert.That(run.Progress).IsEqualTo(100.0);
    }
}
