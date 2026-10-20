using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The repositories a daemon tells the server it can host, as concrete repository roots: a
/// consumer compares one path with another rather than re-deriving the allowlist's rules. A wildcard
/// allowlist entry (<c>…/*</c>) authorizes everything under its prefix, so it is advertised as the
/// repositories found there, never as the prefix itself, which is not a repository.</summary>
static class AdvertisedRepoPaths {
    /// <summary>How deep below a wildcard prefix repositories are looked for: <c>repos/name</c> and
    /// <c>repos/owner/name</c>. A repository found is not searched further.</summary>
    const int WildcardDepth = 2;

    /// <summary>A bound on one prefix's contribution, so a prefix over a large tree cannot swell
    /// every registration.</summary>
    const int WildcardLimit = 500;

    /// <summary>Persisted paths first, in their own order, then the allowlist's: a literal entry as
    /// written, since it authorizes exactly that path, and a wildcard entry as the repositories under it,
    /// each collapsed to its main repository like a persisted one.</summary>
    public static string[] Merge(IReadOnlyList<string> persisted, IReadOnlyList<string> allowed) {
        if (allowed.Count == 0) return [.. persisted];

        var comparer = RepoPathStore.PathComparison == StringComparison.Ordinal ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(persisted, comparer);
        var merged = new List<string>(persisted);

        foreach (var entry in allowed) {
            if (entry.Replace('\\', '/').EndsWith("/*", StringComparison.Ordinal))
                merged.AddRange(RepositoriesUnder(entry[..^2]).Where(seen.Add));
            else if (entry.TrimEnd('/', '\\') is { Length: > 0 } literal && seen.Add(literal))
                merged.Add(literal);
        }

        return [.. merged];
    }

    /// <summary>The prefix itself when it is a repository, else the repositories below it, in name order.</summary>
    internal static IEnumerable<string> RepositoriesUnder(string prefix) {
        var root = prefix.TrimEnd('/', '\\');
        if (root.Length == 0 || !Directory.Exists(root)) return [];
        if (IsRepository(root)) return [GitRepository.ResolveMainRepoRoot(root)];

        var found = new List<string>();
        Walk(root, 1);
        return found.Distinct(StringComparer.Ordinal);

        void Walk(string directory, int depth) {
            foreach (var child in Children(directory)) {
                if (found.Count >= WildcardLimit) return;
                if (IsRepository(child)) found.Add(GitRepository.ResolveMainRepoRoot(child));
                else if (depth < WildcardDepth) Walk(child, depth + 1);
            }
        }
    }

    static IEnumerable<string> Children(string directory) {
        try {
            return Directory.EnumerateDirectories(directory)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Order(StringComparer.Ordinal)
                .ToList();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    static bool IsRepository(string directory) {
        var dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }
}
