using System.Runtime.Versioning;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

[RunOn(OS.Windows)]
[SupportedOSPlatform("windows")]
public class WindowsJunctionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Replace_creates_a_missing_junction() {
        var target = Tmp.CreateDir("v1");
        var link   = Tmp.PathTo("current");

        WindowsJunction.Replace(link, target);

        await Assert.That(new DirectoryInfo(link).LinkTarget).IsEqualTo(target);
    }

    [Test]
    public async Task Replace_repoints_an_existing_junction_and_keeps_both_targets() {
        var v1   = Tmp.CreateDir("v1");
        var v2   = Tmp.CreateDir("v2");
        var link = Tmp.PathTo("current");
        File.WriteAllText(Path.Combine(v1, "marker"), "1");

        WindowsJunction.Replace(link, v1);
        WindowsJunction.Replace(link, v2);

        await Assert.That(new DirectoryInfo(link).LinkTarget).IsEqualTo(v2);
        await Assert.That(File.Exists(Path.Combine(v1, "marker"))).IsTrue();
    }

    [Test]
    public async Task A_target_with_shell_metacharacters_is_taken_literally() {
        var target = Tmp.CreateDir("a&b %PATH%");
        var link   = Tmp.PathTo("current");

        WindowsJunction.Replace(link, target);

        await Assert.That(new DirectoryInfo(link).LinkTarget).IsEqualTo(target);
    }
}
