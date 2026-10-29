using System.Collections.Frozen;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Services;

/// Serves a session's PR links from its listing row when the row carried them, and asks
/// the server only for a session whose row did not: the server's per-user PR read
/// budget is then left to the reads only it can answer.
public sealed class ListedPullRequestSource : IPullRequestSource {
    readonly IPullRequestSource _server;
    volatile IReadOnlyDictionary<string, IReadOnlyList<RepoSessionPullRequestDto>> _listed =
        FrozenDictionary<string, IReadOnlyList<RepoSessionPullRequestDto>>.Empty;

    public ListedPullRequestSource(IPullRequestSource server, IAgentDirectory directory) {
        _server = server;
        // A withheld list (below full access) is not a listing of none: the server decides that session.
        // The subscription ends when the directory disposes its rows.
        directory.Rows.Connect().ToCollection().Subscribe(rows => _listed = rows
            .Where(r => r is { SessionId.Length: > 0, ListedPullRequests.Items: not null })
            .GroupBy(r => r.SessionId!, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First().ListedPullRequests!.Items!, StringComparer.Ordinal));
    }

    public Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        // A PR the row names without a URL has no provider or host to route a read to; only the server's list supplies them.
        if (!_listed.TryGetValue(sessionId, out var listed) || listed.Any(pr => PullRequestWire.SafeLink(pr.Url) is null))
            return _server.ListAsync(sessionId, ct);
        var items = listed.Select(ListedPullRequest.Link).Where(link => PullRequestWire.ValidSubject(PullRequestWire.Subject(link)))
            .DistinctBy(PullRequestWire.Subject).ToArray();
        return Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready, new() { Items = items }));
    }

    public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) => _server.DiscoverAsync(refresh, ct);
    public void ResetSession(string sessionId) => _server.ResetSession(sessionId);
    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => _server.LegacyLinksAsync(sessionId, ct);
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct)
        => _server.OverviewAsync(sessionId, subject, ct);
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class
        => _server.PageAsync<T>(sessionId, subject, section, cursor, resolved, threadId, ct);
}
