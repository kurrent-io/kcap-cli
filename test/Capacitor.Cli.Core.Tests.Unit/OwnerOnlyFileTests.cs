namespace Capacitor.Cli.Core.Tests.Unit;

public class OwnerOnlyFileTests {
    [Test]
    public async Task Creates_the_file_owner_only_and_refuses_an_existing_path() {
        using var tmp = new TempDir();
        var path = tmp.PathTo("new.log");

        await using (var s = OwnerOnlyFile.CreateNew(path)) await s.WriteAsync("x"u8.ToArray());

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await Assert.That(() => OwnerOnlyFile.CreateNew(path)).Throws<IOException>();
    }

    [Test]
    public async Task Does_not_write_through_a_symlink_already_at_the_path() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir();
        var target = tmp.CreateFile("target.txt", "untouched");
        var link   = tmp.PathTo("link.log");
        File.CreateSymbolicLink(link, target);

        await Assert.That(() => OwnerOnlyFile.CreateNew(link)).Throws<IOException>();
        await Assert.That(await File.ReadAllTextAsync(target)).IsEqualTo("untouched");
    }
}
