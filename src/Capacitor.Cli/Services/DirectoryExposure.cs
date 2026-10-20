namespace Capacitor.Cli.Services;

/// <summary>Which directories on the path to a service unit let accounts other than the owner replace what
/// is inside them.
///
/// <para>A unit written <c>0600</c> into a directory nobody else can write is still replaceable if
/// something ABOVE it is: write permission on a directory is permission to rename its entries, so an
/// account that can write <c>~/.config</c> can swap the whole <c>systemd/user</c> directory for one of its
/// own. The mode of the unit and of its immediate directory says nothing about that.</para>
///
/// <para>Mode bits are half of it: a directory's owner can rename its entries whatever the bits say. So
/// <see cref="ForeignOwned(string)"/> reports the ancestors owned by another account, and
/// <see cref="GrantingWrite"/> the ones whose bits let another class write.</para></summary>
static class DirectoryExposure {
    /// <summary>The directories from <paramref name="directory"/> up to the filesystem root that let a
    /// class named in <paramref name="writeBits"/> replace their entries, nearest first. Empty means no
    /// mode bit on the path grants it.
    ///
    /// <para>Write alone is not enough: renaming an entry needs write AND execute on its directory, since
    /// without execute that class cannot resolve a name inside it at all. Each class is paired with its own
    /// execute bit, so a <c>drwxrw-rw-</c> ancestor — writable by everyone, traversable by no one — is not
    /// reported, and an install beneath it is not blocked.</para>
    ///
    /// <para>A sticky directory never counts: that bit restricts rename and unlink to an entry's own owner
    /// and the directory's owner, which is what makes root's <c>/tmp</c> at <c>1777</c> safe to hold a file
    /// nobody else may replace. A sticky directory owned by another account is
    /// <see cref="ForeignOwned(string)"/>'s to report.</para>
    ///
    /// <para>Symlinks are resolved component by component first. <see cref="Path.GetFullPath(string)"/> is
    /// lexical, so ascending from an unresolved path walks the link's parents rather than the target's: with
    /// <c>/home/bob</c> pointing at <c>/shared/homes/bob</c>, the unresolved walk never sees <c>/shared</c>,
    /// which is exactly where a shared writable directory would be.</para>
    ///
    /// <para>An ancestor whose mode cannot be read is skipped rather than assumed hostile — it is one this
    /// process cannot reach past either, so the write it guards fails on its own.</para></summary>
    public static IReadOnlyList<string> GrantingWrite(string directory, UnixFileMode writeBits) {
        if (OperatingSystem.IsWindows()) return [];

        var found = new List<string>();

        foreach (var d in Ancestors(directory)) {
            UnixFileMode mode;

            try { mode = File.GetUnixFileMode(d); } catch (Exception) { continue; }

            if (mode.HasFlag(UnixFileMode.StickyBit)) continue;

            if (Grants(mode, writeBits, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute)
             || Grants(mode, writeBits, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute))
                found.Add(d);
        }

        return found;
    }

    /// <summary>The directories from <paramref name="directory"/> up to the filesystem root owned by an
    /// account other than root and this process's effective user, nearest first, each with its owner. Such
    /// an owner can rename what the directory holds whatever its mode. Root is exempt: it owns <c>/</c> and
    /// the home roots everywhere, and can replace anything regardless.
    ///
    /// <para>A directory whose owner cannot be read is skipped, as an unreadable mode is above: the mode
    /// check still applies to it.</para></summary>
    public static IReadOnlyList<(string Directory, uint Owner)> ForeignOwned(string directory) =>
        OperatingSystem.IsWindows() ? [] : ForeignOwned(directory, FileOwner.Of, FileOwner.Effective);

    internal static IReadOnlyList<(string Directory, uint Owner)> ForeignOwned(
            string directory, Func<string, uint?> ownerOf, uint self) {
        var found = new List<(string, uint)>();

        foreach (var d in Ancestors(directory)) {
            if (IsLinkOutsideStickyDirectory(d)) continue;
            if (ownerOf(d) is { } owner && owner != 0 && owner != self) found.Add((d, owner));
        }

        return found;
    }

    /// <summary>A link cannot be repointed in place, only unlinked and recreated, which its owner may do
    /// without write on its directory only when that directory is sticky. Elsewhere the link's own owner
    /// holds no power over the path, and <c>stat</c> would report it rather than its target's.</summary>
    static bool IsLinkOutsideStickyDirectory(string path) {
        if (OperatingSystem.IsWindows()) return false;

        try {
            if (new FileInfo(path).LinkTarget is null) return false;

            return Path.GetDirectoryName(path) is not { } holder
                || !File.GetUnixFileMode(holder).HasFlag(UnixFileMode.StickyBit);
        } catch (Exception) {
            return false;
        }
    }

    /// <summary>The directory the kernel reaches for <paramref name="directory"/>, every symlinked component
    /// replaced by its target.</summary>
    public static string Physical(string directory) => Resolve(Path.GetFullPath(directory), depth: 0, []);

    static bool Grants(UnixFileMode mode, UnixFileMode writeBits, UnixFileMode write, UnixFileMode execute) =>
        writeBits.HasFlag(write) && mode.HasFlag(write) && mode.HasFlag(execute);

    /// <summary>The directories the kernel walks to reach <paramref name="directory"/>, nearest first, then
    /// every directory that holds a symlink met on the way, with their own ancestors: whoever can rename a
    /// link can point the path elsewhere, so a link's directory counts as much as the ones it leads to.
    /// The given path's own lexical chain is the first such holder.</summary>
    static IEnumerable<string> Ancestors(string directory) {
        var holders = new List<string> { Path.GetFullPath(directory) };
        var physical = Resolve(Path.GetFullPath(directory), depth: 0, holders);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in holders.Prepend(physical))
            for (var d = start; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)!)
                if (seen.Add(d)) yield return d;
    }

    /// <summary>The path with every symlinked component replaced by its target, so the walk ascends the
    /// directories the kernel will, not the ones the string suggests. <paramref name="holders"/> collects
    /// the directory holding each link met. A substituted target is resolved again rather than accepted as
    /// written: the target is an ordinary path and may itself sit under links, so accepting it leaves the
    /// very prefix this exists to resolve unresolved. A Mac shows it without any exotic setup — a link into <c>/var/...</c> lands back
    /// on the symlink into <c>/private</c>.</summary>
    static string Resolve(string full, int depth, List<string> holders) {
        if (depth > 20) return full;   // a cycle ResolveLinkTarget did not refuse

        var root    = Path.GetPathRoot(full)!;
        var current = root;

        foreach (var part in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);

            // A cycle or a dangling link throws rather than resolving; the lexical component stands in,
            // and its own mode read then decides.
            try {
                // One hop at a time: a link to a link is met again inside its target's own walk, so the
                // directory holding every link in a chain is recorded.
                if (Directory.ResolveLinkTarget(current, returnFinalTarget: false) is { } target) {
                    holders.Add(Path.GetDirectoryName(current)!);
                    current = Resolve(target.FullName, depth + 1, holders);
                }
            } catch (IOException) { /* keep the lexical component */ }
        }

        return current;
    }
}
