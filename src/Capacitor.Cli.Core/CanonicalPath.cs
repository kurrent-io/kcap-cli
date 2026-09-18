namespace Capacitor.Cli.Core;

/// <summary>A realpath-style walk: every component is resolved, not only the leaf, because an
/// ancestor symlink is how a path outside a boundary textually matches one inside it.</summary>
public static class CanonicalPath {
    // A symlink cycle can't loop forever: cap the components this walk resolves and return the
    // best-resolved path on hitting the cap. 40 is the common OS-level MAXSYMLINKS limit, but the
    // counter charges a plain component too, so a path of more than 40 segments also comes back
    // partly unresolved.
    const int MaxResolveSteps = 40;

    public static string Resolve(string path) => RealPath(Path.GetFullPath(path));

    /// <summary>Whether <paramref name="candidate"/> resolves to <paramref name="boundary"/> itself
    /// or to something beneath it.</summary>
    public static bool IsWithin(string candidate, string boundary) {
        var resolvedBoundary = Resolve(boundary);
        var resolved         = Resolve(candidate);
        if (string.Equals(resolved, resolvedBoundary, StringComparison.Ordinal)) return true;
        var prefix = resolvedBoundary.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedBoundary
            : resolvedBoundary + Path.DirectorySeparatorChar;
        return resolved.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks the path root-first, resolving each accumulated prefix a single hop at a time: when a
    /// prefix is a symlink, its target replaces the prefix (an absolute target restarts from its own
    /// root; a relative one resolves against the already-canonical parent) and resolution continues
    /// against it, so symlink chains and ancestor symlinks are all followed. Bounded by
    /// <see cref="MaxResolveSteps"/> so a symlink cycle terminates at the best-resolved path.
    /// </summary>
    static string RealPath(string fullPath) {
        var root      = Path.GetPathRoot(fullPath) ?? "";
        var segments  = SplitSegments(fullPath[root.Length..]);
        var resolved  = root;
        var steps     = 0;

        while (segments.Count > 0) {
            if (steps++ >= MaxResolveSteps) {
                // Cycle guard: stitch the unresolved remainder back on and stop.
                return Path.GetFullPath(Path.Combine([resolved, ..segments]));
            }

            var next = Path.Combine(resolved, segments.Dequeue());

            // A component that doesn't exist yet (a materialization destination, say) can't be a
            // symlink either; ResolveLinkTarget throws for it instead of returning null.
            FileSystemInfo? target;
            try {
                target = new DirectoryInfo(next).ResolveLinkTarget(returnFinalTarget: false);
            } catch (IOException) {
                target = null;
            }

            if (target is null) {
                resolved = next; // real directory component
                continue;
            }

            // One symlink hop. Resolve a relative target against the link's (canonical) parent.
            var linkPath = target.FullName;

            if (!Path.IsPathRooted(linkPath)) {
                linkPath = Path.GetFullPath(Path.Combine(resolved, linkPath));
            }

            var linkRoot = Path.GetPathRoot(linkPath) ?? "";

            // Re-queue the target's own segments (so ancestor symlinks inside it get resolved too)
            // ahead of the segments we hadn't reached yet.
            segments = new Queue<string>(SplitSegments(linkPath[linkRoot.Length..]).Concat(segments));
            resolved = linkRoot;
        }

        return resolved;
    }

    static Queue<string> SplitSegments(string pathRemainder) => new(
        pathRemainder
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(s => s.Length > 0)
    );
}
