using Capacitor.Cli.Core;
using Capacitor.App.Platform.Mac;

namespace Capacitor.App.Tests.Unit;

public class ApplicationsMoverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class FakeDitto(Action<string> populate) : IProcessRunner {
        public int Calls;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            populate(args[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    sealed class CancellingDitto : IProcessRunner {
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            CompleteBundle(args[1]);
            throw new OperationCanceledException(ct);
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    static void CompleteBundle(string root) {
        CompleteBundle(root, "1.0.0");
    }

    static void CompleteBundle(string root, string version, string id = "io.kurrent.kcap") {
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"),
            $"<plist><dict><key>CFBundleIdentifier</key><string>{id}</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "kcap-desktop"), "exe");
    }

    static bool MoveSwap(string from, string to) {
        var aside = to + ".aside";
        Directory.Move(to, aside);
        Directory.Move(from, to);
        Directory.Move(aside, from);
        return true;
    }

    static bool MovePromote(string from, string to) {
        if (Directory.Exists(to)) return false;
        Directory.Move(from, to);
        return true;
    }

    [Test]
    public async Task Complete_copy_is_promoted_and_nothing_is_left_staged() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/kcap.app");
        CompleteBundle(source);
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(outcome.InstalledPath).IsEqualTo(Path.Combine(apps, "kcap.app"));
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task An_unverified_user_bundle_is_not_selected_for_update() {
        var systemApps = Tmp.CreateDir("SystemApplications");
        var userApps = Tmp.CreateDir("UserApplications");
        CompleteBundle(Path.Combine(userApps, "kcap.app"), "1.0.0", "another.application");
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");

        var selected = ApplicationsMover.ApplicationDirectories(source, userApps, systemApps).Single();
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, selected);
        await Assert.That(selected).IsEqualTo(systemApps);
        await Assert.That(mover.Inspect(source).Action).IsEqualTo(ApplicationsInstallAction.Install);
        await Assert.That(mover.Inspect(source).Target).IsEqualTo(Path.Combine(systemApps, "kcap.app"));
    }

    [Test]
    public async Task An_installed_kcap_starts_without_an_install_prompt() {
        var systemApps = Tmp.CreateDir("SystemApplications");
        var userApps = Tmp.CreateDir("UserApplications");
        var source = Path.Combine(userApps, "kcap.app");
        CompleteBundle(source, "1.1.0");
        var selected = ApplicationsMover.ApplicationDirectories(source, userApps, systemApps).Single();
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, selected);

        await Assert.That(selected).IsEqualTo(userApps);
        await Assert.That(App.ShouldPromptToInstall(InstallLocationKind.UserApplications, source, mover.Inspect(source))).IsFalse();
    }

    [Test]
    [Arguments(false, "1.1.0")]
    [Arguments(false, "2.0.0")]
    [Arguments(true, "1.1.0")]
    [Arguments(true, "2.0.0")]
    public async Task Opening_a_canonical_install_keeps_its_folder_when_both_copies_are_canonical(bool perUser, string alternateVersion) {
        var system = Tmp.CreateDir("SystemApplications");
        var user = Tmp.CreateDir("UserApplications");
        var home = perUser ? user : system;
        var other = perUser ? system : user;
        var source = Path.Combine(home, "kcap.app");
        CompleteBundle(source, "1.1.0");
        CompleteBundle(Path.Combine(other, "kcap.app"), alternateVersion);

        var selected = ApplicationsMover.ApplicationDirectories(source, user, system).Single();
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, selected);

        await Assert.That(selected).IsEqualTo(home);
        await Assert.That(App.ShouldPromptToInstall(
            perUser ? InstallLocationKind.UserApplications : InstallLocationKind.Applications,
            source, mover.Inspect(source))).IsFalse();
        await Assert.That(ApplicationsMover.ApplicationDirectories(Tmp.PathTo("Downloads/kcap.app"), user, system).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Opening_a_canonical_install_does_not_bypass_an_unrelated_destination() {
        var system = Tmp.CreateDir("SystemApplications");
        var user = Tmp.CreateDir("UserApplications");
        var source = Path.Combine(user, "kcap.app");
        CompleteBundle(source, "1.1.0");
        CompleteBundle(Path.Combine(system, "kcap.app"), "1.0.0", "another.application");

        await Assert.That(ApplicationsMover.ApplicationDirectories(source, user, system).Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_dragged_older_copy_redirects_to_a_newer_install_in_the_other_Applications_folder() {
        var plan = new ApplicationsInstallPlan(ApplicationsInstallAction.OpenInstalled, "/Applications/kcap.app", "2.0.0", "2.0.0");
        await Assert.That(App.ShouldPromptToInstall(InstallLocationKind.UserApplications, "/Users/example/Applications/kcap.app", plan)).IsTrue();
    }

    [Test]
    public async Task Both_installed_directories_and_a_conflicting_system_app_require_a_destination_choice() {
        var system = Tmp.CreateDir("SystemApplications");
        var user = Tmp.CreateDir("UserApplications");
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        CompleteBundle(Path.Combine(user, "kcap.app"));
        CompleteBundle(Path.Combine(system, "kcap.app"));
        await Assert.That(ApplicationsMover.ApplicationDirectories(source, user, system).Count).IsEqualTo(2);
        CompleteBundle(Path.Combine(system, "kcap.app"), "1.0.0", "another.application");
        await Assert.That(ApplicationsMover.ApplicationDirectories(source, user, system).Count).IsEqualTo(2);
        var userMover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, user);
        await Assert.That(userMover.Inspect(source).Action).IsEqualTo(ApplicationsInstallAction.Update);
        var systemMover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, system);
        await Assert.That(systemMover.Inspect(source).Action).IsEqualTo(ApplicationsInstallAction.Blocked);
    }

    [Test]
    public async Task An_older_download_opens_the_newer_install_without_downgrading() {
        var apps = Tmp.CreateDir("Applications");
        var installed = Path.Combine(apps, "kcap.app");
        CompleteBundle(installed, "2.0.0");
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.0.0");
        var ditto = new FakeDitto(root => CompleteBundle(root, "1.0.0"));
        var mover = new ApplicationsMover(ditto, MovePromote, apps);
        await Assert.That(mover.Inspect(source).Action).IsEqualTo(ApplicationsInstallAction.OpenInstalled);
        var outcome = await mover.MoveAsync(source, CancellationToken.None);
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(ditto.Calls).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist"))).Contains("2.0.0");
    }

    [Test]
    public async Task A_failed_launch_restores_the_installed_bundle_and_does_not_remove_the_download() {
        var apps = Tmp.CreateDir("Applications");
        var installed = Tmp.PathTo("Applications/kcap.app");
        CompleteBundle(installed, "1.0.0");
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var mover = new ApplicationsMover(new FakeDitto(root => CompleteBundle(root, "1.1.0")), MovePromote, apps,
            MoveSwap, activate: (_, _) => Task.FromResult(false));
        var outcome = await mover.MoveAsync(source, CancellationToken.None);
        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist"))).Contains("1.0.0");
        await Assert.That(Directory.Exists(source)).IsTrue();
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Quit_and_update_waits_for_the_installed_app_to_exit_before_copying() {
        var apps = Tmp.CreateDir("Applications");
        CompleteBundle(Tmp.PathTo("Applications/kcap.app"));
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var running = true;
        var ditto = new FakeDitto(root => { if (running) throw new InvalidOperationException("Still running"); CompleteBundle(root, "1.1.0"); });
        var mover = new ApplicationsMover(ditto, MovePromote, apps, (_, _) => true, _ => running,
            (_, _) => { running = false; return Task.FromResult(true); });
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsTrue();
        await Assert.That(ditto.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Choosing_a_new_user_Applications_folder_creates_it_before_copying() {
        var apps = Tmp.PathTo("home/Applications");
        var source = Tmp.CreateDir("Downloads/kcap.app");
        CompleteBundle(source);
        var mover = new ApplicationsMover(new FakeDitto(root => {
            if (!Directory.Exists(apps)) throw new IOException("Destination is missing");
            CompleteBundle(root);
        }), MovePromote, apps);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(apps, "kcap.app"))).IsTrue();
    }

    [Test]
    public async Task Incomplete_copy_is_removed_and_reported() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/kcap.app");
        CompleteBundle(source);
        var mover = new ApplicationsMover(new FakeDitto(root => Directory.CreateDirectory(Path.Combine(root, "Contents"))), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("incomplete");
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    [Test]
    public async Task Existing_destination_refuses_before_copying() {
        var apps = Tmp.CreateDir("Applications");
        Tmp.CreateDir("Applications/kcap.app");
        var source = Tmp.CreateDir("Downloads/kcap.app");
        CompleteBundle(source);
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("already exists");
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Destination_appearing_mid_move_fails_promotion_and_cleans_staging() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/kcap.app");
        CompleteBundle(source);
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), (_, _) => false, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("appeared");
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    [Test]
    public async Task Cancellation_mid_copy_removes_staging_and_propagates() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/kcap.app");
        CompleteBundle(source);
        var mover = new ApplicationsMover(new CancellingDitto(), MovePromote, apps);

        await Assert.That(async () => await mover.MoveAsync(source, CancellationToken.None))
            .Throws<OperationCanceledException>();
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    [Test]
    public async Task PromoteExclusive_refuses_an_empty_existing_destination() {
        if (!OperatingSystem.IsMacOS()) return;
        var from = Tmp.CreateDir("staging");
        var to = Tmp.CreateDir("target");

        await Assert.That(ApplicationsMover.PromoteExclusive(from, to)).IsFalse();
        await Assert.That(Directory.Exists(from)).IsTrue();
    }

    [Test]
    public async Task PromoteExclusive_moves_when_the_destination_is_absent() {
        if (!OperatingSystem.IsMacOS()) return;
        var from = Tmp.CreateDir("staging");
        var to = Tmp.PathTo("target");

        await Assert.That(ApplicationsMover.PromoteExclusive(from, to)).IsTrue();
        await Assert.That(Directory.Exists(to)).IsTrue();
    }

    [Test]
    [Arguments("1.1.0", ApplicationsInstallAction.Update)]
    [Arguments("1.0.0", ApplicationsInstallAction.OpenInstalled)]
    [Arguments("0.9.0", ApplicationsInstallAction.OpenInstalled)]
    [Arguments("1.1.0-beta.2", ApplicationsInstallAction.Update)]
    [Arguments("1.0.0-beta.2", ApplicationsInstallAction.OpenInstalled)]
    [Arguments("unknown", ApplicationsInstallAction.OpenInstalled)]
    public async Task Existing_verified_copy_is_updated_only_when_source_is_newer(string version, ApplicationsInstallAction expected) {
        var apps = Tmp.CreateDir("Applications");
        CompleteBundle(Tmp.PathTo("Applications/kcap.app"));
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, version);
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps);
        await Assert.That(mover.Inspect(source).Action).IsEqualTo(expected);
        if (expected == ApplicationsInstallAction.OpenInstalled) {
            await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsTrue();
            await Assert.That(ditto.Calls).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Failed_swap_preserves_installed_copy_and_cleans_new_copy() {
        var apps = Tmp.CreateDir("Applications");
        var installed = Tmp.PathTo("Applications/kcap.app");
        CompleteBundle(installed);
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var mover = new ApplicationsMover(new FakeDitto(root => CompleteBundle(root, "1.1.0")), MovePromote, apps, (_, _) => false);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist"))).Contains("1.0.0");
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Newer_bundle_is_atomically_swapped_and_old_copy_is_removed() {
        if (!OperatingSystem.IsMacOS()) return;
        var apps = Tmp.CreateDir("Applications");
        var installed = Tmp.PathTo("Applications/kcap.app");
        CompleteBundle(installed);
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var mover = new ApplicationsMover(new FakeDitto(root => CompleteBundle(root, "1.1.0")),
            MovePromote, apps, ApplicationsMover.SwapAtomic);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(apps, "kcap.app", "Contents", "Info.plist"))).Contains("1.1.0");
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Running_installed_copy_is_not_replaced() {
        var apps = Tmp.CreateDir("Applications");
        CompleteBundle(Tmp.PathTo("Applications/kcap.app"));
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps, (_, _) => true, _ => true);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsFalse();
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_destination_changed_during_copy_is_not_overwritten() {
        var apps = Tmp.CreateDir("Applications");
        var installed = Tmp.PathTo("Applications/kcap.app");
        CompleteBundle(installed);
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var swapped = false;
        var mover = new ApplicationsMover(new FakeDitto(root => {
            CompleteBundle(root, "1.1.0");
            CompleteBundle(installed, "1.2.0");
        }), MovePromote, apps, (_, _) => swapped = true);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsFalse();
        await Assert.That(swapped).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist"))).Contains("1.2.0");
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task An_unrelated_bundle_is_never_replaced() {
        var apps = Tmp.CreateDir("Applications");
        CompleteBundle(Tmp.PathTo("Applications/kcap.app"), "1.0.0", "another.application");
        var source = Tmp.PathTo("Downloads/kcap.app");
        CompleteBundle(source, "1.1.0");
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps, (_, _) => true);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsFalse();
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }
}
