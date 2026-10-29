using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// <summary>A repository is named by its last segment, whether the path is this host's or a Windows daemon's,
/// and a drive or share root has no name.</summary>
public class RepoLabelTests {
    [Test]
    [Arguments("/work/kcap", "kcap")]
    [Arguments("/work/kcap/", "kcap")]
    [Arguments(@"Z:\x\Engine", "Engine")]
    [Arguments(@"Z:\x\Engine\", "Engine")]
    [Arguments("C:/x/repo", "repo")]
    [Arguments(@"C:\", "")]
    [Arguments("C:", "")]
    [Arguments(@"\\server\share", "")]
    [Arguments(@"\\server\share\repo", "repo")]
    public async Task A_path_is_named_by_its_last_segment(string path, string leaf) =>
        await Assert.That(RepoLabel.Leaf(path)).IsEqualTo(leaf);

    [Test]
    public async Task A_backslash_in_a_unix_name_stays_part_of_it() {
        Skip.Unless(!OperatingSystem.IsWindows(), "a backslash separates segments on Windows");

        await Assert.That(RepoLabel.Leaf(@"/work/foo\bar")).IsEqualTo(@"foo\bar");
    }
}
