using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary><c>bin/current</c> as each platform makes it: a symlink renamed into place on Unix, a junction on
/// Windows.</summary>
public class LocalBinTests {
    [TempDir] public required TempDir Tmp { get; init; }

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("r"), new UserHome(Tmp.CreateDir("home")));

    void Stage(string version) => Directory.CreateDirectory(Path.Combine(LocalBin.Directory(Root), version));

    [Test]
    public async Task Switch_names_the_version_current_reads() {
        Stage("1.2.3");

        LocalBin.Switch(Root, "1.2.3");

        await Assert.That(LocalBin.Current(Root)).IsEqualTo("1.2.3");
    }

    [Test]
    public async Task A_second_switch_repoints_current_and_keeps_the_previous_version() {
        Stage("1.2.3");
        Stage("1.3.0");

        LocalBin.Switch(Root, "1.2.3");
        LocalBin.Switch(Root, "1.3.0");

        await Assert.That(LocalBin.Current(Root)).IsEqualTo("1.3.0");
        await Assert.That(LocalBin.Has(Root, "1.2.3")).IsTrue();
    }

    [Test]
    public async Task Discard_never_removes_the_current_version() {
        Stage("1.3.0");
        LocalBin.Switch(Root, "1.3.0");

        LocalBin.Discard(Root, "x", "1.3.0");

        await Assert.That(LocalBin.Has(Root, "1.3.0")).IsTrue();
    }
}
