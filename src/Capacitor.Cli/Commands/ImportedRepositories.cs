using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>The local repositories an import's captured sessions ran in, for the known-repos list the
/// launcher and the daemon offer.</summary>
static class ImportedRepositories {
    /// <summary>A cwd that is gone is skipped rather than walked up: its nearest surviving ancestor
    /// repository is not where the session ran.</summary>
    public static List<RepoEntry> Collect(
            IEnumerable<ImportCommand.SessionClassification> captured,
            IReadOnlyDictionary<string, string>              sessionCwds
        ) {
        var roots   = new Dictionary<string, string?>(StringComparer.Ordinal);
        var entries = new List<RepoEntry>();

        foreach (var c in captured) {
            if (!sessionCwds.TryGetValue(c.SessionId, out var cwd)) continue;

            if (!roots.TryGetValue(cwd, out var root)) {
                root       = Directory.Exists(cwd) ? GitRepository.FindRoot(cwd) : null;
                roots[cwd] = root;
            }

            if (root is not null) entries.Add(new RepoEntry { Path = root, LastUsed = ImportCommand.ChainTimestamp(c) });
        }

        return entries;
    }
}
