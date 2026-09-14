namespace Capacitor.Cli.Services;

/// <summary>Which directories on the path to a service unit let accounts other than the owner replace what
/// is inside them.
///
/// <para>A unit written <c>0600</c> into a directory nobody else can write is still replaceable if
/// something ABOVE it is: write permission on a directory is permission to rename its entries, so an
/// account that can write <c>~/.config</c> can swap the whole <c>systemd/user</c> directory for one of its
/// own. The mode of the unit and of its immediate directory says nothing about that.</para>
///
/// <para><b>Mode bits only.</b> Ownership is not consulted, so a directory owned by another unprivileged
/// account is reported safe while its owner can still rename what it holds — <c>0755</c> owned by someone
/// else, or a sticky directory whose owner is not us. Reading an owner needs a <c>stat</c> this runtime
/// does not expose. Treat a clean result as "no mode bit grants it", never as "no other account can".</para></summary>
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
    /// <para>A sticky directory never counts: that bit restricts rename and unlink to an entry's own owner,
    /// which is what makes <c>/tmp</c> at <c>1777</c> safe to hold a file nobody else may replace. The
    /// exemption is mode-deep like the rest — a sticky directory's own owner retains that power, so a
    /// foreign-owned one is exempted here although it should not be.</para>
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

        for (var d = Physical(directory); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)!) {
            UnixFileMode mode;

            try { mode = File.GetUnixFileMode(d); } catch (Exception) { continue; }

            if (mode.HasFlag(UnixFileMode.StickyBit)) continue;

            if (Grants(mode, writeBits, UnixFileMode.GroupWrite, UnixFileMode.GroupExecute)
             || Grants(mode, writeBits, UnixFileMode.OtherWrite, UnixFileMode.OtherExecute))
                found.Add(d);
        }

        return found;
    }

    static bool Grants(UnixFileMode mode, UnixFileMode writeBits, UnixFileMode write, UnixFileMode execute) =>
        writeBits.HasFlag(write) && mode.HasFlag(write) && mode.HasFlag(execute);

    /// <summary>The path with every symlinked component replaced by its target, so the walk ascends the
    /// directories the kernel will, not the ones the string suggests.</summary>
    static string Physical(string path) => Resolve(Path.GetFullPath(path), depth: 0);

    /// <summary>A substituted target is resolved again rather than accepted as written: the target is an
    /// ordinary path and may itself sit under links, so accepting it leaves the very prefix this exists to
    /// resolve unresolved. A Mac shows it without any exotic setup — a link into <c>/var/...</c> lands back
    /// on the symlink into <c>/private</c>.</summary>
    static string Resolve(string full, int depth) {
        if (depth > 20) return full;   // a cycle ResolveLinkTarget did not refuse

        var root    = Path.GetPathRoot(full)!;
        var current = root;

        foreach (var part in full[root.Length..]
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);

            // A cycle or a dangling link throws rather than resolving; the lexical component stands in,
            // and its own mode read then decides.
            try {
                if (Directory.ResolveLinkTarget(current, returnFinalTarget: true) is { } target)
                    current = Resolve(target.FullName, depth + 1);
            } catch (IOException) { /* keep the lexical component */ }
        }

        return current;
    }
}
