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

    /// <summary>
    /// Replays the transcript written so far. A watcher resumed mid-session only drains lines
    /// past the server's frontier, so a checkout mutated before the restart would otherwise
    /// never be probed.
    /// </summary>
    public void SeedFromTranscript(string vendor, string transcriptPath) {
        try {
            foreach (var line in File.ReadLinesShared(transcriptPath)) {
                OnLine(vendor, line);
                if (_roots.Count >= capacity) return;
            }
        } catch {
            // fail-open: seeding is best-effort and must never block startup
        }
    }

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
