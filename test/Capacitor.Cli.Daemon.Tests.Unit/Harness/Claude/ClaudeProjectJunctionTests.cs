using Capacitor.Cli.Daemon.Harness.Claude;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// Windows refuses a symlink without Developer Mode or elevation, so the worktree's Claude project
/// directory is linked to the source's with a junction instead. The cleanup relies on .NET seeing
/// it as a link: a delete must remove the link and leave the project's memory alone.
public class ClaudeProjectJunctionTests {
    [Test, RunOn(OS.Windows)]
    public async Task A_junction_links_the_project_directory_and_deletes_as_a_link() {
        using var tmp = new TempDir("junction");
        var target = tmp.CreateDir("source-project");
        tmp.CreateFile(["source-project", "memory.md"], "remember");
        var link = tmp.PathTo("worktree-project");

        ClaudeLauncher.CreateJunction(link, target);

        await Assert.That(File.ReadAllText(Path.Combine(link, "memory.md"))).IsEqualTo("remember");
        var info = new DirectoryInfo(link);
        await Assert.That(info.LinkTarget).IsNotNull();

        info.Delete();

        await Assert.That(Directory.Exists(link)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(target, "memory.md"))).IsTrue();
    }
}
