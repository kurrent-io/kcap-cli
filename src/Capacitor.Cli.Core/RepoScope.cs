namespace Capacitor.Cli.Core;

/// <summary>
/// Repo-keyed scoping over an <c>owner/repo</c> key the caller already resolved. The allowlist
/// gates and the denylist subtracts within it, so <c>allowed_repos: [acme/widgets]</c> with
/// <c>excluded_repos: [acme/secrets]</c> captures the first and not the second.
/// <para>Split from the resolution that feeds it so a caller holding a key — the import gate
/// resolving one per cwd, the daemon reading one off a checkout — decides against the same rules
/// a hook does, rather than a second copy of them.</para>
/// </summary>
public static class RepoScope {
    /// <summary>
    /// True when a session in this repo should not be captured. A null key — detection failed,
    /// or the checkout is not in a repo at all — is outside a configured allowlist and inside
    /// every denylist, which is the direction to fail in when the alternative is uploading what
    /// the list excludes.
    /// </summary>
    public static bool IsOutOfScope(
            string? repoKey, IReadOnlyList<string>? allowedRepos, IReadOnlyList<string>? excludedRepos) {
        if (IsOutsideAllowlist(repoKey, allowedRepos)) return true;

        return repoKey is not null
            && excludedRepos is { Count: > 0 }
            && excludedRepos.Contains(repoKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="allowedRepos"/> is non-empty and <paramref name="repoKey"/> is
    /// not one of them. An empty list admits everything, which is what keeps every config
    /// without the key capturing as before.
    /// </summary>
    public static bool IsOutsideAllowlist(string? repoKey, IReadOnlyList<string>? allowedRepos)
        => allowedRepos is { Count: > 0 }
        && (repoKey is null || !allowedRepos.Contains(repoKey, StringComparer.OrdinalIgnoreCase));
}
