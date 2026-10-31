namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure scope filter for the import pipeline. The repo resolver is injected
/// so unit tests can stub out repository detection; production wires it to
/// the cwd-extractor + RepositoryDetection.DetectRepositoryAsync.
/// </summary>
public static class ImportScopeFilter {
    /// <param name="folderOf">The folder a session ran in, as discovery reports it, or null when unknown.
    /// Only a scope or skip that names folders reads it.</param>
    /// <param name="skip">Left out after the scope matched, so it narrows every scope alike.</param>
    public static async Task<List<(string SessionId, string FilePath, string EncodedCwd)>> Apply(
        IReadOnlyList<(string SessionId, string FilePath, string EncodedCwd)>                       transcripts,
        ImportScope                                                                                  scope,
        Func<(string SessionId, string FilePath, string EncodedCwd), CancellationToken,
             ValueTask<(string? Owner, string? Name)>>                                               resolveRepo,
        CancellationToken                                                                            ct       = default,
        Func<string, string?>?                                                                       folderOf = null,
        ImportSkip?                                                                                  skip     = null) {
        var skipping = skip is { IsEmpty: false };

        if (scope is ImportScope.All && !skipping) return [..transcripts];

        // Hoisted so the sets are built once rather than per session, and keyed on the tuple so no
        // string is allocated inside the loop.
        var wanted = scope is ImportScope.Repo r
            ? new HashSet<(string Owner, string Name)>(r.Repos, ImportScope.RepoComparer)
            : null;
        var wantedFolders = scope is ImportScope.Repo { Folders.Count: > 0 } withFolders
            ? new HashSet<string>(withFolders.Folders, StringComparer.Ordinal)
            : null;

        var kept = new List<(string, string, string)>(transcripts.Count);

        foreach (var t in transcripts) {
            ct.ThrowIfCancellationRequested();
            var (owner, name) = await resolveRepo(t, ct);
            var folder        = wantedFolders is not null || skipping ? folderOf?.Invoke(t.SessionId) : null;

            var match = (scope, owner, name) switch {
                (ImportScope.All, _, _)               => true,
                (ImportScope.Org o, not null, _)      => string.Equals(owner, o.OrgLogin, StringComparison.OrdinalIgnoreCase),
                (ImportScope.Repo, not null, not null) when wanted is not null && wanted.Contains((owner, name)) => true,
                (ImportScope.Repo, _, _)              => folder is not null && wantedFolders?.Contains(ImportScope.FolderKey(folder)) == true,
                _                                     => false,
            };

            if (match && skipping && skip!.Covers((owner, name), folder)) match = false;

            if (match) kept.Add(t);
        }

        return kept;
    }
}
