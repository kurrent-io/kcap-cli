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

            var found = DirectoryExposure.GrantingWrite(Tmp.PathTo("link", "unit"), UnixFileMode.GroupWrite);

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

    /// <summary>An owner renames a directory's entries whatever its mode, so another account's directory
    /// on the path is reported even when its bits grant nothing, and even when it is sticky. Root and this
    /// user are not, and an owner that cannot be read is no finding.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Reports_ancestors_owned_by_another_account_but_not_by_root_or_us() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX paths");

        var leaf  = Tmp.CreateDir("alice", "bob-home", "unit").Path;
        var alice = Tmp.GetResolvedPath("alice");
        var bob   = Tmp.GetResolvedPath("alice", "bob-home");
        const uint Self = 1001, Alice = 1002;
        File.SetUnixFileMode(alice, OwnerOnly | UnixFileMode.StickyBit);
        try {
            var found = DirectoryExposure.ForeignOwned(leaf, d =>
                d == alice ? Alice : d == bob ? Self : d == "/" ? 0u : d.StartsWith(alice, StringComparison.Ordinal) ? Self : null,
                Self);

            await Assert.That(found).IsEquivalentTo(new[] { (alice, Alice) });
        } finally {
            Restore(alice);
        }
    }

    /// <summary>Whoever owns the directory holding a symlink on the path can repoint it, so that directory
    /// is checked as well as the ones the link leads to.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_foreign_owner_of_a_directory_holding_a_link_on_the_path_is_reported() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlinks");

        Tmp.CreateDir("mine", "unit");
        var holder = Tmp.CreateDir("alice").Path;
        File.CreateSymbolicLink(Tmp.PathTo("alice", "link"), Tmp.GetResolvedPath("mine"));
        const uint Self = 1001, Alice = 1002;

        var found = DirectoryExposure.ForeignOwned(Tmp.PathTo("alice", "link", "unit"),
            d => d == holder ? Alice : Self, Self);

        await Assert.That(found.Select(f => f.Directory)).Contains(holder);
    }

    /// <summary>A link cannot be repointed in place, so its own owner matters only where it may also unlink
    /// it: a sticky directory. Elsewhere the directory holding it decides, and that is checked on its own.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_links_own_owner_is_reported_only_inside_a_sticky_directory(bool sticky) {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlinks");

        Tmp.CreateDir("mine", "unit");
        var holder = Tmp.CreateDir("holder").Path;
        var link = Tmp.PathTo("holder", "link");
        File.CreateSymbolicLink(link, Tmp.GetResolvedPath("mine"));
        if (sticky) File.SetUnixFileMode(holder, File.GetUnixFileMode(holder) | UnixFileMode.StickyBit);
        const uint Self = 1001, Alice = 1002;

        var found = DirectoryExposure.ForeignOwned(Tmp.PathTo("holder", "link", "unit"), d => d == link ? Alice : Self, Self);

        await Assert.That(found.Select(f => f.Directory).Contains(link)).IsEqualTo(sticky);
    }

    /// <summary>A link reached through another link is held by a directory neither the given path nor the
    /// resolved one passes through; its owner can repoint the path all the same.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_foreign_owner_of_a_directory_holding_an_intermediate_link_is_reported() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlinks");

        Tmp.CreateDir("safe", "final", "unit");
        var holder = Tmp.CreateDir("foreign", "holder").Path;
        File.CreateSymbolicLink(Tmp.PathTo("foreign", "holder", "next"), Tmp.GetResolvedPath("safe", "final"));
        File.CreateSymbolicLink(Tmp.PathTo("safe", "entry"), holder);
        var resolvedHolder = Tmp.GetResolvedPath("foreign", "holder");
        const uint Self = 1001, Other = 1002;

        var found = DirectoryExposure.ForeignOwned(Tmp.PathTo("safe", "entry", "next", "unit"),
            d => d == resolvedHolder ? Other : Self, Self);

        await Assert.That(found.Select(f => f.Directory)).Contains(resolvedHolder);
    }

    /// <summary>A link pointing straight at another link: the second one's directory is on neither the
    /// given path nor the resolved one.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_foreign_owner_of_a_directory_holding_a_chained_link_is_reported() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlinks");

        Tmp.CreateDir("safe", "final", "unit");
        var holder = Tmp.CreateDir("foreign", "holder").Path;
        File.CreateSymbolicLink(Tmp.PathTo("foreign", "holder", "next"), Tmp.GetResolvedPath("safe", "final"));
        File.CreateSymbolicLink(Tmp.PathTo("safe", "entry"), Tmp.PathTo("foreign", "holder", "next"));
        var resolvedHolder = Tmp.GetResolvedPath("foreign", "holder");
        const uint Self = 1001, Other = 1002;

        var found = DirectoryExposure.ForeignOwned(Tmp.PathTo("safe", "entry", "unit"),
            d => d == resolvedHolder ? Other : Self, Self);

        await Assert.That(found.Select(f => f.Directory)).Contains(resolvedHolder);
    }

    /// <summary>The owner read is the system's own stat, so it must agree with the kernel on each target:
    /// a directory this process made is its own, and the root is root's.</summary>
    [Test]
    [ParallelLimiter<SubprocessLimit>]
    [UnsupportedOSPlatform("windows")]
    public async Task Reads_this_process_as_the_owner_of_its_own_directory_and_root_of_the_root() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX owners");

        await Assert.That(FileOwner.Available).IsTrue();
        await Assert.That(FileOwner.Of(Tmp.CreateDir("mine").Path)).IsEqualTo(FileOwner.Effective);
        await Assert.That(FileOwner.Of("/")).IsEqualTo(0u);
        await Assert.That(FileOwner.Of(Tmp.PathTo("absent"))).IsNull();
        await Assert.That(DirectoryExposure.ForeignOwned(Tmp.CreateDir("clean", "unit").Path)).IsEmpty()
            .Because("a temp directory's path is owned by this user and root");
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
