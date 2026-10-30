using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportExitCodeTests {
    static ImportCommand.ImportRunOutcome Outcome(
            int loaded = 0, int alreadyLoaded = 0, int excluded = 0, int probeError = 0, int errored = 0, int visibilityFailures = 0) =>
        new(new ImportCommand.FinalCounts(loaded, 0, alreadyLoaded, 0, excluded, probeError, errored, 0, 0, 0, 0, 0,
                                          RanBackground: false, RequestedSummaries: false),
            visibilityFailures);

    [Test]
    public async Task A_run_where_nothing_failed_exits_zero() {
        await Assert.That(ImportExitCode.For(0, Outcome(loaded: 3, alreadyLoaded: 2, excluded: 1))).IsEqualTo(0);
        await Assert.That(ImportExitCode.For(0, Outcome(excluded: 4))).IsEqualTo(0);
    }

    [Test]
    public async Task A_run_where_nothing_landed_exits_one() {
        await Assert.That(ImportExitCode.For(0, Outcome(probeError: 8))).IsEqualTo(1);
        await Assert.That(ImportExitCode.For(0, Outcome(probeError: 7, excluded: 1))).IsEqualTo(1);
        await Assert.That(ImportExitCode.For(0, Outcome(errored: 2))).IsEqualTo(1);
    }

    [Test]
    public async Task A_run_where_some_landed_and_some_failed_exits_partial() {
        await Assert.That(ImportExitCode.For(0, Outcome(loaded: 1, probeError: 1))).IsEqualTo(ImportExitCode.Partial);
        await Assert.That(ImportExitCode.For(0, Outcome(alreadyLoaded: 1, errored: 1))).IsEqualTo(ImportExitCode.Partial);
        await Assert.That(ImportExitCode.For(0, Outcome(loaded: 2, visibilityFailures: 1))).IsEqualTo(ImportExitCode.Partial);
    }

    [Test]
    public async Task A_refusal_or_a_run_without_an_outcome_keeps_its_own_code() {
        await Assert.That(ImportExitCode.For(1, Outcome(loaded: 1))).IsEqualTo(1);
        await Assert.That(ImportExitCode.For(1, null)).IsEqualTo(1);
        await Assert.That(ImportExitCode.For(0, null)).IsEqualTo(0);
    }
}
