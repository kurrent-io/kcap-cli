using Capacitor.App.Platform.Mac;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ApplicationRetirementTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Bundle(string path, string version = "1.4.1") {
        var root = Tmp.CreateDir(path);
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"),
            $"<plist><dict><key>CFBundleIdentifier</key><string>io.kurrent.kcap</string><key>CFBundleVersion</key><string>{version}</string></dict></plist>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "kcap-desktop"), "exe");
        return root;
    }

    ConfigRoot Config() => new(Tmp.CreateDir("config"));

    [Test]
    public async Task The_handed_over_bundle_is_removed_once_its_process_has_exited() {
        var config = Config();
        var old = Bundle("Applications/Kurrent Capacitor.app");
        var polls = 0;
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => ++polls < 3 ? [4242] : []);
        retirement.Schedule(KcapBundle.Read(old)!);

        var removed = await retirement.RunPendingAsync(Tmp.PathTo("Applications/kcap.app"), CancellationToken.None);

        await Assert.That(removed).IsEqualTo(old);
        await Assert.That(polls).IsEqualTo(3);
        await Assert.That(Directory.Exists(old)).IsFalse();
        await Assert.That(Directory.GetDirectories(Tmp.PathTo("Applications"))).IsEmpty();
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsFalse();
    }

    [Test]
    public async Task A_bundle_still_in_use_keeps_its_record_for_the_next_launch() {
        var config = Config();
        var old = Bundle("Applications/Kurrent Capacitor.app");
        var time = new FakeTimeProvider();
        var retirement = new ApplicationRetirement(config, time, _ => null);
        retirement.Schedule(KcapBundle.Read(old)!);

        var run = retirement.RunPendingAsync(null, CancellationToken.None);
        while (!run.IsCompleted) {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        await Assert.That(await run).IsNull();
        await Assert.That(Directory.Exists(old)).IsTrue();
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsTrue();
    }

    [Test]
    public async Task The_running_copy_is_never_removed() {
        var config = Config();
        var own = Bundle("Applications/kcap.app");
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);
        retirement.Schedule(KcapBundle.Read(own)!);

        await Assert.That(await retirement.RunPendingAsync(own, CancellationToken.None)).IsNull();
        await Assert.That(Directory.Exists(own)).IsTrue();
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsFalse();
    }

    [Test]
    public async Task A_bundle_replaced_since_the_hand_off_is_left_alone() {
        var config = Config();
        var old = Bundle("Applications/Kurrent Capacitor.app", "1.4.0");
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);
        retirement.Schedule(KcapBundle.Read(old)!);
        Directory.Delete(old, recursive: true);
        Bundle("Applications/Kurrent Capacitor.app", "1.4.2");

        await Assert.That(await retirement.RunPendingAsync(null, CancellationToken.None)).IsNull();
        await Assert.That(KcapBundle.Read(old)?.Version).IsEqualTo("1.4.2");
    }

    [Test]
    public async Task A_bundle_replaced_while_its_process_exits_is_left_alone() {
        var config = Config();
        var old = Bundle("Applications/Kurrent Capacitor.app", "1.4.0");
        var polls = 0;
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => {
            if (++polls > 1) return [];
            Directory.Delete(old, recursive: true);
            Bundle("Applications/Kurrent Capacitor.app", "1.4.2");
            return [4242];
        });
        retirement.Schedule(KcapBundle.Read(old)!);

        await Assert.That(await retirement.RunPendingAsync(null, CancellationToken.None)).IsNull();
        await Assert.That(KcapBundle.Read(old)?.Version).IsEqualTo("1.4.2");
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsFalse();
    }

    [Test]
    public async Task Cancel_withdraws_only_its_own_hand_off() {
        var config = Config();
        var old = Bundle("Applications/Kurrent Capacitor.app");
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);
        retirement.Schedule(KcapBundle.Read(old)!);

        retirement.Cancel(Tmp.PathTo("Applications/other.app"));
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsTrue();
        retirement.Cancel(old);
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsFalse();
    }

    [Test]
    public async Task A_removal_interrupted_after_the_rename_is_finished_on_the_next_launch() {
        var config = Config();
        var old = Tmp.PathTo("Applications/Kurrent Capacitor.app");
        var aside = Bundle("Applications/Kurrent Capacitor.app.retired-0123");
        File.WriteAllText(config.Path(ApplicationRetirement.FileName), string.Join('\n', old, "1.4.1", aside));
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);

        await retirement.RunPendingAsync(null, CancellationToken.None);

        await Assert.That(Directory.Exists(aside)).IsFalse();
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsFalse();
    }

    /// A delete cut short leaves a folder that no longer reads as kcap; it is finished by the name it was given.
    [Test]
    public async Task A_half_deleted_bundle_set_aside_is_still_removed() {
        var config = Config();
        var old = Tmp.PathTo("Applications/Kurrent Capacitor.app");
        var aside = Tmp.CreateDir("Applications/Kurrent Capacitor.app.retired-0123/Contents/Resources");
        File.WriteAllText(config.Path(ApplicationRetirement.FileName), string.Join('\n', old, "1.4.1", Path.GetDirectoryName(Path.GetDirectoryName(aside))));
        var unrelated = Tmp.CreateDir("Applications/Other.app");
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);

        await retirement.RunPendingAsync(null, CancellationToken.None);

        await Assert.That(Directory.Exists(Tmp.PathTo("Applications/Kurrent Capacitor.app.retired-0123"))).IsFalse();
        await Assert.That(Directory.Exists(unrelated)).IsTrue();
        await Assert.That(File.Exists(config.Path(ApplicationRetirement.FileName))).IsFalse();
    }

    [Test]
    public async Task A_recorded_aside_name_that_is_not_its_own_is_never_deleted() {
        var config = Config();
        var old = Tmp.PathTo("Applications/Kurrent Capacitor.app");
        var other = Tmp.CreateDir("Applications/Something Else");
        File.WriteAllText(config.Path(ApplicationRetirement.FileName), string.Join('\n', old, "1.4.1", other));
        var retirement = new ApplicationRetirement(config, TimeProvider.System, _ => []);

        await retirement.RunPendingAsync(null, CancellationToken.None);

        await Assert.That(Directory.Exists(other)).IsTrue();
    }
}
