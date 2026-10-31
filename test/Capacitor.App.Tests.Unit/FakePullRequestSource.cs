using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

internal sealed class FakePullRequestSource(FakeTimeProvider time) : IPullRequestSource, IPullRequestReaders {
    public readonly List<(string Session, PullRequestRepository? Repository, string? Branch)> Descriptions = [];
    public void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch) => Descriptions.Add((sessionId, repository, branch));
    public PullRequestReaderNote? NoteFor(string provider, string host) => null;
    public string? PrLink(string? url, PullRequestSubjectDto subject) => PullRequestWire.PrLink(url, subject);
    public PullRequestLinkDto[] Links = [Link(1), Link(2)];
    public int AccessSeconds = 30;
    public int Lists;
    public TaskCompletionSource? ListGate;
    public int Resets;
    public string? ListFailure;
    public readonly Queue<PullRequestRead<PullRequestLinkListDto>> ListResponses = new();
    public int Overviews;
    public int Pages;
    public int TotalPages = 3;
    public bool EmptyPages;
    /// Held open, every page read waits on it: the section stays in its first load.
    public TaskCompletionSource? PageGate;
    public string HeadSha = new string('a', 40);
    /// The next page read answers Restart with this reason instead of a page.
    public string? RestartNextPage;
    public string? Failure;
    public string OverviewTitle = "Private PR";
    /// Lifecycle an overview reports per PR number; a number not listed reads as open.
    public readonly Dictionary<int, string> Lifecycles = [];
    public Func<PullRequestSubjectDto, string>? TitleFor;
    public Func<PullRequestSubjectDto, string>? RollupFor;
    public Func<string, object?>? PageItem;
    public Func<PullRequestSubjectDto, string, object?>? PageItemFor;
    public readonly Queue<Func<PullRequestSubjectDto, CancellationToken, Task<PullRequestRead<PullRequestOverviewDto>>>> OverviewResponses = new();
    public readonly List<CancellationToken> OverviewTokens = [];
    public PullRequestCapabilityKind Capability = PullRequestCapabilityKind.Supported;
    /// The backoff a failed discovery hands back, as the server source does after an unreachable server.
    public DateTime? CapabilityRetryAt;
    public int Discoveries;
    public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) {
        Interlocked.Increment(ref Discoveries);
        return Task.FromResult(new PullRequestCapability(Capability, 1, RetryAt: CapabilityRetryAt));
    }
    public void ResetSession(string sessionId) { Resets++; }
    public async Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) {
        Lists++;
        if (ListGate is { } gate) await gate.Task.WaitAsync(ct);
        if (ListResponses.TryDequeue(out var response)) return response;
        if (ListFailure is { } reason) return new(PullRequestReadKind.SubjectUnavailable, Reason: reason);
        return new(PullRequestReadKind.Ready, new() { Items = Links });
    }
    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => ListAsync(sessionId, ct);
    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        Overviews++; OverviewTokens.Add(ct);
        if (OverviewResponses.TryDequeue(out var response)) return response(subject, ct);
        return Task.FromResult(Failure is null ? Overview(subject) : new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Unavailable,
            Subject: subject, AccessFailure: Failure, Reason: Failure == "denied" ? "github_access_denied" : "timeout"));
    }
    public PullRequestRead<PullRequestOverviewDto> Overview(PullRequestSubjectDto subject, string? title = null) => new(PullRequestReadKind.Ready,
        new() { Title = title ?? TitleFor?.Invoke(subject) ?? OverviewTitle, Description = "Private description", HeadSha = HeadSha,
            Lifecycle = Lifecycles.GetValueOrDefault(subject.Number, "open"),
            Checks = new() { Availability = new() { Status = "ready", FetchedAt = time.GetUtcNow().UtcDateTime }, Rollup = RollupFor?.Invoke(subject) ?? "success" } },
        subject, time.GetUtcNow().UtcDateTime, AccessValidForSeconds: AccessSeconds, RequestStarted: time.GetTimestamp());
    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
        string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        Pages++;
        if (RestartNextPage is { } reason) {
            RestartNextPage = null;
            return Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Restart, Subject: subject, Reason: reason));
        }
        var page = cursor is null ? 0 : int.Parse(cursor[^8..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var id = "item-" + page.ToString(CultureInfo.InvariantCulture);
        object item = PageItemFor?.Invoke(subject, section) ?? PageItem?.Invoke(section) ?? (section switch {
            "checks" => new PullRequestCheckDto { Id = id, Availability = "available", Name = "test", Outcome = "failure", HeadSha = new string('a', 40) },
            "reviewers" => new PullRequestReviewerDto { Id = id, Availability = "available" },
            "reviews" => new PullRequestReviewDto { Id = id, Availability = "available", Body = "Private review", State = "commented" },
            "threads" => new PullRequestThreadDto { Id = id, Availability = "available", Path = "source.cs", DiffHunk = "+private code",
                RootComment = new() { Id = "comment", Availability = "available", Body = "Private thread" } },
            _ => new PullRequestCommentDto { Id = id, Availability = "available", Body = "Private comment" }
        });
        var next = page + 1 < TotalPages ? (page + 1).ToString("x64", CultureInfo.InvariantCulture) : null;
        var read = new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Ready, new() {
            SnapshotId = new string('a', 64), SnapshotStartedAt = time.GetUtcNow().UtcDateTime, SnapshotCompletedAt = time.GetUtcNow().UtcDateTime,
            Coverage = "complete", HeadSha = section == "checks" ? HeadSha : null, Total = new() { Kind = "exact", Value = TotalPages },
            ExcludedByFilter = new() { Kind = "exact", Value = 0 }, Items = EmptyPages ? [] : [(T)item], PageCursor = page.ToString("x64", CultureInfo.InvariantCulture), NextCursor = next, HasMore = next is not null
        }, subject, time.GetUtcNow().UtcDateTime, AccessValidForSeconds: AccessSeconds, RequestStarted: time.GetTimestamp());
        return PageGate is { } gate ? gate.Task.ContinueWith(_ => read, TaskScheduler.Default) : Task.FromResult(read);
    }
    public static PullRequestLinkDto Link(int number) => new() { Provider = "github", Host = "github.com", RepoHash = "hash",
        Owner = "example", RepoName = "repo", Number = number, Url = $"https://github.com/example/repo/pull/{number}", Title = "Linked PR", HeadRef = "feature" };
}
