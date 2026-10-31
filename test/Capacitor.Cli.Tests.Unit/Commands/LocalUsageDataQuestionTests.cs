using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Commands;

internal sealed class LocalUsageDataQuestionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe SettingUpLocally() =>
        TelemetryProbe.Start(new TelemetryStartup("setup", null, AuthEndpoints.DefaultSignupUrl, false, false, Unsaved: UnsavedConsent.Pending),
            Config.Root);

    static TelemetryDecision NoChoice => new(false, TelemetrySettings.LocalReason);

    [Test]
    public async Task Only_a_machine_with_no_choice_is_asked() {
        await Assert.That(LocalUsageDataQuestion.Due(default(TelemetryStateFile), NoChoice)).IsTrue();
        await Assert.That(LocalUsageDataQuestion.Due(new TelemetryStateFile(true, true), NoChoice)).IsFalse();
        await Assert.That(LocalUsageDataQuestion.Due(new TelemetryStateFile(false, false), NoChoice)).IsFalse();
    }

    /// <summary>An unreadable choice may be an opt-out, and an environment variable outranks any answer.</summary>
    [Test]
    public async Task An_unreadable_choice_or_an_environment_variable_is_not_asked() {
        await Assert.That(LocalUsageDataQuestion.Due(null, NoChoice)).IsFalse();
        await Assert.That(LocalUsageDataQuestion.Due(default(TelemetryStateFile), new(true, "KCAP_TELEMETRY"))).IsFalse();
        await Assert.That(LocalUsageDataQuestion.Due(default(TelemetryStateFile), new(false, "DO_NOT_TRACK"))).IsFalse();
    }

    [Test]
    public async Task Yes_is_saved_and_sends_what_the_run_held() {
        var probe = SettingUpLocally();
        probe.Funnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);

        await Assert.That(LocalUsageDataQuestion.Record(true, probe.Telemetry, Config.Root)).IsEqualTo(LocalUsageDataQuestion.Shared);

        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsTrue();
        await Assert.That(probe.Telemetry.AwaitingChoice).IsFalse();
        await probe.Telemetry.FlushAndClose();
        await Assert.That(probe.Sink.Flushes).IsEqualTo(1);
        await Assert.That(probe.Names).Contains("cli_setup_started");
    }

    [Test]
    public async Task No_is_saved_and_drops_what_the_run_held() {
        var probe = SettingUpLocally();
        probe.Funnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);

        await Assert.That(LocalUsageDataQuestion.Record(false, probe.Telemetry, Config.Root)).IsEqualTo(LocalUsageDataQuestion.NotShared);

        await Assert.That(TelemetryState.PersistedEnabled(Config.Root)).IsFalse();
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await probe.Telemetry.FlushAndClose();
        await Assert.That(probe.Sink.Flushes).IsEqualTo(0);
        await Assert.That(probe.Sink.Discarded).IsTrue();
    }

    /// <summary>A yes that cannot be saved would not hold for the next kcap command, so this run sends nothing either.</summary>
    [Test]
    public async Task An_unsaved_yes_sends_nothing() {
        var probe = SettingUpLocally();
        Config.CreateDir("telemetry.json");

        await Assert.That(LocalUsageDataQuestion.Record(true, probe.Telemetry, Config.Root)).IsEqualTo(LocalUsageDataQuestion.NotSaved);

        await probe.Telemetry.FlushAndClose();
        await Assert.That(probe.Sink.Flushes).IsEqualTo(0);
    }
}
