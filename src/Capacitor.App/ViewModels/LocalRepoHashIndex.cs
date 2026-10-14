using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.App.ViewModels;

public sealed class LocalRepoHashIndex(Func<string, string?>? readOriginUrl = null) {
    readonly Func<string, string?> _readOriginUrl = readOriginUrl
        ?? (path => GitRemoteReader.ReadOriginUrl(GitRepository.ResolveMainRepoRoot(path)));
    readonly Dictionary<string, (string Owner, string Repo, string RepoHash)?> _cache = new(StringComparer.Ordinal);
    readonly Lock _lock = new();

    public (string Owner, string Repo, string RepoHash)? Inspect(string? repoPath) {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;

        lock (_lock) {
            if (_cache.TryGetValue(repoPath, out var cached)) return cached;
            var resolved = ResolveCore(repoPath);
            _cache[repoPath] = resolved;
            return resolved;
        }
    }

    public string? TryGetRepoHash(string? repoPath) => Inspect(repoPath)?.RepoHash;

    public string? TryResolveByRepoHash(string? repoHash, IEnumerable<string> availableRepos) {
        if (string.IsNullOrWhiteSpace(repoHash)) return null;

        foreach (var path in availableRepos) {
            if (Inspect(path) is { } info && string.Equals(info.RepoHash, repoHash, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    public string? TryResolveByOwnerRepo(string? owner, string? repo, IEnumerable<string> availableRepos) {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return null;

        var targetHash = RepoHashHelper.ComputeRepoHash(owner, repo);
        if (TryResolveByRepoHash(targetHash, availableRepos) is { } byHash) return byHash;

        foreach (var path in availableRepos) {
            if (Inspect(path) is { } info
                && string.Equals(info.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(info.Repo, repo, StringComparison.OrdinalIgnoreCase)) {
                return path;
            }
        }

        return null;
    }

    public string? TryResolvePastedLink(
            PastedLinkMatch match,
            IEnumerable<string> availableRepos,
            IReadOnlyDictionary<string, string>? issueKeyRepoHashes = null) {
        var repos = availableRepos as IReadOnlyList<string> ?? availableRepos.ToList();

        if (!string.IsNullOrWhiteSpace(match.RepoHash) && TryResolveByRepoHash(match.RepoHash, repos) is { } byHash)
            return byHash;

        if (!string.IsNullOrWhiteSpace(match.Owner) && !string.IsNullOrWhiteSpace(match.Repo)
            && TryResolveByOwnerRepo(match.Owner, match.Repo, repos) is { } byOwnerRepo)
            return byOwnerRepo;

        if (match.Kind == PastedLinkKind.LinearIssue) {
            if (!string.IsNullOrWhiteSpace(match.IssueKey)
                && issueKeyRepoHashes is not null
                && issueKeyRepoHashes.TryGetValue(match.IssueKey, out var mappedHash)
                && TryResolveByRepoHash(mappedHash, repos) is { } byMappedIssue) {
                return byMappedIssue;
            }

            if (!string.IsNullOrWhiteSpace(match.Slug)) {
                foreach (var path in repos) {
                    var leaf = RepoLabel.Leaf(path);
                    if (leaf.Length >= 3 && match.Slug.Contains(leaf, StringComparison.OrdinalIgnoreCase))
                        return path;
                }
            }
        }

        return null;
    }

    (string Owner, string Repo, string RepoHash)? ResolveCore(string repoPath) {
        var url = _readOriginUrl(repoPath);
        if (string.IsNullOrWhiteSpace(url)) return null;

        var normalized = RemoteMatcher.NormalizeRemoteUrl(url);
        var ownerRepo  = normalized is null ? null : RemoteMatcher.PathAfterHost(normalized);
        if (string.IsNullOrWhiteSpace(ownerRepo)) return null;

        var lastSlash = ownerRepo.LastIndexOf('/');
        if (lastSlash <= 0 || lastSlash == ownerRepo.Length - 1) return null;

        var owner = ownerRepo[..lastSlash];
        var repo  = ownerRepo[(lastSlash + 1)..];
        var hash  = RepoHashHelper.ComputeRepoHash(owner, repo);
        return (owner, repo, hash);
    }
}
