using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class GuardedDiscoveryTests : IDisposable {
    readonly string _root = Directory.CreateTempSubdirectory("kcap-guarded").FullName;

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Test]
    public async Task EnumerateFiles_survives_symlink_cycle() {
        var sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "a.jsonl"), "{}");

        // A directory symlink pointing back at the root creates a cycle.
        var loop = Path.Combine(sub, "loop");
        try { Directory.CreateSymbolicLink(loop, _root); }
        catch { return; /* platform without symlink perms — skip */ }

        var files = GuardedDiscovery.EnumerateFiles(_root, "*.jsonl").ToList();

        await Assert.That(files.Count).IsEqualTo(1);
        await Assert.That(files[0]).EndsWith("a.jsonl");
    }

    [Test]
    public async Task EnumerateFiles_returns_empty_for_missing_root() {
        var files = GuardedDiscovery.EnumerateFiles(Path.Combine(_root, "does-not-exist"), "*.jsonl").ToList();
        await Assert.That(files.Count).IsEqualTo(0);
    }
}
