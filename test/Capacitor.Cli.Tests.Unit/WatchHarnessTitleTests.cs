using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class WatchHarnessTitleTests {
    sealed class FixedStore(Func<StoreTitle?> read) : IHarnessTitleStore {
        public bool RecordsChangeTime => false;
        public StoreTitle? Read() => read();
    }

    static readonly StoreTitle Named = new("Store name", HarnessTitleKind.Rename, null);

    static WatchState StateFor(IHarnessTitleStore store) => new() { TitleTracker = new HarnessTitleTracker(store.RecordsChangeTime) };

    static Task Poll(IHarnessTitleStore store, WatchState state, WireMockServer server, TimeProvider time, TimeSpan? budget = null, Action? beat = null) =>
        WatchCommand.PostHarnessTitleAsync(store, state,
            async (post, b, ct) => {
                using var client = new HttpClient();
                return await HarnessTitleClient.PostOrFallBackAsync(client, time, server.Url!, "abc", post, ct, b);
            },
            budget ?? TimeSpan.FromSeconds(10), time, beat ?? (() => { }), _ => { }, default);

    static int Posts(WireMockServer server) => server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title");

    [Test]
    public async Task A_refused_title_is_posted_once_and_not_again_on_later_drains() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(403));
        var store = new FixedStore(() => Named);
        var state = StateFor(store);
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);

        for (var drain = 0; drain < 3; drain++) {
            await Poll(store, state, server, time);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        await Assert.That(Posts(server)).IsEqualTo(1);
    }

    [Test]
    public async Task A_session_not_yet_visible_is_retried_no_sooner_than_the_gap() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"session_not_found"}"""));
        var store = new FixedStore(() => Named);
        var state = StateFor(store);
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await Poll(store, state, server, time);
        time.Advance(TimeSpan.FromSeconds(10));
        await Poll(store, state, server, time);
        time.Advance(TimeSpan.FromSeconds(10));
        await Poll(store, state, server, time);

        await Assert.That(Posts(server)).IsEqualTo(1);

        time.Advance(WatchCommand.HarnessTitleRetryGap);
        await Poll(store, state, server, time);

        await Assert.That(Posts(server)).IsEqualTo(2);
    }

    [Test]
    public async Task A_new_store_value_is_posted_without_waiting_for_the_gap() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"session_not_found"}"""));
        var current = Named;
        var store   = new FixedStore(() => current);
        var state   = StateFor(store);
        var time    = new FakeTimeProvider(DateTimeOffset.UtcNow);

        await Poll(store, state, server, time);
        current = Named with { Title = "Renamed" };
        time.Advance(TimeSpan.FromSeconds(1));
        await Poll(store, state, server, time);

        await Assert.That(Posts(server)).IsEqualTo(2);
    }

    [Test]
    public async Task A_server_slower_than_the_budget_returns_within_it() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(20)));
        var store = new FixedStore(() => Named);
        var state = StateFor(store);
        var beats = 0;

        var started = TimeProvider.System.GetTimestamp();
        await Poll(store, state, server, TimeProvider.System, TimeSpan.FromSeconds(1), () => beats++);

        await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(beats).IsEqualTo(1);
    }

    [Test]
    public async Task A_throwing_store_does_not_escape() {
        using var server = WireMockServer.Start();
        var store = new FixedStore(() => throw new InvalidOperationException("boom"));

        await Poll(store, StateFor(store), server, TimeProvider.System);

        await Assert.That(Posts(server)).IsEqualTo(0);
    }

    /// <summary>B's post fails, so it may have committed; when the store returns to the acknowledged A, A is sent
    /// again rather than assumed to be what the server still holds.</summary>
    [Test]
    public async Task A_return_to_an_acknowledged_title_after_a_failed_post_is_sent() {
        var current = Named;
        var store   = new FixedStore(() => current);
        var state   = StateFor(store);
        var time    = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sent    = new List<string>();
        var answers = new Queue<HarnessTitleOutcome>([HarnessTitleOutcome.Posted, HarnessTitleOutcome.Failed, HarnessTitleOutcome.Posted]);

        Task Poll() => WatchCommand.PostHarnessTitleAsync(store, state,
            (post, _, _) => { sent.Add(post.Title); return Task.FromResult(answers.Dequeue()); },
            TimeSpan.FromSeconds(10), time, () => { }, _ => { }, default);

        await Poll();
        current = Named with { Title = "B" };
        time.Advance(TimeSpan.FromSeconds(1));
        await Poll();
        current = Named;
        time.Advance(TimeSpan.FromSeconds(1));
        await Poll();

        await Assert.That(sent).IsEquivalentTo(["Store name", "B", "Store name"], CollectionOrdering.Matching);
    }

    static WatchState ReadyForLlmTitle(WatchState state) {
        state.ThresholdReached = true;
        state.FirstUserText    = "Fix the login bug";
        state.EventCount       = 5;
        return state;
    }

    /// <summary>Only a store title the server took through <c>/hooks/harness-title</c> suppresses LLM titling. An older
    /// server takes it through set-title, which fills only an untitled session, so that session still needs a
    /// generated title; the same state with the new route is the positive control.</summary>
    [Test]
    public async Task A_store_title_suppresses_llm_titling_only_when_the_harness_route_took_it() {
        using var newServer = WireMockServer.Start();
        newServer.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var oldServer = WireMockServer.Start();
        oldServer.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));

        var store    = new FixedStore(() => Named);
        var recorded = ReadyForLlmTitle(StateFor(store));
        await Poll(store, recorded, newServer, TimeProvider.System);
        var legacy = ReadyForLlmTitle(StateFor(store));
        await Poll(store, legacy, oldServer, TimeProvider.System);

        await Assert.That(oldServer.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/set-title")).IsEqualTo(1);
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(recorded, agentId: null)).IsFalse();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(legacy, agentId: null)).IsTrue();
    }

    [Test]
    public async Task A_store_title_whose_post_fails_does_not_suppress_llm_titling() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(503));
        var store = new FixedStore(() => Named);
        var state = ReadyForLlmTitle(StateFor(store));

        await Poll(store, state, server, TimeProvider.System);

        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(state, agentId: null)).IsTrue();
    }

    const string CustomTitle = """{"type":"custom-title","customTitle":"Mine","sessionId":"s"}""";
    const string AiTitle     = """{"type":"ai-title","aiTitle":"Claude's","sessionId":"s"}""";

    static WatchState AfterLine(string vendor, string line, bool? serverRecordsHarnessTitles) {
        var state = ReadyForLlmTitle(new WatchState { ServerRecordsHarnessTitles = serverRecordsHarnessTitles });
        WatchCommand.ObserveTitleLine(state, vendor, line);
        return state;
    }

    /// <summary>An older server records none of these lines, so generation must still run there.</summary>
    [Test]
    [Arguments("claude", CustomTitle)]
    [Arguments("pi", """{"type":"session_info","name":"x"}""")]
    [Arguments("gemini", """{"$set":{"summary":"x"}}""")]
    [Arguments("opencode", """{"type":"session_title","title":"x"}""")]
    public async Task A_title_line_only_a_newer_server_records_suppresses_llm_titling_only_there(string vendor, string line) {
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(AfterLine(vendor, line, serverRecordsHarnessTitles: false), agentId: null)).IsTrue();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(AfterLine(vendor, line, serverRecordsHarnessTitles: true), agentId: null)).IsFalse();
    }

    /// <summary>An older server whose first probe is inconclusive still gets a generated title: the watcher may never
    /// probe again before a short session ends, and that server records nothing from the line.</summary>
    [Test]
    public async Task A_title_line_does_not_suppress_llm_titling_while_the_probe_is_inconclusive() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(503));
        using var client = new HttpClient();
        var probed = await HarnessTitleClient.ServerRecordsHarnessTitlesAsync(client, TimeProvider.System, server.Url!, TimeSpan.FromSeconds(5), default);

        await Assert.That(probed).IsNull();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(AfterLine("claude", CustomTitle, probed), agentId: null)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [Arguments(null)]
    public async Task A_claude_ai_title_suppresses_llm_titling_on_every_server(bool? serverRecordsHarnessTitles) =>
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(AfterLine("claude", AiTitle, serverRecordsHarnessTitles), agentId: null)).IsFalse();

    /// <summary>The shutdown read gets whatever is left of the kill grace; with none left it reads and sends nothing
    /// rather than run past the kill.</summary>
    [Test]
    public async Task A_spent_budget_sends_nothing() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        var reads = 0;
        var store = new FixedStore(() => { reads++; return Named; });

        await Poll(store, StateFor(store), server, TimeProvider.System, TimeSpan.Zero);

        await Assert.That(reads).IsEqualTo(0);
        await Assert.That(Posts(server)).IsEqualTo(0);
    }

    /// <summary>A slow store read (a synchronous SQLite query) spends the budget too, so a read that uses it all sends
    /// nothing rather than start the post with a fresh budget past the shutdown deadline.</summary>
    [Test]
    public async Task A_read_that_spends_the_budget_sends_nothing() {
        var time  = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new FixedStore(() => { time.Advance(TimeSpan.FromSeconds(4)); return Named; });
        var state = StateFor(store);
        var posts = 0;

        await WatchCommand.PostHarnessTitleAsync(store, state,
            (_, _, _) => { posts++; return Task.FromResult(HarnessTitleOutcome.Posted); },
            TimeSpan.FromSeconds(3), time, () => { }, _ => { }, default);

        await Assert.That(posts).IsEqualTo(0);
    }

    /// <summary>The post gets only what the read left of the budget.</summary>
    [Test]
    public async Task The_post_gets_what_the_read_left_of_the_budget() {
        var time    = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store   = new FixedStore(() => { time.Advance(TimeSpan.FromSeconds(1)); return Named; });
        var state   = StateFor(store);
        var granted = TimeSpan.Zero;

        await WatchCommand.PostHarnessTitleAsync(store, state,
            (_, b, _) => { granted = b; return Task.FromResult(HarnessTitleOutcome.Posted); },
            TimeSpan.FromSeconds(3), time, () => { }, _ => { }, default);

        await Assert.That(granted).IsEqualTo(TimeSpan.FromSeconds(2));
    }

    /// <summary>A read that never returns (a locked database) is abandoned at the budget: the call returns within it
    /// and posts nothing, rather than hold the watcher past its shutdown deadline.</summary>
    [Test]
    public async Task A_read_that_never_returns_is_abandoned_at_the_budget() {
        using var release = new ManualResetEventSlim();
        var store = new FixedStore(() => { release.Wait(); throw new InvalidOperationException("released"); });
        var state = StateFor(store);
        var posts = 0;

        try {
            var started = TimeProvider.System.GetTimestamp();
            await WatchCommand.PostHarnessTitleAsync(store, state,
                (_, _, _) => { posts++; return Task.FromResult(HarnessTitleOutcome.Posted); },
                TimeSpan.FromSeconds(3), TimeProvider.System, () => { }, _ => { }, default);

            await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(10));
            await Assert.That(posts).IsEqualTo(0);
        } finally {
            release.Set();
        }
    }

    /// <summary>A read still stuck from an earlier poll is waited on, not joined by another: the store readers are not
    /// safe to run concurrently, and a stuck store would otherwise pile up a blocked worker per poll.</summary>
    [Test]
    public async Task A_stuck_read_is_not_joined_by_another_until_it_finishes() {
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var store = new FixedStore(() => { if (Interlocked.Increment(ref reads) == 1) release.Wait(); return Named; });
        var state = StateFor(store);
        var posts = 0;

        Task Poll() => WatchCommand.PostHarnessTitleAsync(store, state,
            (_, _, _) => { posts++; return Task.FromResult(HarnessTitleOutcome.Posted); },
            TimeSpan.FromSeconds(1), TimeProvider.System, () => { }, _ => { }, default);

        try {
            var started = TimeProvider.System.GetTimestamp();
            await Poll();
            await Poll();

            await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(10));
            await Assert.That(reads).IsEqualTo(1);
            await Assert.That(posts).IsEqualTo(0);
        } finally {
            release.Set();
        }

        await Poll(); // takes up the finished read
        await Assert.That(reads).IsEqualTo(1);
        await Assert.That(posts).IsEqualTo(1);

        await Poll();
        await Assert.That(reads).IsEqualTo(2);
    }
}
