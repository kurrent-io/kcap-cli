using Capacitor.Cli.Core.PullRequests;
using Capacitor.Cli.Core.PullRequests.Readers;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The payments session's one pull request, read whole: every section answers in a single complete
/// page, so a docs figure never waits on paging. One check is still running and Dana's requested
/// change leaves a thread open, which is what keeps the pull request from merging.
sealed class NorthwindPullRequestSource(FakeTimeProvider time) : IPullRequestSource, IPullRequestReaders {
    public const int Number = 88;
    public const string Title = "Add retry to the payments gateway";

    static readonly string HeadSha = new('8', 40);

    public static readonly PullRequestLinkDto Link = new() {
        Provider = "github", Host = "github.com", RepoHash = "northwind-payments-api", Owner = "northwind", RepoName = "payments-api",
        Number = Number, Url = $"https://github.com/northwind/payments-api/pull/{Number}", Title = Title, HeadRef = "feat/gateway-retry",
    };

    static readonly PullRequestActorDto Dana = new() { Id = "dana", Kind = "user", Login = "dana-okafor", Name = "Dana Okafor" };
    static readonly PullRequestActorDto Sam = new() { Id = "sam", Kind = "user", Login = "sam-reyes", Name = "Sam Reyes" };
    static readonly PullRequestActorDto Maya = new() { Id = "maya", Kind = "user", Login = "maya-chen", Name = "Maya Chen" };

    DateTime At(int minutesAgo) => time.GetUtcNow().UtcDateTime.AddMinutes(-minutesAgo);

    public void DescribeSession(string sessionId, PullRequestRepository? repository, string? branch) { }
    public PullRequestReaderNote? NoteFor(string provider, string host) => null;
    public string? PrLink(string? url, PullRequestSubjectDto subject) => PullRequestWire.PrLink(url, subject);

    public Task<PullRequestCapability> DiscoverAsync(bool refresh, CancellationToken ct) =>
        Task.FromResult(new PullRequestCapability(PullRequestCapabilityKind.Supported, 1));

    public void ResetSession(string sessionId) { }

    public Task<PullRequestRead<PullRequestLinkListDto>> ListAsync(string sessionId, CancellationToken ct) =>
        Task.FromResult(new PullRequestRead<PullRequestLinkListDto>(PullRequestReadKind.Ready,
            new() { Items = sessionId == "s1" ? [Link] : [] }));

    public Task<PullRequestRead<PullRequestLinkListDto>> LegacyLinksAsync(string sessionId, CancellationToken ct) => ListAsync(sessionId, ct);

    public Task<PullRequestRead<PullRequestOverviewDto>> OverviewAsync(string sessionId, PullRequestSubjectDto subject, CancellationToken ct) {
        var fetched = new PullRequestAvailabilityDto { Status = "ready", FetchedAt = At(1) };
        var overview = new PullRequestOverviewDto {
            Title = Title, Url = Link.Url, Lifecycle = "open", IsDraft = false, Mergeable = true,
            HeadRef = "feat/gateway-retry", BaseRef = "main", HeadSha = HeadSha, UpdatedAt = At(4),
            ReviewDecision = "changes_requested",
            Description = """
                Wraps `GatewayClient` in a retry policy so a transient 503 from the payments gateway no longer fails the charge.

                - Three attempts with exponential backoff and jitter
                - Retries only on 502, 503 and 504; a 4xx fails at once
                - Charges carry an idempotency key, so a retried request cannot charge twice

                Closes PAY-142.
                """,
            Checks = new() { Availability = fetched, Rollup = "pending", HeadSha = HeadSha },
            Reviews = new() {
                Availability = fetched, Published = Exact(2), Approved = Exact(1), ChangesRequested = Exact(1),
                OutstandingUsers = Exact(0), OutstandingTeams = Exact(0),
            },
            Conversation = new() { Availability = fetched, Count = Exact(2) },
            Threads = new() { Availability = fetched, Unresolved = Exact(1) },
        };
        return Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Ready, overview, subject,
            time.GetUtcNow().UtcDateTime, AccessValidForSeconds: 3600, RequestStarted: time.GetTimestamp()));
    }

    public Task<PullRequestRead<PullRequestPageDto<T>>> PageAsync<T>(string sessionId, PullRequestSubjectDto subject, string section,
            string? cursor, string? resolved, string? threadId, CancellationToken ct) where T : class {
        object[] items = section switch {
            "checks" => [
                Check("c1", "build", "success", 9),
                Check("c2", "unit-tests", "success", 7),
                Check("c3", "lint", "success", 8),
                Check("c4", "integration-tests", "pending", 2),
            ],
            "reviewers" => [
                new PullRequestReviewerDto { Id = "r1", Availability = "available", Actor = Sam, ReviewState = "approved", SubmittedAt = At(30) },
                new PullRequestReviewerDto { Id = "r2", Availability = "available", Actor = Dana, ReviewState = "changes_requested", SubmittedAt = At(12) },
            ],
            "reviews" => [
                new PullRequestReviewDto { Id = "v1", Availability = "available", Author = Sam, State = "approved", SubmittedAt = At(30),
                    Body = "Backoff looks right. Nice to see the idempotency key travel with the retry." },
                new PullRequestReviewDto { Id = "v2", Availability = "available", Author = Dana, State = "changes_requested", SubmittedAt = At(12),
                    Body = "A 429 should back off too, and honour Retry-After when the gateway sends it." },
            ],
            "threads" => [
                new PullRequestThreadDto { Id = "t1", Availability = "available", IsResolved = false, IsOutdated = false,
                    Path = "src/Payments.Gateway/GatewayClient.cs", Line = 48, DiffSide = "RIGHT",
                    DiffHunk = "@@ -45,3 +45,4 @@\n+    static bool IsTransient(HttpStatusCode code) =>\n+        code is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;",
                    RootComment = new() { Id = "t1c", Availability = "available", Author = Dana, CreatedAt = At(12),
                        Body = "Treat 429 as transient as well, and wait for Retry-After before the next attempt." } },
            ],
            _ => [
                new PullRequestCommentDto { Id = "m1", Availability = "available", Author = Maya, CreatedAt = At(40),
                    Body = "Ready for review. The integration suite replays the 503 burst from last Tuesday." },
                new PullRequestCommentDto { Id = "m2", Availability = "available", Author = Sam, CreatedAt = At(28),
                    Body = "Ran it against staging: the burst now clears on the second attempt." },
            ],
        };
        var page = new PullRequestPageDto<T> {
            SnapshotId = new string('n', 64), SnapshotStartedAt = time.GetUtcNow().UtcDateTime, SnapshotCompletedAt = time.GetUtcNow().UtcDateTime,
            Coverage = "complete", HeadSha = section == "checks" ? HeadSha : null, Total = Exact(items.Length), ExcludedByFilter = Exact(0),
            Items = [.. items.Cast<T>()], PageCursor = new string('0', 64), HasMore = false,
        };
        return Task.FromResult(new PullRequestRead<PullRequestPageDto<T>>(PullRequestReadKind.Ready, page, subject,
            time.GetUtcNow().UtcDateTime, AccessValidForSeconds: 3600, RequestStarted: time.GetTimestamp()));
    }

    PullRequestCheckDto Check(string id, string name, string outcome, int minutesAgo) => new() {
        Id = id, Availability = "available", Name = name, AppName = "GitHub Actions", Outcome = outcome,
        Status = outcome == "pending" ? "in_progress" : "completed", Conclusion = outcome == "pending" ? null : outcome,
        StartedAt = At(minutesAgo + 3), CompletedAt = outcome == "pending" ? null : At(minutesAgo), HeadSha = HeadSha,
    };

    static PullRequestCountDto Exact(int value) => new() { Value = value, Kind = "exact" };
}
