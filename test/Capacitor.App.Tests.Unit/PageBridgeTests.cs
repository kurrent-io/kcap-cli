using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// What the .NET end of a page's bridge answers, and when it reads and polls results. Every
/// message arrives the way the host page forwards it, wrapped with the load's nonce.
public class PageBridgeTests {
    const string Id = "01ec";
    const string Nonce = "n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    const int Version = 3;

    static ArtefactDto Page() => new(Id, "Poll", "u1", "org", Version, DateTimeOffset.UtcNow, false, "https://srv.test/pages/01ec");

    static PageBridgeReply Reply(int status, string? body = null) =>
        new(status, body is null ? null : JsonDocument.Parse(body).RootElement.Clone());

    static string Results(string etag, bool closed = false) =>
        $$$"""{"mode":"aggregate","version":3,"closed":{{{(closed ? "true" : "false")}}},"revealed":true,"etag":"{{{etag}}}","tallies":{"a":1}}""";

    static string Wrap(string data, string nonce = Nonce) => $$$"""{"nonce":"{{{nonce}}}","data":{{{data}}}}""";

    sealed class Harness : IAsyncDisposable {
        public FakePageSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public List<JsonObject> Posts { get; } = [];
        PageBridge? _bridge;

        public PageBridge Bridge(PageResultsAccess access, bool start = true) {
            _bridge = new PageBridge(Source, Page(), Version, access, Nonce, json => {
                lock (Posts) Posts.Add(JsonNode.Parse(json)!.AsObject());
                return Task.CompletedTask;
            }, Time);
            if (start) _bridge.Start();
            return _bridge;
        }

        public Task Send(string data) => _bridge!.OnNativeMessageAsync(Wrap(data));

        public async Task Tick() {
            Time.Advance(PageBridge.PollInterval);
            await _bridge!.TickCompletion;
        }

        public string[] Types() {
            lock (Posts) return Posts.Select(p => (string)p["type"]!).ToArray();
        }

        public string[] Calls() => Source.BridgeRequested.Select(r => r.Call).ToArray();

        public ValueTask DisposeAsync() => _bridge?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    [Test]
    [Arguments("""{"nonce":"wrong-nonce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{"data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{"nonce":null,"data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{"kcap":"v1","type":"ready"}""")]
    [Arguments("""{"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":"ready"}""")]
    [Arguments("""{"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v2","type":"ready"}}""")]
    [Arguments("""{"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":7}}""")]
    [Arguments("ready")]
    [Arguments("")]
    [Arguments("{\"nonce\":")]
    public async Task A_message_without_this_loads_nonce_or_envelope_is_dropped(string raw) {
        await using var h = new Harness();
        var bridge = h.Bridge(PageResultsAccess.Visible);

        await bridge.OnNativeMessageAsync(raw);

        await Assert.That(h.Posts).IsEmpty();
        await Assert.That(h.Source.BridgeRequested).IsEmpty();
    }

    /// The host script writes the nonce first, so this load's exact prefix is the only shape that is parsed.
    [Test]
    [Arguments("""{"data":{"kcap":"v1","type":"ready"},"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    [Arguments(""" {"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{ "nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAx","data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":"ready"}}""")]
    [Arguments("""{"nonce":"\u006e0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":"ready"}}""")]
    public async Task A_message_not_starting_with_this_loads_nonce_prefix_is_dropped(string raw) {
        await using var h = new Harness();
        var bridge = h.Bridge(PageResultsAccess.Visible);

        await bridge.OnNativeMessageAsync(raw);

        await Assert.That(h.Posts).IsEmpty();
        await Assert.That(h.Source.BridgeRequested).IsEmpty();
    }

    [Test]
    public async Task The_host_scripts_shape_passes_the_prefix_check() {
        await using var h = new Harness();
        h.Source.EnqueueReply("mine", Reply(200, """{"responses":[]}"""));
        var bridge = h.Bridge(PageResultsAccess.Hidden);

        await bridge.OnNativeMessageAsync("""{"nonce":"n0nce-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","data":{"kcap":"v1","type":"ready"}}""");

        await Assert.That(h.Types()).IsEquivalentTo(new[] { "init" });
    }

    /// GetString throws InvalidOperationException on a lone surrogate; the drop must be as silent as for bad JSON.
    [Test]
    [NotInParallel]
    [Arguments("""{"kcap":"v1","type":"\uD800"}""")]
    [Arguments("""{"kcap":"\uDC00","type":"ready"}""")]
    public async Task A_lone_surrogate_is_dropped_without_a_word(string data) {
        await using var h = new Harness();
        h.Bridge(PageResultsAccess.Visible);
        using var stderr = ConsoleOutput.StartErrorCapture();

        await h.Send(data);

        await Assert.That(stderr.GetCapturedError()).IsEmpty();
        await Assert.That(h.Posts).IsEmpty();
        await Assert.That(h.Source.BridgeRequested).IsEmpty();
    }

    [Test]
    public async Task An_oversized_message_is_dropped_before_it_is_parsed() {
        await using var h = new Harness();
        h.Bridge(PageResultsAccess.Visible);

        await h.Send($$$"""{"kcap":"v1","type":"submit","payload":{"x":"{{{new string('a', 300 * 1024)}}}"}}""");

        await Assert.That(h.Source.BridgeRequested).IsEmpty();
    }

    [Test]
    public async Task Ready_sends_init_with_the_viewers_newest_answer_then_results() {
        await using var h = new Harness();
        h.Source.ViewerName = "Ada Lovelace";
        h.Source.EnqueueReply("mine", Reply(200, """{"responses":[{"payload":{"choice":"b"}},{"payload":{"choice":"a"}}]}"""));
        h.Source.EnqueueReply("results", Reply(200, Results("e1")));
        h.Bridge(PageResultsAccess.Visible);

        await h.Send("""{"kcap":"v1","type":"ready"}""");

        await Assert.That(h.Types()).IsEquivalentTo(new[] { "init", "results" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var init = h.Posts[0];
        await Assert.That((int)init["version"]!).IsEqualTo(Version);
        await Assert.That((string)init["theme"]!).IsEqualTo("dark");
        await Assert.That((string)init["viewer"]!["name"]!).IsEqualTo("Ada Lovelace");
        await Assert.That(init["prior"]!.ToJsonString()).IsEqualTo("""{"choice":"b"}""");
        await Assert.That(init.ContainsKey("nonce")).IsFalse();
        var results = h.Posts[1];
        await Assert.That((string)results["mode"]!).IsEqualTo("aggregate");
        await Assert.That((int)results["version"]!).IsEqualTo(3);
        await Assert.That((bool)results["closed"]!).IsFalse();
        await Assert.That((bool)results["revealed"]!).IsTrue();
        await Assert.That((int)results["results"]!["tallies"]!["a"]!).IsEqualTo(1);
        await Assert.That(h.Source.BridgeRequested[0]).IsEqualTo(("mine", Id, Version, (JsonElement?)null));
    }

    [Test]
    [Arguments(200, """{"responses":[]}""")]
    [Arguments(404, """{"message":"nope"}""")]
    [Arguments(0, null)]
    public async Task Ready_without_a_prior_answer_sends_a_null_prior(int status, string? body) {
        await using var h = new Harness();
        h.Source.EnqueueReply("mine", Reply(status, body));
        h.Bridge(PageResultsAccess.Hidden);

        await h.Send("""{"kcap":"v1","type":"ready"}""");

        await Assert.That(h.Types()).IsEquivalentTo(new[] { "init" });
        await Assert.That(h.Posts[0]["prior"]).IsNull();
        await Assert.That((string)h.Posts[0]["viewer"]!["name"]!).IsEqualTo("");
    }

    [Test]
    public async Task Hidden_results_are_never_read_and_nothing_polls() {
        await using var h = new Harness();
        h.Source.EnqueueReply("submit", Reply(201, """{"response_id":"r1"}"""));
        h.Bridge(PageResultsAccess.Hidden);

        await h.Send("""{"kcap":"v1","type":"ready"}""");
        await h.Send("""{"kcap":"v1","type":"refresh"}""");
        await h.Send("""{"kcap":"v1","type":"submit","payload":{"a":1}}""");
        h.Time.Advance(TimeSpan.FromMinutes(1));

        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "mine", "submit" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(h.Source.AccessChecks).IsEqualTo(0);
        await Assert.That(h.Types()).IsEquivalentTo(new[] { "init", "ack" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Visible_results_are_pushed_on_every_tick_that_changes_them() {
        await using var h = new Harness();
        h.Source.EnqueueReply("results", Reply(200, Results("e1")), Reply(200, Results("e1")), Reply(200, Results("e2")));
        h.Bridge(PageResultsAccess.Visible);

        await h.Tick();
        await h.Tick();
        await h.Tick();

        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "results", "results", "results" });
        await Assert.That(h.Types()).IsEquivalentTo(new[] { "results", "results" });
    }

    [Test]
    public async Task Ready_pushes_results_even_when_a_tick_already_sent_the_same_etag() {
        await using var h = new Harness();
        h.Source.EnqueueReply("results", Reply(200, Results("e1")), Reply(200, Results("e1")));
        h.Bridge(PageResultsAccess.Visible);

        await h.Tick();
        await h.Send("""{"kcap":"v1","type":"ready"}""");

        await Assert.That(h.Types()).IsEquivalentTo(new[] { "results", "init", "results" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_closed_version_stops_the_poll() {
        await using var h = new Harness();
        h.Source.EnqueueReply("results", Reply(200, Results("e1", closed: true)));
        h.Bridge(PageResultsAccess.Visible);

        await h.Tick();
        await h.Tick();
        await h.Tick();

        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "results" });
    }

    [Test]
    [Arguments(403)]
    [Arguments(404)]
    [Arguments(401)]
    public async Task A_refused_or_signed_out_results_read_stops_every_later_read(int status) {
        await using var h = new Harness();
        h.Source.EnqueueReply("results", Reply(status));
        h.Bridge(PageResultsAccess.Visible);

        await h.Tick();
        await h.Tick();
        await h.Send("""{"kcap":"v1","type":"refresh"}""");

        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "results" });
        await Assert.That(h.Posts).IsEmpty();
    }

    [Test]
    public async Task A_failed_poll_is_skipped_and_the_next_tick_tries_again() {
        await using var h = new Harness();
        h.Source.EnqueueReply("results", Reply(0), Reply(503), Reply(200, Results("e1")));
        h.Bridge(PageResultsAccess.Visible);

        await h.Tick();
        await h.Tick();
        await h.Tick();

        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "results", "results", "results" });
        await Assert.That(h.Types()).IsEquivalentTo(new[] { "results" });
    }

    [Test]
    public async Task Unknown_access_is_rechecked_on_the_tick_and_turns_visible() {
        await using var h = new Harness();
        h.Source.EnqueueAccess(PageResultsAccess.Unknown, PageResultsAccess.Visible);
        h.Source.EnqueueReply("results", Reply(200, Results("e1")));
        h.Bridge(PageResultsAccess.Unknown);

        await h.Send("""{"kcap":"v1","type":"ready"}""");
        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "mine" });

        await h.Tick();
        await Assert.That(h.Source.AccessChecks).IsEqualTo(1);
        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "mine" });

        await h.Tick();
        await Assert.That(h.Source.AccessChecks).IsEqualTo(2);
        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "mine", "results" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(h.Types()).IsEquivalentTo(new[] { "init", "results" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);

        await h.Tick();
        await Assert.That(h.Source.AccessChecks).IsEqualTo(2);
        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "mine", "results", "results" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(PageResultsAccess.Hidden)]
    [Arguments(PageResultsAccess.SignedOut)]
    public async Task Unknown_access_settling_hidden_or_signed_out_stops_rechecking(PageResultsAccess settled) {
        await using var h = new Harness();
        h.Source.EnqueueAccess(settled);
        h.Bridge(PageResultsAccess.Unknown);

        await h.Tick();
        await h.Tick();
        await h.Tick();
        await h.Send("""{"kcap":"v1","type":"refresh"}""");

        await Assert.That(h.Source.AccessChecks).IsEqualTo(1);
        await Assert.That(h.Calls()).IsEmpty();
    }

    [Test]
    public async Task Signed_out_access_reads_no_results_and_arms_no_timer() {
        await using var h = new Harness();
        h.Bridge(PageResultsAccess.SignedOut);

        await h.Send("""{"kcap":"v1","type":"refresh"}""");
        h.Time.Advance(TimeSpan.FromMinutes(1));

        await Assert.That(h.Source.AccessChecks).IsEqualTo(0);
        await Assert.That(h.Calls()).IsEmpty();
    }

    [Test]
    public async Task An_accepted_submit_acks_and_pushes_fresh_results() {
        await using var h = new Harness();
        h.Source.EnqueueReply("submit", Reply(201, """{"response_id":"r1"}"""));
        h.Source.EnqueueReply("results", Reply(200, Results("e1")), Reply(200, Results("e1")));
        h.Bridge(PageResultsAccess.Visible);

        await h.Send("""{"kcap":"v1","type":"refresh"}""");
        await h.Send("""{"kcap":"v1","type":"submit","payload":{"choice":"b"}}""");

        await Assert.That(h.Types()).IsEquivalentTo(new[] { "results", "ack", "results" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var ack = h.Posts[1];
        await Assert.That((bool)ack["accepted"]!).IsTrue();
        await Assert.That((string)ack["response_id"]!).IsEqualTo("r1");
        await Assert.That((int)ack["version"]!).IsEqualTo(Version);
        var submitted = h.Source.BridgeRequested[1];
        await Assert.That(submitted.Call).IsEqualTo("submit");
        await Assert.That(submitted.Payload!.Value.GetRawText()).IsEqualTo("""{"choice":"b"}""");
    }

    [Test]
    public async Task A_submit_without_a_payload_sends_an_empty_object() {
        await using var h = new Harness();
        h.Bridge(PageResultsAccess.Hidden);

        await h.Send("""{"kcap":"v1","type":"submit"}""");

        await Assert.That(h.Source.BridgeRequested[0].Payload!.Value.GetRawText()).IsEqualTo("{}");
    }

    [Test]
    [Arguments(409, null, "Responses are closed for this page.")]
    [Arguments(429, null, "Too many answers — try again in a moment.")]
    [Arguments(409, """{"message":"Closed by the owner."}""", "Closed by the owner.")]
    [Arguments(422, """{"message":"Answer is too large."}""", "Answer is too large.")]
    [Arguments(500, "{}", "The server refused this answer (HTTP 500).")]
    public async Task A_refused_submit_acks_with_the_servers_words_and_code(int status, string? body, string error) {
        await using var h = new Harness();
        h.Source.EnqueueReply("submit", Reply(status, body));
        h.Bridge(PageResultsAccess.Visible);

        await h.Send("""{"kcap":"v1","type":"submit","payload":{}}""");

        await Assert.That(h.Types()).IsEquivalentTo(new[] { "ack" });
        var ack = h.Posts[0];
        await Assert.That((bool)ack["accepted"]!).IsFalse();
        await Assert.That((string)ack["error"]!).IsEqualTo(error);
        await Assert.That((int)ack["code"]!).IsEqualTo(status);
        await Assert.That(h.Calls()).IsEquivalentTo(new[] { "submit" });
    }

    [Test]
    public async Task An_unreachable_server_acks_the_submit_as_not_accepted() {
        await using var h = new Harness();
        h.Source.EnqueueReply("submit", Reply(0));
        h.Bridge(PageResultsAccess.Hidden);

        await h.Send("""{"kcap":"v1","type":"submit","payload":{}}""");

        var ack = h.Posts.Single();
        await Assert.That((bool)ack["accepted"]!).IsFalse();
        await Assert.That((int)ack["code"]!).IsEqualTo(0);
        await Assert.That((string)ack["error"]!).IsEqualTo("Couldn't reach the server — try again.");
    }

    [Test]
    public async Task An_unknown_type_is_ignored() {
        await using var h = new Harness();
        h.Bridge(PageResultsAccess.Visible);

        await h.Send("""{"kcap":"v1","type":"resize","height":400}""");

        await Assert.That(h.Posts).IsEmpty();
        await Assert.That(h.Source.BridgeRequested).IsEmpty();
    }

    [Test]
    public async Task Nothing_is_read_or_posted_after_dispose() {
        await using var h = new Harness();
        var bridge = h.Bridge(PageResultsAccess.Visible);
        await bridge.DisposeAsync();

        await h.Send("""{"kcap":"v1","type":"ready"}""");
        h.Time.Advance(TimeSpan.FromMinutes(1));

        await Assert.That(h.Posts).IsEmpty();
        await Assert.That(h.Source.BridgeRequested).IsEmpty();
    }

    [Test]
    public async Task A_throwing_post_does_not_escape_the_message_handler() {
        var source = new FakePageSource();
        await using var bridge = new PageBridge(source, Page(), Version, PageResultsAccess.Hidden, Nonce,
            _ => throw new InvalidOperationException("webview gone"), new FakeTimeProvider());

        await bridge.OnNativeMessageAsync(Wrap("""{"kcap":"v1","type":"ready"}"""));

        await Assert.That(source.BridgeRequested.Select(r => r.Call)).IsEquivalentTo(new[] { "mine" });
    }

    static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bridge-v1.json")))!.AsObject();

    [Test]
    public async Task The_bridge_answers_exactly_the_inbound_types_of_bridge_v1() {
        var inbound = Fixture()["inbound"]!.AsObject().Select(p => p.Key).ToArray();

        await Assert.That(PageBridge.InboundTypes).IsEquivalentTo(inbound);
        foreach (var type in inbound) {
            await using var h = new Harness();
            h.Source.EnqueueReply("results", Reply(200, Results("e1")));
            h.Bridge(PageResultsAccess.Visible);
            await h.Send($$"""{"kcap":"v1","type":"{{type}}"}""");
            await Assert.That(h.Source.BridgeRequested.Count + h.Posts.Count).IsGreaterThan(0);
        }
    }

    [Test]
    public async Task Every_message_the_bridge_sends_is_a_bridge_v1_outbound_shape() {
        var outbound = Fixture()["outbound"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.AsArray().Select(f => (string)f!).ToHashSet());
        await using var h = new Harness();
        h.Source.EnqueueReply("mine", Reply(200, """{"responses":[{"payload":{"a":1}}]}"""));
        h.Source.EnqueueReply("results", Reply(200, Results("e1")), Reply(200, Results("e2")), Reply(200, Results("e3")));
        h.Source.EnqueueReply("submit", Reply(201, """{"response_id":"r1"}"""), Reply(409));
        h.Bridge(PageResultsAccess.Visible);

        await h.Send("""{"kcap":"v1","type":"ready"}""");
        await h.Send("""{"kcap":"v1","type":"submit","payload":{}}""");
        await h.Send("""{"kcap":"v1","type":"submit","payload":{}}""");
        await h.Send("""{"kcap":"v1","type":"refresh"}""");

        await Assert.That(h.Types().Distinct()).IsEquivalentTo(outbound.Keys);
        foreach (var post in h.Posts) {
            var fields = post.Select(p => p.Key).Where(k => k != "type").ToArray();
            await Assert.That(outbound[(string)post["type"]!].IsSupersetOf(fields)).IsTrue();
        }
    }
}
