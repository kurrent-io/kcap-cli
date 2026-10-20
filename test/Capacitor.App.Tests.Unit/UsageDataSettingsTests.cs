using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using System.Reactive.Threading.Tasks;

namespace Capacitor.App.Tests.Unit;

/// <summary>The app's usage-data disclosure shows until it is acknowledged, and turning usage data off,
/// from the notice or from Settings, stops this process sending as well as later ones.</summary>
public class UsageDataSettingsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    TelemetryProbe Desktop() {
        var probe = TelemetryProbe.Start(TelemetryStartup.ForDesktopApp(null, AuthEndpoints.DefaultSignupUrl), Config.Root);
        TelemetryTestGuards.AssertEnabled("app", Config.Root, probe.Telemetry);

        return probe;
    }

    [Test]
    public async Task Acknowledging_the_notice_hides_it_for_good() {
        var probe  = Desktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);
        await Assert.That(notice.Visible).IsTrue();

        await notice.AcknowledgeCommand.Execute().ToTask();

        await Assert.That(notice.Visible).IsFalse();
        await Assert.That(new UsageDataNotice(probe.Telemetry, Config.Root).Visible).IsFalse();
    }

    [Test]
    public async Task Turning_it_off_from_the_notice_stops_this_process_and_persists() {
        var probe  = Desktop();
        var notice = new UsageDataNotice(probe.Telemetry, Config.Root);

        await notice.TurnOffCommand.Execute().ToTask();

        await Assert.That(notice.Visible).IsFalse();
        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root) == false).IsTrue();
    }

    [Test]
    public async Task The_settings_switch_reads_and_writes_the_shared_flag() {
        var probe   = Desktop();
        var notice  = new UsageDataNotice(probe.Telemetry, Config.Root);
        var privacy = new PrivacySettingsViewModel(probe.Telemetry, Config.Root, notice);
        await Assert.That(privacy.SendUsageData).IsTrue();

        privacy.SendUsageData = false;

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(notice.Visible).IsFalse();
        await Assert.That(TelemetryState.PersistedEnabled(Config.Root) == false).IsTrue();

        privacy.SendUsageData = true;

        await Assert.That(TelemetryState.PersistedEnabled(Config.Root) == true).IsTrue();
        await Assert.That(privacy.Hint).IsEqualTo(PrivacySettingsViewModel.TakesEffectOnRelaunch);
    }
}
