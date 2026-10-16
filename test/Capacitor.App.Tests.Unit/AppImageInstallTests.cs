using Capacitor.Cli.Core;
using Capacitor.App.Platform;
using Capacitor.App.Platform.Linux;

namespace Capacitor.App.Tests.Unit;

public class AppImageInstallTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string TakeCliOutOfMount(string image = "/apps/Kcap.AppImage") =>
        new AppImageInstall(image).TakeCliOutOfMount(_ => null, new UserHome(Tmp.PathTo("home")), Tmp.PathTo("mount"));

    [Test]
    public async Task The_cli_runs_from_bin_when_the_bundle_has_one_and_beside_the_app_otherwise() {
        Tmp.CreateFile("linux/bin/kcap", "cli");
        Tmp.CreateFile("mac/kcap", "cli");

        await Assert.That(BundleLayout.CliDirectory(Tmp.PathTo("linux"))).IsEqualTo(Tmp.PathTo("linux", "bin"));
        await Assert.That(BundleLayout.CliDirectory(Tmp.PathTo("mac"))).IsEqualTo(Tmp.PathTo("mac"));
    }

    [Test]
    public async Task The_cli_and_plugin_are_copied_out_of_the_mount_and_refreshed_when_they_change() {
        Tmp.CreateFile("mount/bin/kcap", "cli v1");
        Tmp.CreateFile("mount/bin/kcap-daemon", "daemon v1");
        Tmp.CreateFile("mount/kcap/.claude-plugin/plugin.json", "v1");
        Tmp.CreateFile("mount/kcap/hooks/old.json", "old");
        var staged = Tmp.PathTo("home", ".local", "share", "kcap", "desktop", "bin");
        var plugin = Tmp.PathTo("home", ".local", "share", "kcap", "desktop", "kcap");

        await Assert.That(TakeCliOutOfMount()).IsEqualTo(staged);
        await Assert.That(File.ReadAllText(Path.Combine(staged, "kcap-daemon"))).IsEqualTo("daemon v1");
        await Assert.That(File.Exists(Path.Combine(staged, "libpty_shim.so"))).IsFalse();
        await Assert.That(File.ReadAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"))).IsEqualTo("v1");

        File.WriteAllText(Tmp.PathTo("mount", "bin", "kcap-daemon"), "daemon v2");
        File.WriteAllText(Tmp.PathTo("mount", "kcap", ".claude-plugin", "plugin.json"), "v2");
        File.Delete(Tmp.PathTo("mount", "kcap", "hooks", "old.json"));
        TakeCliOutOfMount();

        await Assert.That(File.ReadAllText(Path.Combine(staged, "kcap-daemon"))).IsEqualTo("daemon v2");
        await Assert.That(Directory.GetFiles(staged).Length).IsEqualTo(2);
        await Assert.That(File.ReadAllText(Path.Combine(plugin, ".claude-plugin", "plugin.json"))).IsEqualTo("v2");
        await Assert.That(File.Exists(Path.Combine(plugin, "hooks", "old.json"))).IsFalse();
        await Assert.That(Directory.GetDirectories(Path.GetDirectoryName(plugin)!).Length).IsEqualTo(2);
    }

    [Test]
    public async Task An_unchanged_image_skips_the_bundle_comparison_and_a_changed_one_restages() {
        var image = Tmp.CreateFile("Kcap.AppImage", "image v1");
        Tmp.CreateFile("mount/bin/kcap", "cli v1");
        Tmp.CreateFile("mount/bin/kcap-daemon", "daemon v1");
        var staged = Path.Combine(TakeCliOutOfMount(image), "kcap-daemon");

        File.WriteAllText(Tmp.PathTo("mount", "bin", "kcap-daemon"), "daemon v2");
        TakeCliOutOfMount(image);
        await Assert.That(File.ReadAllText(staged)).IsEqualTo("daemon v1");

        File.Delete(staged);
        TakeCliOutOfMount(image);
        await Assert.That(File.ReadAllText(staged)).IsEqualTo("daemon v2");

        File.WriteAllText(Tmp.PathTo("mount", "bin", "kcap-daemon"), "daemon v3");
        File.WriteAllText(image, "image v2 is longer");
        TakeCliOutOfMount(image);
        await Assert.That(File.ReadAllText(staged)).IsEqualTo("daemon v3");
    }
}
