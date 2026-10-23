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
}
