using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Tests.Unit.Skills;

public class AtomicFileTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Replace_writes_new_contents() {
        var path = Tmp.CreateFile("a.json", "old");

        AtomicFile.Replace(path, "new");

        await Assert.That(File.ReadAllText(path)).IsEqualTo("new");
    }

    [Test]
    public async Task Replace_keeps_the_destination_mode_when_none_is_given() {
        if (OperatingSystem.IsWindows()) return;
        var path = Tmp.CreateFile("a.toml", "old");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        AtomicFile.Replace(path, "new");

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Replace_applies_the_requested_mode_to_a_new_file() {
        if (OperatingSystem.IsWindows()) return;
        var path = Tmp.PathTo("new.json");

        AtomicFile.Replace(path, "x", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Replace_leaves_no_temporary_file_behind() {
        var path = Tmp.CreateFile("a.json", "old");

        AtomicFile.Replace(path, "new");

        await Assert.That(Directory.GetFiles(Tmp.Path)).IsEquivalentTo(new[] { path });
    }

    [Test]
    public async Task Replace_writes_through_a_symlink_and_keeps_the_link() {
        Skip.When(OperatingSystem.IsWindows(), "creating a symlink needs privileges Windows CI does not have");
        var target = Tmp.CreateFile("dotfiles/settings.json", "old");
        var link   = Tmp.PathTo("settings.json");
        File.CreateSymbolicLink(link, target);

        AtomicFile.Replace(link, "new");

        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
        await Assert.That(File.ReadAllText(target)).IsEqualTo("new");
        await Assert.That(Directory.GetFiles(Tmp.PathTo("dotfiles"))).IsEquivalentTo(new[] { target });
    }

    [Test]
    public async Task Replace_through_a_symlink_keeps_the_targets_mode() {
        if (OperatingSystem.IsWindows()) return;
        var target = Tmp.CreateFile("dotfiles/settings.json", "old");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Tmp.PathTo("settings.json");
        File.CreateSymbolicLink(link, target);

        AtomicFile.Replace(link, "new");

        await Assert.That(File.GetUnixFileMode(target)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Replace_through_a_dangling_link_creates_the_file_it_names() {
        Skip.When(OperatingSystem.IsWindows(), "creating a symlink needs privileges Windows CI does not have");
        var target = Tmp.CreateDir("dotfiles").PathTo("settings.json");
        var link   = Tmp.PathTo("settings.json");
        File.CreateSymbolicLink(link, target);

        AtomicFile.Replace(link, "new");

        await Assert.That(new FileInfo(link).LinkTarget).IsEqualTo(target);
        await Assert.That(File.ReadAllText(target)).IsEqualTo("new");
    }

    [Test]
    public async Task Replace_without_following_replaces_the_link_itself() {
        Skip.When(OperatingSystem.IsWindows(), "creating a symlink needs privileges Windows CI does not have");
        var target = Tmp.CreateFile("elsewhere/SKILL.md", "old");
        var link   = Tmp.PathTo("SKILL.md");
        File.CreateSymbolicLink(link, target);

        AtomicFile.Replace(link, "new", followLink: false);

        await Assert.That(new FileInfo(link).LinkTarget).IsNull();
        await Assert.That(File.ReadAllText(link)).IsEqualTo("new");
        await Assert.That(File.ReadAllText(target)).IsEqualTo("old");
    }
}
