namespace Capacitor.Cli.Core.Setup;

/// Finds and removes `kcap` symlinks, in the given folders, that resolve to `target`. A regular
/// file, or a link to any other binary, is never touched.
public sealed class KcapLinkRemover(IReadOnlyList<string> folders, string target) {
    public IReadOnlyList<string> Find() {
        var resolvedTarget = Resolve(target);
        var found          = new List<string>();

        foreach (var folder in folders) {
            var path = Path.Combine(folder, "kcap");
            if (PointsAt(path, resolvedTarget)) found.Add(path);
        }

        return found;
    }

    public IReadOnlyList<KcapLinkFailure> Remove() {
        var failures       = new List<KcapLinkFailure>();
        var resolvedTarget = Resolve(target);

        foreach (var path in Find()) {
            try {
                // The link may have been repointed since Find.
                if (PointsAt(path, resolvedTarget)) File.Delete(path);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                failures.Add(new KcapLinkFailure(path, ex.Message));
            }
        }

        return failures;
    }

    static bool PointsAt(string path, string? resolvedTarget) {
        if (resolvedTarget is null) return false;

        try {
            var info = new FileInfo(path);

            return info.LinkTarget is not null
                && info.ResolveLinkTarget(true)?.FullName is { } resolved
                && string.Equals(resolved, resolvedTarget, StringComparison.Ordinal);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static string? Resolve(string path) {
        if (string.IsNullOrEmpty(path)) return null;

        try {
            var info = new FileInfo(path);

            return info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(true)?.FullName;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }
}
