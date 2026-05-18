using Kapacitor.Cli.Daemon.Services;

namespace Kapacitor.Cli.Tests.Unit;

public class FileSystemOverlayTests {
    [Test]
    public async Task OverlayDirectory_copies_regular_files() {
        using var tmp = new TempDir();
        var sourceFile = Path.Combine(tmp.Source, "file.txt");
        File.WriteAllText(sourceFile, "hello");

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "file.txt"))).IsTrue();
    }

    [Test]
    public async Task OverlayDirectory_skips_symlinked_files() {
        using var tmp = new TempDir();

        // Create an external file that the symlink points to
        var externalFile = Path.Combine(tmp.External, "secret.txt");
        File.WriteAllText(externalFile, "secret");

        // Create a symlink inside source pointing to external file
        var linkPath = Path.Combine(tmp.Source, "link.txt");
        File.CreateSymbolicLink(linkPath, externalFile);

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(File.Exists(Path.Combine(tmp.Dest, "link.txt"))).IsFalse();
    }

    [Test]
    public async Task OverlayDirectory_skips_symlinked_directories() {
        using var tmp = new TempDir();

        // Create an external directory with content
        var externalDir = Path.Combine(tmp.External, "extdir");
        Directory.CreateDirectory(externalDir);
        File.WriteAllText(Path.Combine(externalDir, "inside.txt"), "contents");

        // Create a symlinked subdir in source pointing to external dir
        var linkDir = Path.Combine(tmp.Source, "linked-dir");
        Directory.CreateSymbolicLink(linkDir, externalDir);

        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        await Assert.That(Directory.Exists(Path.Combine(tmp.Dest, "linked-dir"))).IsFalse();
    }

    [Test]
    public async Task OverlayDirectory_handles_symlink_loop_without_recursion() {
        using var tmp = new TempDir();

        // Create a symlink loop: source/loop → source (points back to its ancestor)
        var loopLink = Path.Combine(tmp.Source, "loop");
        Directory.CreateSymbolicLink(loopLink, tmp.Source);

        // Should complete without StackOverflowException or hang
        FileSystemOverlay.OverlayDirectory(tmp.Source, tmp.Dest);

        // No loop directory should have been created in dest
        await Assert.That(Directory.Exists(Path.Combine(tmp.Dest, "loop"))).IsFalse();
    }

    sealed class TempDir : IDisposable {
        readonly DirectoryInfo _root;

        public string Source { get; }
        public string Dest { get; }
        public string External { get; }

        public TempDir() {
            _root = Directory.CreateTempSubdirectory("kapacitor-overlay-test-");
            Source = Path.Combine(_root.FullName, "source");
            Dest = Path.Combine(_root.FullName, "dest");
            External = Path.Combine(_root.FullName, "external");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(Dest);
            Directory.CreateDirectory(External);
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
