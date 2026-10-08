using Capacitor.App.Services;
using Capacitor.Cli.Core;

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

    static void CompleteBundle(string root, string version, string id = "io.kurrent.capacitor") {
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"),
            $"<plist><dict><key>CFBundleIdentifier</key><string>{id}</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "Kurrent Capacitor"), "exe");
    }

    static bool MovePromote(string from, string to) {
        if (Directory.Exists(to)) return false;
        Directory.Move(from, to);
        return true;
    }

    [Test]
    public async Task Complete_copy_is_promoted_and_nothing_is_left_staged() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        CompleteBundle(source);
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(outcome.InstalledPath).IsEqualTo(Path.Combine(apps, "Kurrent Capacitor.app"));
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Incomplete_copy_is_removed_and_reported() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
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
        Tmp.CreateDir("Applications/Kurrent Capacitor.app");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
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
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
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
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
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
        CompleteBundle(Tmp.PathTo("Applications/Kurrent Capacitor.app"));
        var source = Tmp.PathTo("Downloads/Kurrent Capacitor.app");
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
        var installed = Tmp.PathTo("Applications/Kurrent Capacitor.app");
        CompleteBundle(installed);
        var source = Tmp.PathTo("Downloads/Kurrent Capacitor.app");
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
        var installed = Tmp.PathTo("Applications/Kurrent Capacitor.app");
        CompleteBundle(installed);
        var source = Tmp.PathTo("Downloads/Kurrent Capacitor.app");
        CompleteBundle(source, "1.1.0");
        var mover = new ApplicationsMover(new FakeDitto(root => CompleteBundle(root, "1.1.0")),
            MovePromote, apps, ApplicationsMover.SwapAtomic);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(installed, "Contents", "Info.plist"))).Contains("1.1.0");
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Running_installed_copy_is_not_replaced() {
        var apps = Tmp.CreateDir("Applications");
        CompleteBundle(Tmp.PathTo("Applications/Kurrent Capacitor.app"));
        var source = Tmp.PathTo("Downloads/Kurrent Capacitor.app");
        CompleteBundle(source, "1.1.0");
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps, (_, _) => true, _ => true);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsFalse();
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task A_destination_changed_during_copy_is_not_overwritten() {
        var apps = Tmp.CreateDir("Applications");
        var installed = Tmp.PathTo("Applications/Kurrent Capacitor.app");
        CompleteBundle(installed);
        var source = Tmp.PathTo("Downloads/Kurrent Capacitor.app");
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
        CompleteBundle(Tmp.PathTo("Applications/Kurrent Capacitor.app"), "1.0.0", "another.application");
        var source = Tmp.PathTo("Downloads/Kurrent Capacitor.app");
        CompleteBundle(source, "1.1.0");
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps, (_, _) => true);
        await Assert.That((await mover.MoveAsync(source, CancellationToken.None)).Moved).IsFalse();
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }
}
