using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The one place <c>kcap import</c> decides whether a session is inside the profile's capture
/// scope. Sources discover and classify; none of them holds an opinion about scope, so a source
/// cannot opt out of it by staying silent — which is the failure this shape exists to prevent.
///
/// <para>Runs over every source's classifications at once, so the repo cache is shared across
/// vendors: two harnesses with sessions in the same directory cost one detection, not two.</para>
/// </summary>
sealed class CaptureScope {
    readonly UserHome                               _home;
    readonly string[]?                              _allowedPaths;
    readonly string[]?                              _excludedPaths;
    readonly string[]?                              _allowedRepos;
    readonly string[]?                              _excludedRepos;
    readonly Func<string, Task<RepositoryPayload?>> _repoDetector;
    readonly ConcurrentDictionary<string, string?> _repoCache = new(StringComparer.Ordinal);

    public CaptureScope(
            GitProviderRouter router, ConfigRoot config, UserHome home,
            string[]? allowedPaths, string[]? excludedPaths,
            string[]? allowedRepos, string[]? excludedRepos,
            Func<string, Task<RepositoryPayload?>>? repoDetector = null) {
        _home          = home;
        _allowedPaths  = allowedPaths;
        _excludedPaths = excludedPaths;
        _allowedRepos  = allowedRepos;
        _excludedRepos = excludedRepos;
        _repoDetector  = repoDetector
                      ?? (cwd => RepositoryDetection.DetectRepositoryAsync(router, config, cwd, detectPullRequest: false));
    }

    /// <summary>Whether any list is set. Nothing to do when the profile scopes nothing.</summary>
    public bool Configured => NeedsRepo || NeedsPath;

    bool NeedsRepo => _allowedRepos is { Length: > 0 } || _excludedRepos is { Length: > 0 };
    bool NeedsPath => _allowedPaths is { Length: > 0 } || _excludedPaths is { Length: > 0 };

    /// <summary>
    /// Stamps the scope verdict onto the sessions this run could act on. Already-loaded counts:
    /// it still reaches <c>ImportSessionAsync</c> to re-assert its lifecycle hooks, so one outside
    /// the scope has to carry the verdict that keeps it out. A session that is too short, errored
    /// or internal reaches nothing, and stamping it would relabel it <c>Excluded</c> in the plan —
    /// hiding a probe failure behind a scope decision that never applied to it.
    /// </summary>
    public async Task<List<ImportCommand.SessionClassification>> ApplyAsync(
            IReadOnlyList<ImportCommand.SessionClassification> classifications) {
        if (NeedsRepo) await WarmRepoCacheAsync(classifications);

        return [.. classifications.Select(c => Actionable(c) ? Stamp(c) : c)];
    }

    /// <summary>
    /// The statuses that can still reach the server this run: <c>BuildImportChains</c> takes New and
    /// Partial, and the routed path additionally re-asserts AlreadyLoaded.
    /// </summary>
    internal static bool Actionable(ImportCommand.SessionClassification c) =>
        c.Status is ImportCommand.ClassificationStatus.New
                 or ImportCommand.ClassificationStatus.Partial
                 or ImportCommand.ClassificationStatus.AlreadyLoaded;

    /// <summary>
    /// Resolves every distinct cwd up front, bounded, so a few hundred workspaces are not a serial
    /// wall of <c>git</c> invocations between the probe bar clearing and the plan appearing. Each
    /// miss can spawn several processes under a 5s cap, and one unreachable path costs that alone.
    /// </summary>
    async Task WarmRepoCacheAsync(IReadOnlyList<ImportCommand.SessionClassification> classifications) {
        var cwds = classifications.Where(Actionable)
            .Select(CwdOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(cwd => !_repoCache.ContainsKey(cwd))
            .ToList();

        await Parallel.ForEachAsync(
            cwds,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (cwd, _) => _repoCache[cwd] = await DetectAsync(cwd));
    }

    ImportCommand.SessionClassification Stamp(ImportCommand.SessionClassification c) {
        var cwd     = CwdOf(c);
        var repoKey = NeedsRepo && cwd is not null && _repoCache.TryGetValue(cwd, out var k) ? k : null;

        string? excludedRepoKey = null;

        if (repoKey is not null && _excludedRepos is { Length: > 0 }
         && _excludedRepos.Contains(repoKey, StringComparer.OrdinalIgnoreCase)) {
            excludedRepoKey = repoKey;
        }

        string? excludedPathKey = null;

        if (cwd is not null && _excludedPaths is { Length: > 0 }) {
            foreach (var entry in _excludedPaths) {
                if (!PathExclusion.IsExcluded(cwd, [entry], _home)) continue;

                excludedPathKey = PathExclusion.Normalize(entry, _home);
                break;
            }
        }

        // A cwd or repo that could not be resolved is outside a configured allow list — the same
        // call the live hooks make, and the reason this runs even when cwd is null.
        var outsideAllowlist = PathExclusion.IsOutsideAllowlist(cwd, _allowedPaths, _home)
                            || RepoExclusion.IsOutsideAllowlist(repoKey, _allowedRepos);

        return c with {
            ExcludedRepoKey  = excludedRepoKey,
            ExcludedPathKey  = excludedPathKey,
            OutsideAllowlist = outsideAllowlist,
        };
    }

    /// <summary>
    /// The best cwd known for a session: the one its transcript reported, else the one decoded from
    /// the project directory name. Sources that learn the cwd at discovery put it on Meta; Claude
    /// and Codex only learn it while classifying, which is why scope is applied here and not at the
    /// discovery fan-out, where their cwd is still null.
    /// </summary>
    static string? CwdOf(ImportCommand.SessionClassification c) =>
        c.Meta.Cwd ?? (string.IsNullOrEmpty(c.EncodedCwd) ? null : SessionImporter.DecodeCwdFromDirName(c.EncodedCwd));

    async Task<string?> DetectAsync(string cwd) {
        try {
            var repo = await _repoDetector(cwd);

            if (repo is { Owner: { } owner, RepoName: { } repoName }) return $"{owner}/{repoName}";
        } catch {
            // Unresolvable, which the allow list reads as "not admitted" and the deny list ignores.
        }

        return null;
    }
}
