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
    /// True when the session's repo should not be captured. The allowlist gates and the denylist
    /// subtracts within it. Resolves the repo once, so configuring both costs one detection.
    ///
    /// <para>A repo that cannot be resolved — detection failed, the budget ran out, or the session
    /// is not in a git repo at all — is outside a configured allowlist. Callers persist that
    /// verdict like any other: the repo gate runs at session start and later events read the
    /// marker, so a verdict that is not recorded is a session whose later events go uninspected.
    /// The cost is that a transient failure drops the session rather than an event, which is the
    /// direction to fail in when the alternative is uploading what the list excludes.</para>
    /// </summary>
    public static async Task<bool> IsOutOfScopeAsync(
            GitProviderRouter router, ConfigRoot config, string body,
            string[]? allowedRepos, string[]? excludedRepos, TimeProvider time, TimeSpan? budget = null) {
        var hasAllowlist = allowedRepos  is { Length: > 0 };
        var hasDenylist  = excludedRepos is { Length: > 0 };

        if (!hasAllowlist && !hasDenylist) return false;

        var key = await ResolveKeyAsync(router, config, body, time, budget);

        if (key is null) return hasAllowlist;

        if (hasAllowlist && !allowedRepos!.Contains(key, StringComparer.OrdinalIgnoreCase)) return true;

        return hasDenylist && excludedRepos!.Contains(key, StringComparer.OrdinalIgnoreCase);
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
            GitProviderRouter router, ConfigRoot config, string body, TimeProvider time, TimeSpan? budget) {
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
            var repo = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time, budget, detectPullRequest: false);

            if (repo?.Owner is not null && repo.RepoName is not null) return $"{repo.Owner}/{repo.RepoName}";
        } catch {
            // Best effort — an unresolvable repo is the caller's to interpret.
        }

        return null;
    }
}
