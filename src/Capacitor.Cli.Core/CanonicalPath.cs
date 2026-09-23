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
    // A rooted path is walked exactly as given: GetFullPath would fold its `..` lexically, before
    // the component ahead of it has been resolved, which is the escape this walk exists to refuse.
    // A relative one has no base but the process's own, so it is rooted the only way there is.
    public static bool TryResolve(string path, out string resolved) =>
        RealPath(Path.IsPathRooted(path) ? path : Path.GetFullPath(path), out resolved);

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
    /// Walks the path root-first, one component at a time: a component that is a symlink has its
    /// target spliced in front of whatever is left to walk (an absolute target restarts from its own
    /// root; a relative one continues from the link's already-resolved parent), so symlink chains
    /// and ancestor symlinks are all followed.
    ///
    /// <para>Nothing is normalized ahead of the walk. <c>..</c> is a step the walk takes, against
    /// what the components before it were found to point at — folding it lexically instead is how a
    /// target leading out of a boundary comes back reading as inside it.</para>
    /// </summary>
    static bool RealPath(string fullPath, out string resolved) {
        var root     = Path.GetPathRoot(fullPath) ?? "";
        var segments = SplitSegments(fullPath[root.Length..]);
        var walked   = root;
        var hops     = 0;

        while (segments.Count > 0) {
            var segment = segments.Dequeue();

            if (segment == ".") continue;
            if (segment == "..") { walked = Parent(walked, root); continue; }

            var next = Path.Combine(walked, segment);

            // The raw target as stored, never a normalized rendering of it: a `..` the target itself
            // contains belongs to this walk, not to string arithmetic. A component that doesn't
            // exist yet — a materialization destination, say — is not a link either.
            string? linkTarget;
            try {
                linkTarget = new DirectoryInfo(next).LinkTarget;
            } catch (IOException) {
                linkTarget = null;
            }

            if (linkTarget is null) {
                walked = next; // real directory component
                continue;
            }

            if (++hops > MaxSymlinkHops) {
                resolved = Path.Combine([next, .. segments]);
                return false;
            }

            var targetRoot = Path.GetPathRoot(linkTarget) ?? "";

            segments = new Queue<string>(
                SplitSegments(linkTarget[targetRoot.Length..]).Concat(segments));
            // An absolute target restarts from its own root; a relative one continues from the
            // link's parent, which is what `walked` already holds.
            if (targetRoot.Length > 0) walked = targetRoot;
        }

        resolved = walked;
        return true;
    }

    /// <summary>One step up, stopping at the root: a path cannot climb out of its own volume.
    /// </summary>
    static string Parent(string walked, string root) =>
        walked.Length <= root.Length ? root : Path.GetDirectoryName(walked) ?? root;

    static Queue<string> SplitSegments(string pathRemainder) => new(
        pathRemainder
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0)
    );
}
