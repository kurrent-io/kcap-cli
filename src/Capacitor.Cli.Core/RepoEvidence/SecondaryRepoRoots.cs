namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>
/// Collects the roots of checkouts an agent mutated other than the one it was launched in, so a
/// PR opened from an agent-made worktree can be probed there. Only mutation paths count: a
/// checkout the agent merely read must never have whatever PR its branch carries attached to
/// the session. Fail-open on every parse or lookup error.
/// </summary>
public sealed class SecondaryRepoRoots(Func<string, string?> findRoot, string? primaryRoot, int capacity = 8) {
    readonly HashSet<string> _roots = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Roots => _roots;

    public void OnLine(string vendor, string jsonlLine) {
        if (vendor != "claude" || _roots.Count >= capacity) return;

        try {
            foreach (var (path, kind) in RepoEvidencePaths.ExtractClaudePaths(jsonlLine)) {
                if (kind != RepoEvidenceKind.Mutation) continue;

                var separator = path.AsSpan().LastIndexOfAny('/', '\\');
                if (separator < 0) continue;

                var root = findRoot(path[..separator]);
                if (root is null || root == primaryRoot) continue;

                _roots.Add(root);
                if (_roots.Count >= capacity) return;
            }
        } catch {
            // fail-open: a bad line must never break watching
        }
    }
}
