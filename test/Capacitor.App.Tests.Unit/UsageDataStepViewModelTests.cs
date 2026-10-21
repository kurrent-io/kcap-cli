using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

public class UsageDataStepViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe Desktop() {
        var probe = TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl), Config.Root);
        TelemetryTestGuards.AssertEnabled("app", Config.Root, probe.Telemetry);

        return probe;
    }

    [Test]
    public async Task The_page_is_left_out_once_the_notice_was_acknowledged() {
        var probe = Desktop();
        new UsageDataNotice(probe.Telemetry, Config.Root).Acknowledge();

        await Assert.That(new UsageDataStepViewModel(new UsageDataNotice(probe.Telemetry, Config.Root)).Applicable).IsFalse();
        await Assert.That(new UsageDataStepViewModel(null).Applicable).IsFalse();
    }

    [Test]
    public async Task Continue_with_sharing_acknowledges_and_keeps_usage_data_on() {
        var probe  = Desktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        var step   = new UsageDataStepViewModel(notice);
        await Assert.That(step.Applicable).IsTrue();
        await Assert.That(step.Share).IsTrue();

        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();

        await Assert.That(notice.Visible).IsFalse();
        await Assert.That(probe.Telemetry.Enabled).IsTrue();
        await Assert.That(new UsageDataNotice(probe.Telemetry, Config.Root).Visible).IsFalse();
    }

    [Test]
    public async Task Continue_with_dont_share_turns_usage_data_off() {
        var probe  = Desktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        var step   = new UsageDataStepViewModel(notice) { DontShare = true };

        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();

        await Assert.That(notice.Visible).IsFalse();
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root) == false).IsTrue();
    }

    [Test]
    public async Task Back_records_nothing() {
        var probe  = Desktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        var step   = new UsageDataStepViewModel(notice) { DontShare = true };

        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None)).IsTrue();

        await Assert.That(notice.Visible).IsTrue();
        await Assert.That(probe.Telemetry.Enabled).IsTrue();
    }

    /// <summary>The page holds once to say the opt-out will not last, then hides the choice and lets setup
    /// finish with the notice still due: a Share picked afterwards would hide it while nothing sends.</summary>
    [Test]
    public async Task An_unsaved_opt_out_holds_the_page_once_and_keeps_the_notice_due() {
        var probe  = Desktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        var step   = new UsageDataStepViewModel(notice) { DontShare = true };
        var path   = Config.Root.Path("telemetry.json");
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(path);

        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsFalse();
        await Assert.That(step.Problem).IsEqualTo(UsageDataNotice.NotSaved);
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(step.ChoiceVisible).IsFalse();

        step.Share = true;
        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();

        await Assert.That(notice.Visible).IsTrue();
        await Assert.That(notice.Message).IsEqualTo(UsageDataNotice.NotSaved);
    }
}
