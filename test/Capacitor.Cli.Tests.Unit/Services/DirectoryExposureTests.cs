using System.Runtime.Versioning;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>A unit written 0600 into a directory nobody else can write is still replaceable when something
/// above it is writable — renaming an entry needs write on its directory, not on the entry.</summary>
public class DirectoryExposureTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Reports_the_nearest_granting_ancestor_first() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var outer = Tmp.CreateDir("outer").Path;
        var inner = Tmp.CreateDir("outer", "inner").Path;
        var leaf  = Tmp.CreateDir("outer", "inner", "leaf").Path;
        try {
            foreach (var d in new[] { outer, inner }) GroupWritable(d);

            var found = DirectoryExposure.GrantingWrite(leaf, UnixFileMode.GroupWrite);

            // Resolved: a Mac's temp root is under /var, itself a symlink into /private, and the walk
            // reports the directories the kernel sees.
            await Assert.That(found.Take(2))
                .IsEquivalentTo(new[] { Tmp.GetResolvedPath("outer", "inner"), Tmp.GetResolvedPath("outer") })
                .Because("nearest first, and the leaf itself grants nothing");
        } finally {
            Restore(inner, outer);
        }
    }

    /// <summary>Renaming an entry needs write AND execute on its directory: without execute that class
    /// cannot resolve a name inside it at all. A directory writable by everyone and traversable by no one
    /// is not a hazard, and blocking an install beneath it would be a refusal with nothing behind it.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Write_without_execute_is_not_exposure() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var parent = Tmp.CreateDir("parent").Path;
        var leaf   = Tmp.CreateDir("parent", "leaf").Path;
        try {
            File.SetUnixFileMode(parent, OwnerOnly | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);

            await Assert.That(DirectoryExposure.GrantingWrite(leaf, UnixFileMode.OtherWrite))
                .DoesNotContain(Tmp.GetResolvedPath("parent"));

            File.SetUnixFileMode(parent, OwnerOnly | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            await Assert.That(DirectoryExposure.GrantingWrite(leaf, UnixFileMode.OtherWrite))
                .Contains(Tmp.GetResolvedPath("parent"))
                .Because("write plus execute is the pair that allows a rename");
        } finally {
            Restore(parent);
        }
    }

    /// <summary>The sticky bit restricts rename and unlink to an entry's own owner, which is what makes
    /// /tmp at 1777 safe to hold a file nobody else may replace. Without the exemption every temp-rooted
    /// path on Linux reports its own root, and no install under one could ever succeed.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_sticky_directory_never_counts_however_permissive() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var sticky = Tmp.CreateDir("sticky").Path;
        var leaf   = Tmp.CreateDir("sticky", "leaf").Path;
        try {
            File.SetUnixFileMode(sticky,
                OwnerOnly |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
                UnixFileMode.StickyBit);

            await Assert.That(DirectoryExposure.GrantingWrite(leaf, UnixFileMode.OtherWrite))
                .DoesNotContain(Tmp.GetResolvedPath("sticky"));
        } finally {
            Restore(sticky);
        }
    }

    /// <summary>A symlinked component is followed to its target, so the walk ascends the directories the
    /// kernel will. Unresolved, the ascent would climb the link's own parents and never reach the target's
    /// — which is where a shared writable directory lives when a home is mounted from elsewhere.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Ascends_the_targets_parents_through_a_symlinked_component() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        var shared = Tmp.CreateDir("shared").Path;            // the hidden hazard
        var real   = Tmp.CreateDir("shared", "home").Path;
        var link   = Tmp.PathTo("link");                      // link -> shared/home
        try {
            Directory.CreateSymbolicLink(link, real);
            GroupWritable(shared);

            var found = DirectoryExposure.GrantingWrite(Path.Combine(link, "unit"), UnixFileMode.GroupWrite);

            await Assert.That(found).Contains(Tmp.GetResolvedPath("shared"))
                .Because("the link's lexical parent is the temp root, which grants nothing");
        } finally {
            Restore(shared);
        }
    }

    /// <summary>The ascent stops at the filesystem root instead of looping on it.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Terminates_at_the_filesystem_root() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        await Assert.That(DirectoryExposure.GrantingWrite("/", UnixFileMode.OtherWrite)).IsEmpty();
    }

    [UnsupportedOSPlatform("windows")]
    static void GroupWritable(string directory) =>
        File.SetUnixFileMode(directory,
            OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

    // Before the TempDir is disposed — the recursive delete needs the modes back.
    [UnsupportedOSPlatform("windows")]
    static void Restore(params string[] directories) {
        foreach (var d in directories)
            try { File.SetUnixFileMode(d, OwnerOnly); } catch { /* best-effort */ }
    }
}
