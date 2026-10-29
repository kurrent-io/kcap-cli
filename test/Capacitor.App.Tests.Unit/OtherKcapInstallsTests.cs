using Capacitor.App.Platform.Mac;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class OtherKcapInstallsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    string Bundle => Tmp.GetResolvedPath("Kcap.app");

    string Exe(string relative) {
        Tmp.CreateExecutable(relative, "#!/bin/sh\n");
        return Tmp.GetResolvedPath(relative);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Ignores_links_into_the_bundle() {
        var inside = Exe("Kcap.app/Contents/MacOS/kcap");
        var bin    = Tmp.CreateDir("bin");
        File.CreateSymbolicLink(Path.Combine(bin, "kcap"), inside);

        var found = OtherKcapInstalls.Find(bin, Bundle);

        await Assert.That(found.Count).IsEqualTo(0);
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Reports_an_npm_install_with_its_command() {
        var real = Exe("lib/node_modules/@kurrent/kcap/bin/kcap");
        var bin  = Tmp.CreateDir("bin");
        File.CreateSymbolicLink(Path.Combine(bin, "kcap"), real);

        var found = OtherKcapInstalls.Find(bin, Bundle);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].RemoveCommand).IsEqualTo("npm uninstall -g @kurrent/kcap");
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Reports_an_unknown_install_without_a_command() {
        Exe("bin/kcap");

        var found = OtherKcapInstalls.Find(Tmp.GetResolvedPath("bin"), Bundle);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].RemoveCommand).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task Reports_one_install_reached_twice() {
        var real = Exe("real/kcap");
        var a    = Tmp.CreateDir("a");
        var b    = Tmp.CreateDir("b");
        File.CreateSymbolicLink(Path.Combine(a, "kcap"), real);
        File.CreateSymbolicLink(Path.Combine(b, "kcap"), real);

        var found = OtherKcapInstalls.Find($"{a}:{b}", Bundle);

        await Assert.That(found.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Null_path_finds_nothing() {
        await Assert.That(OtherKcapInstalls.Find(null, Bundle).Count).IsEqualTo(0);
    }
}
