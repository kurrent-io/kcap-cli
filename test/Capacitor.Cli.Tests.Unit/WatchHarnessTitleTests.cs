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

    /// <summary>A store title suppresses LLM titling even when its post never lands; the store without a title is the
    /// positive control that the same state would otherwise trigger generation.</summary>
    [Test]
    public async Task A_store_title_suppresses_llm_titling_even_when_its_post_fails() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(503));

        var titled   = new FixedStore(() => Named);
        var withName = ReadyForLlmTitle(StateFor(titled));
        await Poll(titled, withName, server, TimeProvider.System);

        var untitled = new FixedStore(() => null);
        var without  = ReadyForLlmTitle(StateFor(untitled));
        await Poll(untitled, without, server, TimeProvider.System);

        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(withName, agentId: null)).IsFalse();
        await Assert.That(WatchCommand.ShouldGenerateLlmTitle(without, agentId: null)).IsTrue();
    }

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
}
