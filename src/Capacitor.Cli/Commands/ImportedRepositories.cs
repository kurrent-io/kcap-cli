using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>The local repositories an import's captured sessions ran in, for the known-repos list the
/// launcher and the daemon offer.</summary>
static class ImportedRepositories {
    /// <summary>One entry per repository, at its latest session activity. A cwd that is gone is skipped
    /// rather than walked up: its nearest surviving ancestor repository is not where the session ran.</summary>
    public static List<RepoEntry> Collect(
            IEnumerable<ImportCommand.SessionClassification> captured,
            IReadOnlyDictionary<string, string>              sessionCwds
        ) {
        var roots  = new Dictionary<string, string?>(StringComparer.Ordinal);
        var latest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var c in captured) {
            if (!sessionCwds.TryGetValue(c.SessionId, out var cwd)) continue;

            if (!roots.TryGetValue(cwd, out var root)) {
                root       = Directory.Exists(cwd) ? GitRepository.FindRoot(cwd) : null;
                roots[cwd] = root;
            }

            if (root is null) continue;

            var at = LastActivity(c);
            if (!latest.TryGetValue(root, out var seen) || at > seen) latest[root] = at;
        }

        return [.. latest.Select(kv => new RepoEntry { Path = kv.Key, LastUsed = kv.Value })];
    }

    /// <summary>The transcript's last write, else its first message; <see cref="DateTimeOffset.MinValue"/>
    /// when neither is known, so the repository sorts last rather than at an invented time.</summary>
    static DateTimeOffset LastActivity(ImportCommand.SessionClassification c) {
        try {
            if (c.FilePath.Length > 0 && File.Exists(c.FilePath))
                return new DateTimeOffset(File.GetLastWriteTimeUtc(c.FilePath), TimeSpan.Zero);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Fall through to the transcript's own timestamp.
        }

        return c.Meta.FirstTimestamp ?? DateTimeOffset.MinValue;
    }
}
