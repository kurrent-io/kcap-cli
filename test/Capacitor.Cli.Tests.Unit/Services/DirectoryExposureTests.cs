using System.Runtime.Versioning;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>A unit written 0600 into a directory nobody else can write is still replaceable when something
/// above it is writable — renaming an entry needs write on its directory, not on the entry.</summary>
public class DirectoryExposureTests {
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Reports_the_nearest_granting_ancestor_first() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var outer     = tmp.CreateDir("outer").Path;
        var inner     = tmp.CreateDir("outer", "inner").Path;
        var leaf      = tmp.CreateDir("outer", "inner", "leaf").Path;
        try {
            foreach (var d in new[] { outer, inner })
                File.SetUnixFileMode(d,
                    UnixFileMode.UserRead  | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);

            var found = DirectoryExposure.GrantingWrite(leaf, UnixFileMode.GroupWrite);

            await Assert.That(found.Take(2)).IsEquivalentTo(new[] { inner, outer })
                .Because("nearest first, and the leaf itself grants nothing");
        } finally {
            foreach (var d in new[] { inner, outer })
                try {
                    File.SetUnixFileMode(d,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                } catch { /* best-effort */ }
        }
    }

    /// <summary>The sticky bit restricts rename and unlink to an entry's own owner, which is what makes
    /// /tmp at 1777 safe to hold a file nobody else may replace. Without the exemption every temp-rooted
    /// path on Linux reports its own root, and no install under one could ever succeed.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_sticky_directory_never_counts_however_permissive() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();
        var sticky    = tmp.CreateDir("sticky").Path;
        var leaf      = tmp.CreateDir("sticky", "leaf").Path;
        try {
            File.SetUnixFileMode(sticky,
                UnixFileMode.UserRead   | UnixFileMode.UserWrite   | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead  | UnixFileMode.GroupWrite  | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead  | UnixFileMode.OtherWrite  | UnixFileMode.OtherExecute |
                UnixFileMode.StickyBit);

            await Assert.That(DirectoryExposure.GrantingWrite(leaf, UnixFileMode.OtherWrite))
                .DoesNotContain(sticky);
        } finally {
            try {
                File.SetUnixFileMode(sticky,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            } catch { /* best-effort */ }
        }
    }

    /// <summary>The walk reaches the filesystem root rather than stopping at the first readable parent, so
    /// a permissive directory high up is still found.</summary>
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task Walks_all_the_way_to_the_root() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX file modes");

        using var tmp = new TempDir();

        // UserExecute, because every directory on the way up is owner-traversable — including "/", which
        // only appears if the walk runs to completion.
        await Assert.That(DirectoryExposure.GrantingWrite(tmp.Path, UnixFileMode.UserExecute))
            .Contains("/");
    }
}
