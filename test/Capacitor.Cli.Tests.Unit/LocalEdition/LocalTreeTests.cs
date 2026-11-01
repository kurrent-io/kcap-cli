using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalTreeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("r"), new UserHome(Tmp.CreateDir("home")));

    [Test]
    public async Task A_bin_holding_current_is_removed_without_following_it() {
        var outside = Tmp.CreateDir("outside");
        File.WriteAllText(Path.Combine(outside, "keep"), "x");
        var nested = Path.Combine(outside, "nested");
        Link(nested, Tmp.CreateDir("far"));
        Directory.CreateDirectory(Path.Combine(LocalBin.Directory(Root), "1.2.3"));
        LocalBin.Switch(Root, "1.2.3");
        Link(Path.Combine(LocalBin.Directory(Root), "elsewhere"), outside);

        await Assert.That(LocalTree.Remove(LocalBin.Directory(Root))).IsTrue();

        await Assert.That(Directory.Exists(LocalBin.Directory(Root))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(outside, "keep"))).IsTrue();
        await Assert.That(new DirectoryInfo(nested).LinkTarget).IsNotNull();
    }

    static void Link(string path, string target) {
        if (OperatingSystem.IsWindows()) WindowsJunction.Create(path, target);
        else Directory.CreateSymbolicLink(path, target);
    }

    [Test]
    public async Task A_link_at_the_top_is_removed_and_its_target_kept() {
        Directory.CreateDirectory(Path.Combine(LocalBin.Directory(Root), "1.2.3"));
        LocalBin.Switch(Root, "1.2.3");
        var current = Path.Combine(LocalBin.Directory(Root), "current");

        await Assert.That(LocalTree.Remove(current)).IsTrue();

        await Assert.That(new DirectoryInfo(current).LinkTarget).IsNull();
        await Assert.That(LocalBin.Has(Root, "1.2.3")).IsTrue();
    }

    [Test]
    public async Task Nothing_there_is_reported() =>
        await Assert.That(LocalTree.Remove(Tmp.PathTo("absent"))).IsFalse();
}
