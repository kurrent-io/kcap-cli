using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class CurateApplyFileWriteTests {
    static string NewTempDir() {
        var dir = Path.Combine(Path.GetTempPath(), "kcap-curate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task WriteFileAtomic_writes_content_and_leaves_no_tmp() {
        var dir  = NewTempDir();
        var path = Path.Combine(dir, "AGENTS.md");

        CurateCommand.WriteFileAtomic(path, "hello\n");

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("hello\n");
        await Assert.That(File.Exists(path + ".tmp")).IsFalse();
        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task WriteFileAtomic_preserves_existing_unix_mode() {
        if (OperatingSystem.IsWindows()) return;

        var dir  = NewTempDir();
        var path = Path.Combine(dir, "CLAUDE.md");
        await File.WriteAllTextAsync(path, "old\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var before = File.GetUnixFileMode(path);

        CurateCommand.WriteFileAtomic(path, "new\n");

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(before);
        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("new\n");
        Directory.Delete(dir, recursive: true);
    }
}
