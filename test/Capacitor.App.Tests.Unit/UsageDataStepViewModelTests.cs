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

    // The local edition: the app waits for an answer before it sends anything.

    TelemetryProbe LocalDesktop() =>
        TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl, UnsavedConsent.Pending), Config.Root);

    /// A kcap command on a machine with the local edition, which reads the same saved choice.
    bool CommandSends() =>
        TelemetryProbe.Start(new TelemetryStartup("recap", null, AuthEndpoints.DefaultSignupUrl, false, false, Unsaved: UnsavedConsent.Withhold),
            Config.Root).Telemetry.Enabled;

    static async Task<int> FlushesAfterFlushing(TelemetryProbe probe) {
        await probe.Telemetry.FlushAndClose();

        return probe.Sink.Flushes;
    }

    [Test]
    public async Task The_local_edition_picks_neither_answer_and_holds_continue_until_one_is_picked() {
        var probe  = LocalDesktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        var step   = new UsageDataStepViewModel(notice);

        await Assert.That(step.Applicable).IsTrue();
        await Assert.That(step.Asking).IsTrue();
        await Assert.That(step.PassThrough).IsFalse();
        await Assert.That(step.Lede).IsEqualTo(UsageDataStepViewModel.LocalLede);
        await Assert.That(step.Share).IsFalse();
        await Assert.That(step.DontShare).IsFalse();
        await Assert.That(step.CanContinue).IsFalse();
        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsFalse();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsNull();
        await Assert.That(notice.Visible).IsFalse();

        step.DontShare = true;

        await Assert.That(step.CanContinue).IsTrue();
    }

    /// The launcher's Dismiss is not an answer: dismissing the notice sends nothing in the local edition.
    [Test]
    public async Task Nothing_is_sent_before_the_local_edition_has_an_answer() {
        var probe = LocalDesktop();
        probe.Funnel.StepShown("privacy");

        new UsageDataNotice(probe.Telemetry, Config.Root).Acknowledge();

        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(0);
        await Assert.That(probe.Names).DoesNotContain("cli_first_run");
        await Assert.That(CommandSends()).IsFalse();
    }

    [Test]
    public async Task Sharing_in_the_local_edition_is_saved_and_sends_what_waited() {
        var probe = LocalDesktop();
        probe.Funnel.StepShown("privacy");
        var step  = new UsageDataStepViewModel(new UsageDataNotice(probe.Telemetry, Config.Root)) { Share = true };

        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();

        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsTrue();
        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(await FlushesAfterFlushing(probe)).IsGreaterThan(0);
        await Assert.That(probe.Names).Contains("cli_setup_step_shown");
        await Assert.That(CommandSends()).IsTrue();
    }

    [Test]
    public async Task Declining_in_the_local_edition_is_saved_and_drops_what_waited() {
        var probe = LocalDesktop();
        probe.Funnel.StepShown("privacy");
        var step  = new UsageDataStepViewModel(new UsageDataNotice(probe.Telemetry, Config.Root)) { DontShare = true };

        await Assert.That(await step.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsTrue();

        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(probe.Sink.Discarded).IsTrue();
        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(0);
        await Assert.That(CommandSends()).IsFalse();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task An_answer_from_an_earlier_run_is_not_asked_again(bool share) {
        var first = new UsageDataStepViewModel(new UsageDataNotice(LocalDesktop().Telemetry, Config.Root));
        if (share) first.Share = true;
        else first.DontShare = true;
        await first.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        var probe = LocalDesktop();
        var again = new UsageDataStepViewModel(new UsageDataNotice(probe.Telemetry, Config.Root));

        await Assert.That(again.Applicable).IsFalse();
        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(probe.Telemetry.Enabled).IsEqualTo(share);
    }

    /// Choosing the cloud edition after the page was built makes it the disclosure, with Share picked as before.
    [Test]
    public async Task The_cloud_edition_turns_the_page_back_into_the_notice() {
        var probe  = LocalDesktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        var step   = new UsageDataStepViewModel(notice);

        probe.Telemetry.SettleEdition(local: false);
        notice.Restate();
        await step.OnEnterAsync(CancellationToken.None);

        await Assert.That(step.Asking).IsFalse();
        await Assert.That(notice.Visible).IsTrue();
        await Assert.That(step.Share).IsTrue();
        await Assert.That(step.CanContinue).IsTrue();
        await Assert.That(step.Lede).IsEqualTo(UsageDataNotice.Text);
    }
}
