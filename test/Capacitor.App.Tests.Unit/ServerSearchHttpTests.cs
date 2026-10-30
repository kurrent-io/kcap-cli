using Capacitor.App.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

public class ServerSearchHttpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Search_sends_query_mode_and_limit_and_reads_the_lane() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/search").UsingGet()
                  .WithParam("q", "auth bug").WithParam("mode", "transcripts").WithParam("limit", "20"))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"sessions":null,"transcripts":[{"session_id":"s1","slug":"a","title":"t","started_at":"2026-10-22T13:04:24Z","snippet":"x","event_id":"e1","matched_field":"transcript"}],"took_ms":4}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var fetch = await ServerSearchHttp.Search(http, profiles)(new SearchRequest("auth bug", SearchMode.Transcripts), CancellationToken.None);
        await Assert.That(fetch.Outcome).IsEqualTo(ServerReadOutcome.Ok);
        await Assert.That(fetch.Response!.Transcripts!.Length).IsEqualTo(1);
    }

    [Test]
    public async Task Search_maps_401_to_unauthorized_and_500_to_failed() {
        using var server = WireMockServer.Start();
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var search = ServerSearchHttp.Search(http, profiles);
        var request = new SearchRequest("abc", SearchMode.Sessions);

        server.Given(Request.Create().WithPath("/api/search").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));
        await Assert.That((await search(request, CancellationToken.None)).Outcome).IsEqualTo(ServerReadOutcome.Unauthorized);

        server.Reset();
        server.Given(Request.Create().WithPath("/api/search").UsingGet()).RespondWith(Response.Create().WithStatusCode(500));
        var failed = await search(request, CancellationToken.None);
        await Assert.That(failed.Outcome).IsEqualTo(ServerReadOutcome.Failed);
        await Assert.That(failed.Response).IsNull();
    }

    [Test]
    public async Task Summary_maps_404_and_403_to_unavailable() {
        using var server = WireMockServer.Start();
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var summary = ServerSearchHttp.Summary(http, profiles);

        server.Given(Request.Create().WithPath("/api/sessions/s1/summary").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        await Assert.That((await summary("s1", CancellationToken.None)).Outcome).IsEqualTo(ServerReadOutcome.Unavailable);

        server.Reset();
        server.Given(Request.Create().WithPath("/api/sessions/s1/summary").UsingGet()).RespondWith(Response.Create().WithStatusCode(403));
        await Assert.That((await summary("s1", CancellationToken.None)).Outcome).IsEqualTo(ServerReadOutcome.Unavailable);
    }

    [Test]
    public async Task Summary_reads_the_session() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/summary").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"session_id":"s1","status":"ended","started_at":"2026-10-22T13:04:24Z"}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var fetch = await ServerSearchHttp.Summary(http, profiles)("s1", CancellationToken.None);
        await Assert.That(fetch.Outcome).IsEqualTo(ServerReadOutcome.Ok);
        await Assert.That(fetch.Session!.SessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task HandoffContext_reads_the_context_and_maps_404_to_unavailable() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/handoff-context").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"session_id":"s1","repo_owner":"o","repo_name":"r","branch":"feat","pull_request":{"number":7,"title":"t","state":"open","head_ref":"feat","head_sha":"abc","head_owner":"o","head_repo":"r","head_is_fork":false},"session_commits":["abc"]}"""));
        server.Given(Request.Create().WithPath("/api/sessions/gone/handoff-context").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var read = ServerSearchHttp.HandoffContext(http, profiles);

        var fetch = await read("s1", CancellationToken.None);
        await Assert.That(fetch.Outcome).IsEqualTo(ServerReadOutcome.Ok);
        var context = fetch.Context!;
        await Assert.That(context.Branch).IsEqualTo("feat");
        await Assert.That(context.PullRequest!.Number).IsEqualTo(7);
        await Assert.That(context.PullRequest.HeadIsFork).IsFalse();
        await Assert.That(context.SessionCommits!.Count).IsEqualTo(1);
        var gone = await read("gone", CancellationToken.None);
        await Assert.That(gone.Outcome).IsEqualTo(ServerReadOutcome.Unavailable);
        await Assert.That(gone.Context).IsNull();
    }

    [Test]
    public async Task Search_propagates_cancellation() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/search").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(2)).WithBody("{}"));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await ServerSearchHttp.Search(http, profiles)(new SearchRequest("abc", SearchMode.Sessions), cts.Token))
            .Throws<OperationCanceledException>();
    }
}
