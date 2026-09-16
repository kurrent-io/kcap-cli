using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

/// <summary>
/// Repo-keyed session scoping. Matches a session's <c>owner/repo</c> against the profile's allow
/// and deny lists, preferring the payload's repository block and falling back to detecting the
/// repo from the cwd.
/// </summary>
static class RepoExclusion {
    /// <summary>
    /// Whether the session's repo is out of the profile's capture scope, and whether that verdict
    /// rests on a repo we actually identified.
    /// </summary>
    /// <param name="OutOfScope">True when the session should not be captured.</param>
    /// <param name="Resolved">
    /// False when the repo could not be identified at all. A caller that persists the verdict — a
    /// <c>DisabledSessions</c> marker, say — must not persist an unresolved one: a one-off git
    /// failure or an exhausted budget would otherwise cost the whole session rather than the event,
    /// in a repo that is on the allow list.
    /// </param>
    public readonly record struct RepoScopeVerdict(bool OutOfScope, bool Resolved);

    /// <summary>
    /// True when the session's repo should not be captured. The allowlist gates and the denylist
    /// subtracts within it. Resolves the repo once, so configuring both costs one detection.
    /// </summary>
    public static async Task<RepoScopeVerdict> IsOutOfScopeAsync(
            GitProviderRouter router, ConfigRoot config, string body,
            string[]? allowedRepos, string[]? excludedRepos, TimeSpan? budget = null) {
        var hasAllowlist = allowedRepos  is { Length: > 0 };
        var hasDenylist  = excludedRepos is { Length: > 0 };

        if (!hasAllowlist && !hasDenylist) return new(false, Resolved: true);

        var key = await ResolveKeyAsync(router, config, body, budget);

        // Nothing to match on — detection failed, the budget ran out, or the session is not in a
        // repo at all. A denylist keeps capturing; an allowlist has nothing to admit it by, and one
        // that admitted what it could not identify would not be restricting anything. The
        // consequence is worth stating: while allowed_repos is set, work outside any repo is never
        // captured.
        if (key is null) return new(hasAllowlist, Resolved: false);

        if (hasAllowlist && !allowedRepos!.Contains(key, StringComparer.OrdinalIgnoreCase))
            return new(true, Resolved: true);

        return new(hasDenylist && excludedRepos!.Contains(key, StringComparer.OrdinalIgnoreCase), Resolved: true);
    }

    /// <summary>
    /// Allowlist check for a repo key the caller already resolved — the import path resolves one
    /// per unique cwd and reuses it. A key it could not resolve is outside a configured list, the
    /// same call as <see cref="IsOutOfScopeAsync"/> makes.
    /// </summary>
    public static bool IsOutsideAllowlist(string? repoKey, IReadOnlyList<string>? allowedRepos)
        => allowedRepos is { Count: > 0 }
        && (repoKey is null || !allowedRepos.Contains(repoKey, StringComparer.OrdinalIgnoreCase));

    /// <summary><c>owner/repo</c> for the session, or null when it cannot be determined.</summary>
    static async Task<string?> ResolveKeyAsync(
            GitProviderRouter router, ConfigRoot config, string body, TimeSpan? budget) {
        try {
            var payload = JsonNode.Parse(body);

            if (payload is null) return null;

            // Try to get repo from the payload's repository field first
            var owner    = payload["repository"]?["owner"]?.GetValue<string>();
            var repoName = payload["repository"]?["repo_name"]?.GetValue<string>();

            if (owner is not null && repoName is not null) return $"{owner}/{repoName}";

            // Fall back to detecting repo from cwd
            var cwd = payload["cwd"]?.GetValue<string>();

            if (cwd is null) return null;

            // Matching is on owner/repo only → skip the PR round-trip (~600ms to GitHub).
            var repo = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, budget, detectPullRequest: false);

            if (repo?.Owner is not null && repo.RepoName is not null) return $"{repo.Owner}/{repo.RepoName}";
        } catch {
            // Best effort — an unresolvable repo is the caller's to interpret.
        }

        return null;
    }
}
