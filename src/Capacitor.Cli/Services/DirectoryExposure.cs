namespace Capacitor.Cli.Services;

/// <summary>Which directories on the path to a service unit grant write to accounts other than the owner.
///
/// <para>A unit written <c>0600</c> into a directory nobody else can write is still replaceable if
/// something ABOVE it is: write permission on a directory is permission to rename its entries, so an
/// account that can write <c>~/.config</c> can swap the whole <c>systemd/user</c> directory for one of its
/// own. The mode of the unit and of its immediate directory says nothing about that.</para></summary>
static class DirectoryExposure {
    /// <summary>The directories from <paramref name="directory"/> up to the filesystem root that grant any
    /// of <paramref name="bits"/>, nearest first. Empty means nothing on the path does.
    ///
    /// <para>A sticky directory never counts, whichever bits it grants: the sticky bit restricts rename and
    /// unlink to an entry's own owner, which is exactly what makes <c>/tmp</c> at <c>1777</c> safe to hold
    /// files nobody else may replace. Without this exemption every temp-rooted path would be reported.</para>
    ///
    /// <para>An ancestor whose mode cannot be read is skipped rather than assumed hostile — it is a
    /// directory this process cannot reach past either, so the write it guards will fail on its own.</para></summary>
    public static IReadOnlyList<string> GrantingWrite(string directory, UnixFileMode bits) {
        if (OperatingSystem.IsWindows()) return [];

        var found = new List<string>();

        for (var d = Path.GetFullPath(directory); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d)!) {
            UnixFileMode mode;

            try { mode = File.GetUnixFileMode(d); } catch (Exception) { continue; }

            if ((mode & bits) != 0 && !mode.HasFlag(UnixFileMode.StickyBit)) found.Add(d);
        }

        return found;
    }
}
