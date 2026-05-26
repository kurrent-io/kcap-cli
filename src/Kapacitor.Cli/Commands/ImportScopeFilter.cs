namespace Kapacitor.Cli.Commands;

/// <summary>
/// Pure scope filter for the import pipeline. The repo resolver is injected
/// so unit tests can stub out repository detection; production wires it to
/// the cwd-extractor + RepositoryDetection.DetectRepositoryAsync.
/// </summary>
public static class ImportScopeFilter {
    public static async Task<List<(string SessionId, string FilePath, string EncodedCwd)>> Apply(
        IReadOnlyList<(string SessionId, string FilePath, string EncodedCwd)>                       transcripts,
        ImportScope                                                                                  scope,
        Func<(string SessionId, string FilePath, string EncodedCwd), CancellationToken,
             ValueTask<(string? Owner, string? Name)>>                                               resolveRepo,
        CancellationToken                                                                            ct = default) {
        if (scope is ImportScope.All) return [..transcripts];

        var kept = new List<(string, string, string)>(transcripts.Count);

        foreach (var t in transcripts) {
            ct.ThrowIfCancellationRequested();
            var (owner, name) = await resolveRepo(t, ct);

            var match = (scope, owner, name) switch {
                (ImportScope.Org o,  not null, _) => string.Equals(owner, o.OrgLogin, StringComparison.OrdinalIgnoreCase),
                (ImportScope.Repo r, not null, not null) =>
                    string.Equals(owner, r.Owner, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(name,  r.Name,  StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (match) kept.Add(t);
        }

        return kept;
    }
}
