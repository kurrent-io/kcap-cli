using Avalonia.Media;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.FakeDaemonClientService;

namespace Capacitor.App.Tests.Unit;

/// A daemon that reports its own sign-in as why its hub will not connect reads as signed out in the
/// footer and tray, rather than reconnecting for ever, and the app offers Sign in only when that
/// repairs it.
public class DaemonSignInTests {
    static readonly AttachStatus Attached = new(AttachState.Connected, null, null);

    [Test]
    [Arguments("reconnecting", "signed_out", "work", "work", true, true)]
    [Arguments("reconnecting", "rejected", "work", "WORK", true, false)]  // profile names are case-sensitive keys
    [Arguments("reconnecting", "signed_out", "old", "work", true, false)]
    [Arguments("reconnecting", "signed_out", null, "work", true, true)]
    [Arguments("reconnecting", "signed_out", "work", null, true, false)]
    [Arguments("reconnecting", null, "work", "work", false, false)]
    [Arguments("connected", "rejected", "work", "work", false, false)]
    public async Task Signed_out_only_while_the_hub_is_down_and_repairable_only_on_the_apps_profile(
            string connection, string? credential, string? daemonProfile, string? appProfile, bool signedOut, bool ours) {
        var daemon = Snap(connection: connection, credential: credential, profile: daemonProfile).Daemon;

        await Assert.That(MainWindowViewModel.DaemonSignInFor(Attached, daemon, appProfile)).IsEqualTo((signedOut, ours));
    }

    [Test]
    public async Task An_unreachable_daemon_says_nothing_about_its_sign_in() =>
        await Assert.That(MainWindowViewModel.DaemonSignInFor(
                new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null),
                Snap(connection: "reconnecting", credential: "signed_out").Daemon, "work"))
            .IsEqualTo((false, false));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_footer_says_signed_out_and_offers_sign_in_for_the_apps_own_profile() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var vm = new MainWindowViewModel(
                service, CancellationToken.None, TestActivity.New(), TimeProvider.System,
                tenantName: "work", requestSignIn: () => { });
            using var activation = vm.Activator.Activate();

            service.SnapshotsSubject.OnNext(Snap(connection: "reconnecting", credential: "signed_out", profile: "work"));
            service.StatusSubject.OnNext(Attached);

            await Assert.That(vm.ConnectionDisplay).IsEqualTo(MainWindowViewModel.SignedOutDisplay);
            // A signed-out daemon needs the user, as the launcher's card shows it too.
            await Assert.That(((ISolidColorBrush)vm.StatusBrush).Color).IsEqualTo(Color.Parse(StatusColors.InProgress));
            await Assert.That(vm.SignInVisible).IsTrue();

            service.SnapshotsSubject.OnNext(Snap(connection: "reconnecting", credential: "signed_out", profile: "old"));
            await Assert.That(vm.ConnectionDisplay).IsEqualTo(MainWindowViewModel.SignedOutDisplay);
            await Assert.That(vm.SignInVisible).IsFalse().Because("signing the app in leaves another profile's daemon signed out");

            service.SnapshotsSubject.OnNext(Snap(connection: "connected", profile: "old"));
            await Assert.That(vm.ConnectionDisplay).IsEqualTo("Connected");
        });
    }

    /// A local server takes no sign-in, so neither the app's nor the daemon's sign-in notice may ask for one.
    [Test]
    [Arguments(HomeViewModel.SignInExpiredNotice)]
    [Arguments(HomeViewModel.DaemonSignedOutNotice)]
    public async Task A_local_server_reports_a_refusal_rather_than_asking_for_a_sign_in(string notice) =>
        await Assert.That(HomeViewModel.NoticeForLocalServer(notice, localServer: true, localServerDown: false, remoteMachine: null))
            .IsEqualTo(HomeViewModel.LocalServerRefusedNotice);

    [Test]
    [Arguments("work", "work", "The local daemon runs on this profile")]
    [Arguments("work", null, "If the local daemon runs on this profile")]
    public async Task Removing_the_daemons_profile_or_an_unknown_one_warns_of_the_sign_out(string name, string? daemonProfile, string warning) =>
        await Assert.That(ProfilesSettingsViewModel.RemovePrompt(name, daemonProfile)).Contains(warning);

    [Test]
    [Arguments("other")]
    [Arguments("WORK")]
    public async Task Removing_another_profile_says_nothing_about_the_daemon(string daemonProfile) =>
        await Assert.That(ProfilesSettingsViewModel.RemovePrompt("work", daemonProfile)).DoesNotContain("daemon");

    [Test]
    [Arguments(false, false, false, HomeViewModel.DaemonSignedOutNotice)]
    [Arguments(true, false, false, HomeViewModel.SignInExpiredNotice)]
    [Arguments(false, true, false, HomeViewModel.FinishingSignInNotice)]
    [Arguments(false, false, true, null)]
    public async Task The_launcher_says_the_daemon_is_signed_out_for_a_local_launch(bool appExpired, bool awaiting, bool remote, string? notice) =>
        await Assert.That(HomeViewModel.NoticeFor(Attached, HomeViewModel.DaemonSignedOutWord, appExpired, awaitingServer: awaiting,
                remoteSelected: remote, remote ? LaunchAvailability.Ready : LaunchAvailability.ServerDisconnected))
            .IsEqualTo(notice);

    [Test]
    [Arguments(false, "it's work", "run KCAP_PROFILE='it'\\''s work' kcap login")]
    [Arguments(true, "work", "in PowerShell, run powershell -NoProfile -Command '$env:KCAP_PROFILE=''work''; kcap login'")]
    [Arguments(true, "it's", "in PowerShell, run powershell -NoProfile -Command '$env:KCAP_PROFILE=''it''''s''; kcap login'")]
    [Arguments(true, "it\u2019s", "in PowerShell, run powershell -NoProfile -Command '$env:KCAP_PROFILE=''it\u2019\u2019\u2019\u2019s''; kcap login'")]
    public async Task The_login_hint_quotes_the_profile_and_leaves_the_shell_untouched(bool windows, string profile, string hint) =>
        await Assert.That(TrayViewModel.LoginHint(profile, windows)).IsEqualTo(hint);
}
