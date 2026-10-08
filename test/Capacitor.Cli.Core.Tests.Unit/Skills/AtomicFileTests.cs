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
}
