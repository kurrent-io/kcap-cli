using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// Finds this machine's checkout of a repository by its origin. The daemon re-checks the path on
/// the probe and on launch, so this choice is a convenience, never authority.
public sealed class LocalCheckoutFinder(RepoIdentityResolver identities, Func<string, string?>? mainRoot = null) {
    readonly Func<string, string?> _mainRoot = mainRoot ?? MainRootOf;

    /// Every checkout of the repository, the session's own first, then the candidates in order. Reads
    /// git metadata, so it runs on the thread pool.
    public Task<IReadOnlyList<string>> FindAllAsync(
            string owner, string name, string? sessionCwd, IEnumerable<string> candidates, CancellationToken ct) {
        var ordered = new List<string>();
        if (!string.IsNullOrEmpty(sessionCwd)) ordered.Add(sessionCwd);
        ordered.AddRange(candidates);
        return Task.Run(() => Find($"repo:{owner}/{name}".ToLowerInvariant(), ordered, ct), ct);
    }

    IReadOnlyList<string> Find(string key, IReadOnlyList<string> paths, CancellationToken ct) {
        var seen = new HashSet<string>(PlatformPaths.Comparer);
        var found = new List<string>();
        foreach (var path in paths) {
            ct.ThrowIfCancellationRequested();
            if (_mainRoot(path) is not { Length: > 0 } root) continue;
            var normalized = PlatformPaths.Normalize(root);
            if (!seen.Add(normalized)) continue;
            if (identities.ForLocalRoot(normalized).Key == key) found.Add(normalized);
        }
        return found;
    }

    static string? MainRootOf(string path) =>
        GitRepository.FindRoot(path) is { } root ? GitRepository.ResolveMainRepoRoot(root) : null;
}
