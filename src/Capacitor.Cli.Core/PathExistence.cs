namespace Capacitor.Cli.Core;

/// <summary>What could be established about a path. <see cref="Indeterminate"/> is the answer
/// whenever the filesystem refused to say — which is neither presence nor absence, and is what every
/// caller has to fail closed on.</summary>
public enum PathPresence {
    Missing,
    Present,
    Indeterminate,
}

/// <summary>
/// The one place existence is decided. <c>File.Exists</c> and <c>Directory.Exists</c> answer false
/// for a path that is there but could not be reached — a directory without search permission answers
/// "not found" for everything inside it — so a caller that reads their false as absence deletes,
/// discharges or forgets on the strength of a refusal to answer.
/// </summary>
public static class PathExistence {
    public static PathPresence OfFile(string path) => Decide(new FileInfo(path).Exists, path);

    public static PathPresence OfDirectory(string path) => Decide(Directory.Exists(path), path);

    static PathPresence Decide(bool found, string path) {
        if (found) return PathPresence.Present;

        var full   = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        var leaf   = Path.GetFileName(full);

        // A volume root that did not answer is not something to reason about further.
        if (parent is null || leaf.Length == 0) return PathPresence.Indeterminate;

        try {
            // Listed, and yet nothing about it could be answered: the entry is there and this is
            // not absence. Names are compared rather than matched, because a pattern would read a
            // wildcard in the leaf as a search.
            return Directory.EnumerateFileSystemEntries(parent)
                    .Any(entry => PathComparison.Equal(Path.GetFileName(entry), leaf))
                ? PathPresence.Indeterminate
                : PathPresence.Missing;
        } catch (DirectoryNotFoundException) {
            return PathPresence.Missing;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return PathPresence.Indeterminate;
        }
    }
}
