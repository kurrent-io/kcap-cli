namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// Directory creation that survives a power loss. A mkdir is only in the parent's entries once the
/// parent is synced, so every level down to the target is synced, including levels that already
/// existed: an earlier run may have created them and crashed before syncing.
/// </summary>
public static class DurableDirectory {
    /// <summary>Creates the missing components of <paramref name="path"/> one at a time, then syncs the
    /// parent of every component strictly below <paramref name="anchor"/>. Created components get
    /// exactly <paramref name="mode"/>, whatever the umask.</summary>
    public static void Ensure(string path, string anchor, UnixFileMode? mode) {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root   = Path.TrimEndingDirectorySeparator(Path.GetFullPath(anchor));

        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(target);
            return;
        }

        if (!IsBelow(target, root))
            throw new ArgumentException($"{path} is not under {anchor}.", nameof(path));

        var components = new List<string>();

        for (var current = target; current != root; current = Path.GetDirectoryName(current)!)
            components.Add(current);

        components.Reverse();

        foreach (var component in components) {
            var existed = Directory.Exists(component);

            if (!existed) {
                if (mode is { } m) Directory.CreateDirectory(component, m);
                else Directory.CreateDirectory(component);

                // The create is filtered by the umask.
                if (mode is { } exact) File.SetUnixFileMode(component, exact);

                FaultPoints.Hit($"mkdir:{component}");
            }

            var parent = Path.GetDirectoryName(component)!;

            try {
                Sync(parent);
            } catch (DirectorySyncDeniedException) when (existed) {
                // An ancestor that predates this call and that the caller may not read cannot be
                // synced, and whoever created it was not this call.
            }
        }
    }

    public static void Sync(string directory) {
        FaultPoints.Hit($"sync:{directory}");
        DirectorySync.Flush(directory);
    }

    /// <summary>The home when <paramref name="path"/> is under it, else the filesystem root.</summary>
    public static string AnchorFor(string path, UserHome? home) {
        var full = Path.GetFullPath(path);

        if (home is not null) {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home.Path));
            if (IsBelow(full, root)) return root;
        }

        return Path.GetPathRoot(full)!;
    }

    static bool IsBelow(string path, string ancestor) {
        var prefix = Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, StringComparison.Ordinal);
    }
}
