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

    /// A choice that cannot be read sends nothing, so the switch shows it off rather than on.
    [Test]
    public async Task An_unreadable_choice_shows_the_switch_off() {
        var probe = Desktop();
        Unwritable();

        using var privacy = new PrivacySettingsViewModel(probe.Telemetry, Config.Root, new UsageDataNotice(probe.Telemetry, Config.Root));

        await Assert.That(privacy.SendUsageData).IsFalse();
    }

    [Test]
    public async Task Turning_it_off_from_the_notice_moves_an_open_settings_switch() {
        var probe   = Desktop();
        var notice  = new UsageDataNotice(probe.Telemetry, Config.Root);
        using var privacy = new PrivacySettingsViewModel(probe.Telemetry, Config.Root, notice);
        var changed = new List<string?>();
        privacy.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await notice.TurnOffCommand.Execute().ToTask();

        await Assert.That(privacy.SendUsageData).IsFalse();
        await Assert.That(changed).Contains(nameof(PrivacySettingsViewModel.SendUsageData));
    }

    /// <summary>A choice that cannot be written to disk still stops this process, and both surfaces say the
    /// next launch will send again rather than hiding as though it had been saved.</summary>
    [Test]
    public async Task An_opt_out_that_cannot_be_saved_stops_this_process_and_says_so() {
        var probe   = Desktop();
        var notice  = new UsageDataNotice(probe.Telemetry, Config.Root);
        using var privacy = new PrivacySettingsViewModel(probe.Telemetry, Config.Root, notice);
        Unwritable();

        privacy.SendUsageData = false;

        await Assert.That(probe.Telemetry.Enabled).IsFalse();
        await Assert.That(privacy.Hint).IsEqualTo(UsageDataNotice.NotSaved);
        await Assert.That(notice.Visible).IsTrue();
        await Assert.That(notice.Message).IsEqualTo(UsageDataNotice.NotSaved);
    }

    [Test]
    public async Task Turning_it_on_when_that_cannot_be_saved_leaves_the_switch_off() {
        var probe   = Desktop();
        var notice  = new UsageDataNotice(probe.Telemetry, Config.Root);
        using var privacy = new PrivacySettingsViewModel(probe.Telemetry, Config.Root, notice);
        privacy.SendUsageData = false;
        Unwritable();

        privacy.SendUsageData = true;

        await Assert.That(privacy.SendUsageData).IsFalse();
        await Assert.That(privacy.Hint).IsEqualTo(PrivacySettingsViewModel.CouldNotSave);
    }

    void Unwritable() {
        var path = Config.Root.Path("telemetry.json");
        if (File.Exists(path)) File.Delete(path);
        Directory.CreateDirectory(path);
    }
}
