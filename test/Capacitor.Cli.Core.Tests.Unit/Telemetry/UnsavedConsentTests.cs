using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Core.Tests.Unit.Telemetry;

/// <summary>What a run does with usage data nobody has chosen to share: the local edition sends nothing until someone
/// opts in, a run still choosing its edition holds what it captures, and the cloud edition keeps its notice.</summary>
public class UnsavedConsentTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe Start(UnsavedConsent unsaved, string command = "setup") =>
        TelemetryProbe.Start(new TelemetryStartup(command, null, AuthEndpoints.DefaultSignupUrl, false, false, Unsaved: unsaved), Config.Root);

    /// <summary>Flushes, then reports how many reached the sink: a held run asks the sink for nothing.</summary>
    static async Task<int> FlushesAfterFlushing(TelemetryProbe probe) {
        await probe.Telemetry.FlushAndClose();

        return probe.Sink.Flushes;
    }

    [Test]
    public async Task A_run_still_choosing_its_edition_captures_but_sends_nothing() {
        var probe = Start(UnsavedConsent.Pending);

        probe.Funnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);
        probe.Funnel.EditionOffered();

        await Assert.That(probe.Telemetry.AwaitingChoice).IsTrue();
        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(0);
        await Assert.That(probe.Names).Contains("cli_setup_edition_offered");
        await Assert.That(probe.Names).DoesNotContain("cli_first_run");
        await Assert.That(probe.Telemetry.NoticeDue(Config.Root)).IsFalse();
        await Assert.That(TelemetryState.Read(Config.Root)).IsEqualTo(default(TelemetryStateFile));
    }

    /// <summary>A run that cannot ask, such as <c>kcap setup --local --yes --no-prompt</c>, never settles: what it
    /// captured stays unsent and no choice is saved for the user.</summary>
    [Test]
    public async Task A_local_run_that_never_asks_sends_nothing_and_saves_no_choice() {
        var probe = Start(UnsavedConsent.Pending);

        probe.Telemetry.SettleEdition(local: true);
        probe.Funnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: true);
        probe.Telemetry.RecordCommand("setup", ["setup", "--local", "--no-prompt"], 0, 10);

        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(0);
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsNull();
    }

    [Test]
    public async Task Choosing_the_cloud_edition_sends_what_waited_under_the_notice() {
        var probe = Start(UnsavedConsent.Pending);
        probe.Funnel.EditionOffered();

        probe.Telemetry.SettleEdition(local: false);

        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(1);
        await Assert.That(probe.Names).Contains("cli_setup_edition_offered");
        await Assert.That(probe.Names).Contains("cli_first_run");
    }

    [Test]
    public async Task The_local_edition_waits_until_an_opt_in_is_saved_then_sends_what_waited() {
        var probe = Start(UnsavedConsent.Pending);
        probe.Telemetry.SettleEdition(local: false);
        probe.Telemetry.SettleEdition(local: true);
        probe.Funnel.LocalInstallStarted();

        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(0);

        await Assert.That(TelemetryState.SetEnabled(true, Config.Root)).IsTrue();

        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(1);
        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(probe.Names).Contains("cli_setup_local_install_started");
    }

    [Test]
    public async Task An_opt_out_saved_while_waiting_drops_what_waited() {
        var probe = Start(UnsavedConsent.Pending);
        probe.Funnel.EditionOffered();

        TelemetryState.SetEnabled(false, Config.Root);

        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(0);
        await Assert.That(probe.Sink.Discarded).IsTrue();
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
    }

    [Test]
    public async Task A_choice_saved_before_the_run_is_honoured_and_never_waited_on() {
        TelemetryState.SetEnabled(true, Config.Root);
        var shared = Start(UnsavedConsent.Pending);
        shared.Telemetry.SettleEdition(local: true);

        await Assert.That(shared.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(await FlushesAfterFlushing(shared)).IsEqualTo(1);
        await Assert.That(Start(UnsavedConsent.Withhold, "recap").Telemetry.Enabled).IsTrue();

        TelemetryState.SetEnabled(false, Config.Root);

        await Assert.That(Start(UnsavedConsent.Pending).Telemetry.Enabled).IsFalse();
        await Assert.That(Start(UnsavedConsent.Share, "recap").Telemetry.Enabled).IsFalse();
    }

    [Test]
    public async Task The_local_edition_with_no_choice_is_off_and_mints_no_device_id() {
        var probe = Start(UnsavedConsent.Withhold, "recap");

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(probe.Telemetry.Unsaved).IsEqualTo(UnsavedConsent.Withhold);
        await Assert.That(TelemetryDeviceId.ReadPersisted(Config.Root)).IsNull();
        await Assert.That(TelemetryState.Read(Config.Root).NoticeShown).IsFalse();
    }

    [Test]
    public async Task An_opt_in_made_in_the_run_sends_what_waited_and_shows_the_notice() {
        var probe = Start(UnsavedConsent.Pending);
        probe.Funnel.EditionOffered();
        TelemetryState.SetEnabled(true, Config.Root);

        probe.Telemetry.OptedIn(Config.Root);

        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await Assert.That(probe.Names).Contains("cli_first_run");
        await Assert.That(await FlushesAfterFlushing(probe)).IsEqualTo(1);
    }
}
