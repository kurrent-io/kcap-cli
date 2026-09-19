namespace Capacitor.Cli.Core;

/// <summary>A realpath-style walk: every component is resolved, not only the leaf, because an
/// ancestor symlink is how a path outside a boundary textually matches one inside it.</summary>
public static class CanonicalPath {
    // Charged per symlink traversal only. An ordinary component costs nothing, so no path is deep
    // enough to exhaust the budget before the walk reaches the link that matters. 40 is the common
    // OS-level MAXSYMLINKS limit, and running out is also how a cycle terminates.
    const int MaxSymlinkHops = 40;

    /// <summary>Best effort: a path whose links could not all be followed comes back with its
    /// unresolved remainder stitched on. For grouping and comparing intents, where a partial answer
    /// is harmless — a containment decision takes <see cref="TryResolve"/> instead, because an
    /// unresolved remainder is a name rather than a location.</summary>
    public static string Resolve(string path) {
        TryResolve(path, out var resolved);

        return resolved;
    }

    /// <summary>The fully resolved path, or false with the best-effort one: the walk ran out of
    /// symlink traversals, which is a chain too long to follow or a cycle.</summary>
    public static bool TryResolve(string path, out string resolved) =>
        RealPath(Path.GetFullPath(path), out resolved);

    /// <summary>Whether <paramref name="candidate"/> resolves to <paramref name="boundary"/> itself
    /// or to something beneath it. A path that could not be fully resolved — either side — is within
    /// nothing.</summary>
    public static bool IsWithin(string candidate, string boundary) {
        if (!TryResolve(boundary, out var resolvedBoundary)) return false;
        if (!TryResolve(candidate, out var resolved)) return false;
        if (PathComparison.Equal(resolved, resolvedBoundary)) return true;
        var prefix = resolvedBoundary.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedBoundary
            : resolvedBoundary + Path.DirectorySeparatorChar;
        return resolved.StartsWith(prefix, PathComparison.Comparison);
    }

    /// <summary>
    /// Walks the path root-first, resolving each accumulated prefix a single hop at a time: when a
    /// prefix is a symlink, its target replaces the prefix (an absolute target restarts from its own
    /// root; a relative one resolves against the already-canonical parent) and resolution continues
    /// against it, so symlink chains and ancestor symlinks are all followed.
    /// </summary>
    static bool RealPath(string fullPath, out string resolved) {
        var root     = Path.GetPathRoot(fullPath) ?? "";
        var segments = SplitSegments(fullPath[root.Length..]);
        var walked   = root;
        var hops     = 0;

        while (segments.Count > 0) {
            var next = Path.Combine(walked, segments.Dequeue());

            // A component that doesn't exist yet (a materialization destination, say) can't be a
            // symlink either; ResolveLinkTarget throws for it instead of returning null.
            FileSystemInfo? target;
            try {
                target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: false);
            } catch (IOException) {
                target = null;
            }

            if (target is null) {
                walked = next; // real directory component
                continue;
            }

            if (++hops > MaxSymlinkHops) {
                resolved = Path.GetFullPath(Path.Combine([next, .. segments]));
                return false;
            }

            // One symlink hop. Resolve a relative target against the link's (canonical) parent.
            var linkPath = target.FullName;

            if (!Path.IsPathRooted(linkPath)) {
                linkPath = Path.GetFullPath(Path.Combine(walked, linkPath));
            }

            var linkRoot = Path.GetPathRoot(linkPath) ?? "";

            // Re-queue the target's own segments (so ancestor symlinks inside it get resolved too)
            // ahead of the segments we hadn't reached yet.
            segments = new Queue<string>(SplitSegments(linkPath[linkRoot.Length..]).Concat(segments));
            walked   = linkRoot;
        }

        resolved = walked;
        return true;
    }

    static Queue<string> SplitSegments(string pathRemainder) => new(
        pathRemainder
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0)
    );
}
