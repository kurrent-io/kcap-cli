using Capacitor.Cli.Core;
using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class ApplicationsMoverPredecessorTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Current = "io.kurrent.kcap";
    const string Legacy = "io.kurrent.capacitor";

    static void Bundle(string root, string version, string id = Current, string executable = "kcap-desktop") {
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"),
            $"<plist><dict><key>CFBundleIdentifier</key><string>{id}</string><key>CFBundleExecutable</key><string>{executable}</string>"
            + $"<key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", executable), "exe");
    }

    static void LegacyBundle(string root, string version = "1.3.1") => Bundle(root, version, Legacy, "Kurrent Capacitor");

    sealed class Ditto(string version) : IProcessRunner {
        public int Calls;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            Bundle(args[1], version);
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    sealed class Retirement : IBundleRetirement {
        public readonly List<string> Scheduled = [];
        public readonly List<string> Cancelled = [];
        public void Schedule(KcapBundle bundle) => Scheduled.Add(bundle.Root);
        public void Cancel(string root) => Cancelled.Add(root);
    }

    sealed class RecordingReferences : IApplicationReferences {
        public IReadOnlyList<string> OldRoots = [];
        public bool RolledBack;
        public Task PrepareAsync(IReadOnlyList<string> oldRoots, string target, CancellationToken ct) {
            OldRoots = oldRoots;
            return Task.CompletedTask;
        }
        public Task ApplyAsync(CancellationToken ct) => Task.CompletedTask;
        public Task RollbackAsync(Action restoreBundles) {
            RolledBack = true;
            restoreBundles();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static bool Promote(string from, string to) {
        if (Directory.Exists(to)) return false;
        Directory.Move(from, to);
        return true;
    }

    static bool Swap(string from, string to) {
        var aside = to + ".aside";
        Directory.Move(to, aside);
        Directory.Move(from, to);
        Directory.Move(aside, from);
        return true;
    }

    /// The reported failure: the running copy, updated in place at its pre-rebrand path, renamed its own bundle
    /// before opening the installed one and then could not load an assembly from it.
    [Test]
    public async Task The_running_bundle_stays_in_place_and_is_handed_to_the_installed_copy() {
        var apps = Tmp.CreateDir("Applications");
        var source = Path.Combine(apps, KcapBundle.LegacyBundleName);
        Bundle(source, "1.4.1");
        var retirement = new Retirement();
        var sourcePresentAtActivation = false;
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps, Swap, retirement: retirement, systemApplicationsDir: apps,
            activate: (_, _) => {
                sourcePresentAtActivation = Directory.Exists(source) && retirement.Scheduled.Count == 1;
                return Task.FromResult(true);
            });

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(sourcePresentAtActivation).IsTrue();
        await Assert.That(Directory.Exists(source)).IsTrue();
        await Assert.That(KcapBundle.Read(Path.Combine(apps, "kcap.app"))?.Version).IsEqualTo("1.4.1");
        await Assert.That(retirement.Scheduled.SequenceEqual([source])).IsTrue();
        await Assert.That(Directory.GetDirectories(apps).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(["Kurrent Capacitor.app", "kcap.app"])).IsTrue();
    }

    /// A pre-rebrand path keeps receiving in-place updates, so it can hold a newer release than the disk image run.
    [Test]
    public async Task A_pre_rebrand_bundle_newer_than_the_installer_is_never_removed() {
        var apps = Tmp.CreateDir("Applications");
        var legacy = Path.Combine(apps, KcapBundle.LegacyBundleName);
        Bundle(legacy, "1.5.0");
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps, Swap, activate: (_, _) => Task.FromResult(true),
            retirement: new Retirement(), systemApplicationsDir: apps);

        var plan = mover.Inspect(source);
        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(plan.Replaces).IsEmpty();
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(KcapBundle.Read(legacy)?.Version).IsEqualTo("1.5.0");
    }

    /// Opening the copy in ~/Applications while /Applications holds the one the daemon runs from: keeping the opened
    /// copy quits the other, moves the references to the kept copy and removes the other one.
    [Test]
    public async Task Keeping_the_opened_copy_replaces_the_installed_one_in_the_other_folder() {
        var system = Tmp.CreateDir("Applications");
        var user = Tmp.CreateDir("home/Applications");
        var source = Path.Combine(user, "kcap.app");
        var other = Path.Combine(system, "kcap.app");
        Bundle(source, "1.4.1");
        Bundle(other, "1.4.1");
        var running = new HashSet<string> { other };
        var quit = new List<string>();
        var references = new RecordingReferences();
        var opened = new List<string>();
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, user, Swap, isRunning: running.Contains,
            quitRunning: (root, _) => { quit.Add(root); running.Remove(root); return Task.FromResult(true); },
            migrateReferences: () => references, activate: (target, _) => { opened.Add(target); return Task.FromResult(true); },
            retirement: new Retirement(), userApplicationsDir: user, systemApplicationsDir: system);

        var plan = mover.Inspect(source);
        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(plan.Action).IsEqualTo(ApplicationsInstallAction.OpenInstalled);
        await Assert.That(plan.Replaces.SequenceEqual([other])).IsTrue();
        await Assert.That(plan.Title).IsEqualTo("kcap is installed twice");
        await Assert.That(plan.ButtonLabel).IsEqualTo("Keep this copy");
        await Assert.That(plan.Detail).StartsWith($"The copy at {Path.Combine(user, "kcap.app")} stays. The one at {other} is removed");
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(quit.SequenceEqual([other])).IsTrue();
        await Assert.That(references.OldRoots).Contains(other);
        await Assert.That(opened.SequenceEqual([source])).IsTrue();
        await Assert.That(Directory.Exists(other)).IsFalse();
        await Assert.That(KcapBundle.Read(source)?.Version).IsEqualTo("1.4.1");
    }

    [Test]
    public async Task A_failed_activation_withdraws_the_hand_off_and_restores_the_folder() {
        var apps = Tmp.CreateDir("Applications");
        var source = Path.Combine(apps, "kcap 2.app");
        Bundle(source, "1.4.1");
        var retirement = new Retirement();
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps, Swap, retirement: retirement, systemApplicationsDir: apps,
            activate: (_, _) => Task.FromResult(false));

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(retirement.Cancelled.SequenceEqual([source])).IsTrue();
        await Assert.That(Directory.GetDirectories(apps).Select(Path.GetFileName).SequenceEqual(["kcap 2.app"])).IsTrue();
    }

    [Test]
    public async Task A_running_pre_rebrand_app_is_quit_repointed_and_removed() {
        var apps = Tmp.CreateDir("Applications");
        var legacy = Path.Combine(apps, KcapBundle.LegacyBundleName);
        LegacyBundle(legacy);
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");
        var running = new HashSet<string> { legacy };
        var quit = new List<string>();
        var references = new RecordingReferences();
        var retirement = new Retirement();
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps, Swap, isRunning: running.Contains,
            quitRunning: (root, _) => { quit.Add(root); running.Remove(root); return Task.FromResult(true); },
            migrateReferences: () => references, activate: (_, _) => Task.FromResult(true), retirement: retirement,
            systemApplicationsDir: apps);

        var plan = mover.Inspect(source);
        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(plan.Title).IsEqualTo("Upgrade to kcap");
        await Assert.That(plan.ButtonLabel).IsEqualTo("Quit and update");
        await Assert.That(plan.Detail).StartsWith("kcap will replace Kurrent Capacitor and open.");
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(quit.SequenceEqual([legacy])).IsTrue();
        await Assert.That(references.OldRoots).Contains(legacy);
        await Assert.That(Directory.Exists(legacy)).IsFalse();
        await Assert.That(retirement.Scheduled).IsEmpty();
        await Assert.That(Directory.GetDirectories(apps).Select(Path.GetFileName).SequenceEqual(["kcap.app"])).IsTrue();
    }

    [Test]
    public async Task A_pre_rebrand_app_is_restored_when_activation_fails() {
        var apps = Tmp.CreateDir("Applications");
        var legacy = Path.Combine(apps, KcapBundle.LegacyBundleName);
        LegacyBundle(legacy);
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");
        var references = new RecordingReferences();
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps, Swap, migrateReferences: () => references,
            activate: (_, _) => Task.FromResult(false), systemApplicationsDir: apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(references.RolledBack).IsTrue();
        await Assert.That(KcapBundle.Read(legacy)?.Legacy).IsTrue();
        await Assert.That(Directory.GetDirectories(apps).Select(Path.GetFileName).SequenceEqual(["Kurrent Capacitor.app"])).IsTrue();
    }

    [Test]
    public async Task A_pre_rebrand_app_in_the_other_Applications_folder_is_retired_too() {
        var system = Tmp.CreateDir("Applications");
        var user = Tmp.CreateDir("home/Applications");
        var legacy = Path.Combine(user, KcapBundle.LegacyBundleName);
        LegacyBundle(legacy);
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, system, Swap, activate: (_, _) => Task.FromResult(true),
            userApplicationsDir: user, systemApplicationsDir: system);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(Directory.Exists(legacy)).IsFalse();
    }

    [Test]
    public async Task An_installed_beta_with_the_pre_rebrand_identity_is_updated_not_reported_as_a_conflict() {
        var system = Tmp.CreateDir("Applications");
        var user = Tmp.PathTo("home/Applications");
        var installed = Path.Combine(system, "kcap.app");
        Bundle(installed, "1.4.0-beta.2", Legacy);
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");

        var directories = ApplicationsMover.ApplicationDirectories(source, user, system);
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, system, Swap, activate: (_, _) => Task.FromResult(true),
            userApplicationsDir: user, systemApplicationsDir: system);
        var plan = mover.Inspect(source);
        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(directories.SequenceEqual([system])).IsTrue();
        await Assert.That(plan.Action).IsEqualTo(ApplicationsInstallAction.Update);
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(KcapBundle.Read(installed) is { Legacy: false, Version: "1.4.1" }).IsTrue();
    }

    [Test]
    public async Task An_up_to_date_install_still_takes_over_from_a_copy_at_the_pre_rebrand_path() {
        var apps = Tmp.CreateDir("Applications");
        Bundle(Path.Combine(apps, "kcap.app"), "1.4.1");
        var source = Path.Combine(apps, KcapBundle.LegacyBundleName);
        Bundle(source, "1.4.1");
        var ditto = new Ditto("1.4.1");
        var retirement = new Retirement();
        var activated = new List<string>();
        var references = new RecordingReferences();
        var mover = new ApplicationsMover(ditto, Promote, apps, Swap, migrateReferences: () => references, retirement: retirement,
            activate: (target, _) => { activated.Add(target); return Task.FromResult(true); }, systemApplicationsDir: apps);

        var plan = mover.Inspect(source);
        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(plan.Action).IsEqualTo(ApplicationsInstallAction.OpenInstalled);
        await Assert.That(plan.Title).IsEqualTo("Upgrade to kcap");
        await Assert.That(plan.ButtonLabel).IsEqualTo("Replace and open");
        await Assert.That(plan.Detail).StartsWith($"kcap at {Path.Combine(apps, "kcap.app")} will replace Kurrent Capacitor and open.");
        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(ditto.Calls).IsEqualTo(0);
        await Assert.That(references.OldRoots).Contains(source);
        await Assert.That(retirement.Scheduled.SequenceEqual([source])).IsTrue();
        await Assert.That(activated.SequenceEqual([Path.Combine(apps, "kcap.app")])).IsTrue();
    }

    [Test]
    public async Task A_bundle_under_the_pre_rebrand_name_that_is_not_kcap_is_never_touched() {
        var apps = Tmp.CreateDir("Applications");
        var other = Path.Combine(apps, KcapBundle.LegacyBundleName);
        Bundle(other, "9.9.9", "com.example.other", "Kurrent Capacitor");
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");
        var mover = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps, Swap, activate: (_, _) => Task.FromResult(true),
            systemApplicationsDir: apps);

        var plan = mover.Inspect(source);
        await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(plan.Replaces).IsEmpty();
        await Assert.That(Directory.Exists(other)).IsTrue();
    }

    [Test]
    public async Task Without_an_explicit_system_folder_nothing_outside_the_given_folders_is_considered() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.PathTo("Volumes/kcap/kcap.app");
        Bundle(source, "1.4.1");

        var plan = new ApplicationsMover(new Ditto("1.4.1"), Promote, apps).Inspect(source);

        await Assert.That(plan.Replaces.All(root => root.StartsWith(apps, StringComparison.Ordinal))).IsTrue();
    }
}
