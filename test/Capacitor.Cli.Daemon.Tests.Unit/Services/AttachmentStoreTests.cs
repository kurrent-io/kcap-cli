using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AttachmentStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Directory_is_hashed_under_the_state_dir_and_removed_without_following_links() {
        var store = new AttachmentStore(Tmp.Path);
        var dir = store.DirectoryFor("agent-1");
        await Assert.That(dir).IsEqualTo(Path.Combine(Tmp.Path, "attachments", AgentFileNames.For("agent-1")));
        Directory.CreateDirectory(dir);
        var outside = Tmp.CreateDir("outside");
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        File.CreateSymbolicLink(Path.Combine(dir, "link"), outside);
        store.Remove("agent-1");
        await Assert.That(Directory.Exists(dir)).IsFalse();
        await Assert.That(File.Exists(Path.Combine(outside, "keep.txt"))).IsTrue();
        store.Remove("agent-1"); // absent is fine
    }

    [Test]
    public async Task Sweep_removes_orphan_directories_and_stale_staging_only() {
        var store = new AttachmentStore(Tmp.Path);
        var live = store.DirectoryFor("live"); Directory.CreateDirectory(live);
        var dead = store.DirectoryFor("dead"); Directory.CreateDirectory(dead);
        var pending = Path.Combine(live, ".pending-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(pending);
        var published = Path.Combine(live, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(published);
        store.SweepOrphans(stem => stem == AgentFileNames.For("live"), NullLogger.Instance);
        await Assert.That(Directory.Exists(live)).IsTrue();
        await Assert.That(Directory.Exists(published)).IsTrue();
        await Assert.That(Directory.Exists(pending)).IsFalse();
        await Assert.That(Directory.Exists(dead)).IsFalse();
    }

    [Test]
    public async Task Lease_removes_on_dispose_unless_kept() {
        var store = new AttachmentStore(Tmp.Path);
        using (var lease = store.Lease("a")) { Directory.CreateDirectory(store.DirectoryFor("a")); }
        await Assert.That(Directory.Exists(store.DirectoryFor("a"))).IsFalse();
        using (var lease = store.Lease("b")) { Directory.CreateDirectory(store.DirectoryFor("b")); lease.Keep(); }
        await Assert.That(Directory.Exists(store.DirectoryFor("b"))).IsTrue();
    }

    [Test]
    public async Task Sweep_survives_a_faulting_enumeration_and_logs_a_warning() {
        if (OperatingSystem.IsWindows()) return; // Unix permission model only

        var store = new AttachmentStore(Tmp.Path);
        Directory.CreateDirectory(store.Root);
        var logger = new CapturingLogger();

        try {
            // No read/execute: Directory.Exists(Root) still succeeds (it only needs the parent's
            // search permission), but EnumerateDirectories's MoveNext throws — the fault this guards.
            File.SetUnixFileMode(store.Root, UnixFileMode.None);
            store.SweepOrphans(_ => true, logger);
        } finally {
            File.SetUnixFileMode(store.Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(logger.Warnings.Any(w => w.Contains("enumeration failed"))).IsTrue();
    }
}
