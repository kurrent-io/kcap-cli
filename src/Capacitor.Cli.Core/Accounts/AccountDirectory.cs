namespace Capacitor.Cli.Core.Accounts;

public static class AccountDirectory {
    const int MaxLinkHops = 40;

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Physical(path));

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The path with every symlinked component replaced by its target, like <c>realpath</c>. A vendor
    /// reports paths through whichever spelling its config named, so resolving only the last component
    /// leaves a linked ancestor unequal to its target. A component that cannot be resolved (missing,
    /// dangling, cyclic, unreadable) stands as written; only a path <see cref="Path.GetFullPath(string)"/>
    /// rejects throws.
    /// </summary>
    public static string Physical(string path) {
        var full = Path.GetFullPath(path);

        try {
            var hops = 0;

            return Resolve(full, ref hops);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return full;
        }
    }

    static string Resolve(string full, ref int hops) {
        var root    = Path.GetPathRoot(full) ?? "";
        var current = root;

        foreach (var part in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)) {
            current = Path.Combine(current, part);

            if (hops >= MaxLinkHops) continue;

            FileSystemInfo? target;

            try {
                var info = new FileInfo(current);
                target = info.LinkTarget is null ? null : info.ResolveLinkTarget(returnFinalTarget: true);
            } catch (IOException) {
                continue;
            }

            if (target is null) continue;

            hops++;
            current = Resolve(Path.GetFullPath(target.FullName), ref hops);
        }

        return current;
    }
}
