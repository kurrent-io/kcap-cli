using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class DurableDirectoryTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Ensure_syncs_every_parent_below_the_anchor_even_when_the_directory_exists() {
        if (OperatingSystem.IsWindows()) return;

        var anchor = Tmp.CreateDir("anchor").Path;
        var leaf   = Path.Combine(anchor, "a", "b", "c");
        string[] expected = [$"sync:{anchor}", $"sync:{Path.Combine(anchor, "a")}", $"sync:{Path.Combine(anchor, "a", "b")}"];

        for (var pass = 0; pass < 2; pass++) {
            var syncs = new List<string>();
            using (FaultPoints.Arm(p => { if (p.StartsWith("sync:", StringComparison.Ordinal)) syncs.Add(p); }))
                DurableDirectory.Ensure(leaf, anchor, null);

            await Assert.That(string.Join("|", syncs)).IsEqualTo(string.Join("|", expected));
        }
    }

    [Test]
    public async Task Ensure_applies_the_mode_to_every_created_component() {
        if (OperatingSystem.IsWindows()) return;

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var anchor = Tmp.CreateDir("anchor").Path;

        DurableDirectory.Ensure(Path.Combine(anchor, "a", "b"), anchor, ownerOnly);

        await Assert.That(File.GetUnixFileMode(Path.Combine(anchor, "a"))).IsEqualTo(ownerOnly);
        await Assert.That(File.GetUnixFileMode(Path.Combine(anchor, "a", "b"))).IsEqualTo(ownerOnly);
    }

    [Test]
    public async Task AnchorFor_is_the_home_when_the_path_is_under_it_else_the_filesystem_root() {
        if (OperatingSystem.IsWindows()) return;

        var home = new UserHome("/home/me");

        await Assert.That(DurableDirectory.AnchorFor("/home/me/.config/kcap", home)).IsEqualTo("/home/me");
        await Assert.That(DurableDirectory.AnchorFor("/srv/stack", home)).IsEqualTo("/");
        await Assert.That(DurableDirectory.AnchorFor("/home/meow/x", home)).IsEqualTo("/");
        await Assert.That(DurableDirectory.AnchorFor("/srv/stack", null)).IsEqualTo("/");
    }

    [Test]
    public async Task A_fresh_ensure_interleaves_each_mkdir_with_the_sync_of_its_parent() {
        if (OperatingSystem.IsWindows()) return;

        var anchor = Tmp.CreateDir("anchor").Path;
        var a      = Path.Combine(anchor, "a");
        var b      = Path.Combine(a, "b");
        var hits   = new List<string>();

        using (FaultPoints.Arm(hits.Add)) DurableDirectory.Ensure(b, anchor, null);

        await Assert.That(string.Join("|", hits)).IsEqualTo($"mkdir:{a}|sync:{anchor}|mkdir:{b}|sync:{a}");
    }

    [Test]
    public async Task A_root_resolved_with_a_home_anchors_there() {
        if (OperatingSystem.IsWindows()) return;

        var home = new UserHome(Tmp.CreateDir("home").Path);
        var root = LocalRoot.Resolve(null, home);
        var syncs = new List<string>();

        using (FaultPoints.Arm(p => { if (p.StartsWith("sync:", StringComparison.Ordinal)) syncs.Add(p[5..]); }))
            root.EnsureCreated();

        await Assert.That(syncs).IsNotEmpty();
        await Assert.That(syncs.All(s => s == home.Path || s.StartsWith(home.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task An_unreadable_preexisting_ancestor_is_skipped() {
        if (OperatingSystem.IsWindows()) return;

        var anchor = Tmp.CreateDir("anchor").Path;
        var middle = Tmp.CreateDir("anchor/middle").Path;
        var leaf   = Path.Combine(middle, "leaf");
        Directory.CreateDirectory(leaf);
        // Executable but unreadable: the parent of "leaf" cannot be opened for sync.
        File.SetUnixFileMode(middle, UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        try {
            if (Environment.UserName == "root") return;

            await Assert.That(() => DurableDirectory.Ensure(Path.Combine(leaf, "new"), anchor, null)).ThrowsNothing();
        } finally {
            File.SetUnixFileMode(middle, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
