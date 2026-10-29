using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Tests.Unit;

public class LocalInstallOutcomeTests {
    static LocalSetupProgress Run(params string[] lines) {
        var progress = new LocalSetupProgress();
        foreach (var text in lines) progress.Apply(LocalSetupLine.Parse(text)!);
        return progress;
    }

    [Test]
    public async Task Committed_with_exit_130_settles_installed() {
        var p = Run("{\"v\":1,\"event\":\"committed\",\"server_url\":\"http://localhost:5000\"}");

        var outcome = LocalInstallOutcome.Settle(p, 130, false);

        await Assert.That(outcome).IsEqualTo(new LocalInstallOutcome.Installed("http://localhost:5000", "fresh"));
    }

    [Test]
    public async Task Commit_failed_before_the_profile_is_retryable_and_locked_when_the_journal_stands() {
        var p = Run("{\"v\":1,\"event\":\"failed\",\"code\":\"commit_failed\",\"message\":\"run setup again\"}");

        var outcome = LocalInstallOutcome.Settle(p, 1, true);

        await Assert.That(outcome).IsEqualTo(
            new LocalInstallOutcome.Failed("commit_failed", "run setup again", LocalInstallAffordance.Retry, true));
    }

    [Test]
    public async Task A_non_zero_exit_with_no_lines_is_a_retryable_generic_failure() {
        var outcome = LocalInstallOutcome.Settle(new LocalSetupProgress(), 2, false);

        await Assert.That(outcome).IsEqualTo(new LocalInstallOutcome.Failed(
            null, "The install stopped before it finished (exit 2).", LocalInstallAffordance.Retry, false));
    }

    [Test]
    [Arguments("lifecycle_busy", LocalInstallAffordance.Busy)]
    [Arguments("other_server", LocalInstallAffordance.Permanent)]
    [Arguments("client_below_floor", LocalInstallAffordance.Retry)]
    [Arguments("cancelled", LocalInstallAffordance.Retry)]
    [Arguments("verification_failed", LocalInstallAffordance.Retry)]
    [Arguments(null, LocalInstallAffordance.Retry)]
    public async Task Affordance_follows_the_code_alone(string? code, LocalInstallAffordance expected) =>
        await Assert.That(LocalInstallAffordance.For(code)).IsEqualTo(expected);
}
