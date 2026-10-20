using Capacitor.App.Services.LocalSetup;

namespace Capacitor.App.Tests.Unit;

public class LocalUpgradeOutcomeTests {
    static LocalUpgradeProgress Run(params string[] lines) {
        var progress = new LocalUpgradeProgress();
        foreach (var text in lines) progress.Apply(LocalSetupLine.Parse(text)!);
        return progress;
    }

    [Test]
    public async Task Exit_0_is_upgraded() {
        var p = Run(UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Upgraded("1.2.0", "1.3.0"));

        var outcome = LocalUpgradeOutcome.Settle(p, 0, null);

        await Assert.That(outcome).IsEqualTo(new LocalUpgradeOutcome.Upgraded("1.2.0", "1.3.0"));
    }

    [Test]
    [Arguments("current")]
    [Arguments("stack_newer")]
    [Arguments("not_installed")]
    public async Task Exit_3_is_nothing_to_do(string reason) {
        var p = Run(UpgradeScript.NothingToDo(reason, "1.2.0", "1.2.0"));

        var outcome = LocalUpgradeOutcome.Settle(p, 3, null);

        await Assert.That(outcome).IsEqualTo(new LocalUpgradeOutcome.NothingToDo(reason));
    }

    [Test]
    public async Task Exit_4_is_not_published_rather_than_a_failure() {
        var p = Run(UpgradeScript.Failed("bundle_not_published", "No bundle is published for 1.3.0 yet.", false));

        var outcome = LocalUpgradeOutcome.Settle(p, 4, null);

        await Assert.That(outcome).IsEqualTo(new LocalUpgradeOutcome.NotPublished("No bundle is published for 1.3.0 yet."));
    }

    [Test]
    public async Task Exit_1_failed_with_the_old_version_kept() {
        var p = Run(
            UpgradeScript.Planned("1.2.0", "1.3.0"),
            UpgradeScript.Failed("download_failed", "The download failed. Capacitor local 1.2.0 was left as it was.", false));

        var outcome = LocalUpgradeOutcome.Settle(p, 1, null);

        await Assert.That(outcome).IsEqualTo(new LocalUpgradeOutcome.Failed(
            "download_failed", "The download failed. Capacitor local 1.2.0 was left as it was.", false,
            LocalInstallAffordance.Retry, "1.2.0", "1.3.0"));
    }

    [Test]
    public async Task Exit_2_failed_after_the_switch() {
        var p = Run(
            UpgradeScript.Planned("1.2.0", "1.3.0"),
            UpgradeScript.Switched("1.3.0"),
            UpgradeScript.Failed("start_failed", "Capacitor local 1.3.0 did not start.", true));

        var outcome = LocalUpgradeOutcome.Settle(p, 2, null);

        await Assert.That(outcome).IsTypeOf<LocalUpgradeOutcome.Failed>();
        await Assert.That(((LocalUpgradeOutcome.Failed)outcome).Switched).IsTrue();
    }

    /// A child killed after its switch line exits with a signal's code, not 2: the line still says
    /// the new version is selected.
    [Test]
    public async Task A_switch_line_makes_any_failure_one_after_the_switch() {
        var p = Run(UpgradeScript.Planned("1.2.0", "1.3.0"), UpgradeScript.Switched("1.3.0"));

        var outcome = LocalUpgradeOutcome.Settle(p, 137, null);

        await Assert.That(((LocalUpgradeOutcome.Failed)outcome).Switched).IsTrue();
    }

    [Test]
    public async Task A_failure_with_no_line_explains_itself_from_stderr_or_the_exit_code() {
        var fromStderr = LocalUpgradeOutcome.Settle(new LocalUpgradeProgress(), -1, "kcap CLI not found");
        var fromExit   = LocalUpgradeOutcome.Settle(new LocalUpgradeProgress(), 137, null);

        await Assert.That(((LocalUpgradeOutcome.Failed)fromStderr).Message).IsEqualTo("kcap CLI not found");
        await Assert.That(((LocalUpgradeOutcome.Failed)fromExit).Message).IsEqualTo("The upgrade stopped before it finished (exit 137).");
        await Assert.That(((LocalUpgradeOutcome.Failed)fromExit).Affordance).IsEqualTo(LocalInstallAffordance.Retry);
    }
}
