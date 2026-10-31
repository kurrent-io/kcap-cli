using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap import</c>'s exit status, read off how the run ended.</summary>
public class ImportRunOutcomeTests {
    static ImportCommand.ImportRunOutcome Outcome(
            int loaded = 0, int alreadyLoaded = 0, int tooShort = 0, int excluded = 0,
            int probeError = 0, int errored = 0, int visibilityFailures = 0) =>
        new(new ImportCommand.FinalCounts(
                Loaded: loaded, Resumed: 0, AlreadyLoaded: alreadyLoaded, TooShort: tooShort, Excluded: excluded,
                ProbeError: probeError, Errored: errored, TitlesGenerated: 0, TitlesSkipped: 0, TitlesFailed: 0,
                SummariesGenerated: 0, SummariesFailed: 0, RanBackground: false, RequestedSummaries: false),
            visibilityFailures);

    [Test]
    public async Task A_run_where_nothing_failed_exits_zero() =>
        await Assert.That(Outcome(loaded: 3, alreadyLoaded: 2).ExitCode).IsEqualTo(0);

    [Test]
    public async Task Skipped_and_excluded_sessions_are_not_failures() =>
        await Assert.That(Outcome(tooShort: 1, excluded: 4).ExitCode).IsEqualTo(0);

    [Test]
    public async Task A_run_where_every_session_failed_exits_one() =>
        await Assert.That(Outcome(probeError: 7, excluded: 1).ExitCode).IsEqualTo(1);

    [Test]
    public async Task A_partial_failure_exits_one_too() =>
        await Assert.That(Outcome(loaded: 5, errored: 1).ExitCode).IsEqualTo(1);

    [Test]
    public async Task A_session_left_unprivatized_fails_the_run() =>
        await Assert.That(Outcome(loaded: 2, visibilityFailures: 1).ExitCode).IsEqualTo(1);
}
