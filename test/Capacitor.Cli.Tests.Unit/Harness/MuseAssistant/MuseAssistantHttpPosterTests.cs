using System.Text.Json;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Harness.MuseAssistant;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Pins the wire shapes the server binds: the two literal lifecycle routes and a strict,
/// vendor-tagged transcript batch; visibility is always private whatever the profile says.</summary>
public class MuseAssistantHttpPosterTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static readonly MuseAssistantChatState State = new("chat-main", "https://acme.kcap.ai", "user_01HXYZ",
        new("0123456789abcdef0123456789abcdef", MuseAssistantGrantBoundaries.History, null, 1, null), null, null, null);
    static readonly MuseAssistantOpenSession Session = new("451e8f8bd31baf5cf44a88b1ff4231d4", 12, 1_760_000_005_000, true, "/home/hatch", MuseAssistantSessionPhases.PendingStart, null);

    MuseAssistantHttpPoster Poster() => new(Resolutions.Of(new() { ServerUrl = _server.Url!, DefaultVisibility = "org_public" }), new FixedCapacitorHttpClient(), TimeProvider.System);

    void Accept(string path) => _server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));

    JsonElement LastBody(string path) =>
        JsonDocument.Parse(_server.LogEntries.Last(e => e.RequestMessage.Path == path).RequestMessage.Body!).RootElement;

    [Test]
    public async Task Start_body_carries_the_contract_fields_and_is_always_private() {
        Accept("/hooks/session-start/muse-assistant");

        var refused = await Poster().SendAsync(new MuseAssistantStartSession(Session, null, State), "chat-main", default);

        await Assert.That(refused).IsNull();
        var body = LastBody("/hooks/session-start/muse-assistant");
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(Session.SessionId);
        await Assert.That(body.GetProperty("chat_id").GetString()).IsEqualTo("chat-main");
        await Assert.That(body.GetProperty("is_main").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("started_at").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_005_000));
        await Assert.That(body.GetProperty("default_visibility").GetString()).IsEqualTo("private");
        await Assert.That(body.GetProperty("home_dir").GetString()).IsEqualTo("/home/hatch");
    }

    [Test]
    public async Task A_start_for_a_side_chat_posts_is_main_false() {
        Accept("/hooks/session-start/muse-assistant");

        await Poster().SendAsync(new MuseAssistantStartSession(Session with { IsMain = false }, null, State), "chat-main", default);

        await Assert.That(LastBody("/hooks/session-start/muse-assistant").GetProperty("is_main").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Transcript_body_is_a_strict_vendor_tagged_batch_with_seqs_as_line_numbers() {
        Accept("/hooks/transcript");

        var refused = await Poster().SendAsync(new MuseAssistantSendLines(Session.SessionId, ["""{"a":1}""", """{"b":2}"""], [12, 15], State), "chat-main", default);

        await Assert.That(refused).IsNull();
        var body = LastBody("/hooks/transcript");
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(Session.SessionId);
        await Assert.That(body.GetProperty("vendor").GetString()).IsEqualTo("muse-assistant");
        await Assert.That(body.GetProperty("strict").GetBoolean()).IsTrue();
        await Assert.That(body.GetProperty("line_numbers").EnumerateArray().Select(e => e.GetInt32())).IsEquivalentTo([12, 15], CollectionOrdering.Matching);
        await Assert.That(body.GetProperty("lines").EnumerateArray().Select(e => e.GetString()!)).IsEquivalentTo(["""{"a":1}""", """{"b":2}"""], CollectionOrdering.Matching);
    }

    [Test]
    public async Task End_body_carries_the_items_timestamp_and_an_idle_reason() {
        Accept("/hooks/session-end/muse-assistant");

        var refused = await Poster().SendAsync(new MuseAssistantEndSession(Session, 1_760_000_420_000, null, State), "chat-main", default);

        await Assert.That(refused).IsNull();
        var body = LastBody("/hooks/session-end/muse-assistant");
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo(Session.SessionId);
        await Assert.That(body.GetProperty("ended_at").GetDateTimeOffset()).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1_760_000_420_000));
        await Assert.That(body.GetProperty("reason").GetString()).IsEqualTo("idle");
    }

    [Test]
    public async Task A_401_names_kcap_login_and_a_500_names_the_route() {
        _server.Given(Request.Create().WithPath("/hooks/session-start/muse-assistant").UsingPost()).RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost()).RespondWith(Response.Create().WithStatusCode(500));

        var unauthorized = await Poster().SendAsync(new MuseAssistantStartSession(Session, null, State), "chat-main", default);
        var failed       = await Poster().SendAsync(new MuseAssistantSendLines(Session.SessionId, ["{}"], [1], State), "chat-main", default);

        await Assert.That(unauthorized).Contains("kcap login");
        await Assert.That(unauthorized).Contains("401");
        await Assert.That(failed).Contains("hooks/transcript");
        await Assert.That(failed).Contains("500");
    }

    [Test]
    public async Task An_unreachable_server_is_a_refusal_not_an_exception() {
        var poster = new MuseAssistantHttpPoster(Resolutions.Of(new() { ServerUrl = "http://127.0.0.1:9" }), new FixedCapacitorHttpClient(), TimeProvider.System);

        var refused = await poster.SendAsync(new MuseAssistantEndSession(Session, 1, null, State), "chat-main", default);

        await Assert.That(refused).Contains("127.0.0.1:9");
    }

    [Test]
    public async Task A_spent_retry_budget_reports_the_last_transport_fault_on_one_line_with_a_line_break_read_as_a_space() {
        var time   = new FakeTimeProvider();
        var poster = new MuseAssistantHttpPoster(Resolutions.Of(new() { ServerUrl = "https://acme.kcap.ai" }), new FaultingHttpClient(), time);

        var sending = poster.SendAsync(new MuseAssistantEndSession(Session, 1, null, State), "chat-main", default);
        for (var i = 0; !sending.IsCompleted && i++ < 1000;) { time.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(1); }
        var refused = await sending.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(refused).Contains("retry budget");
        await Assert.That(refused).Contains("socket reset by peer second line");
        await Assert.That(refused).DoesNotContain("\n");
    }

    sealed class FaultingHttpClient : ICapacitorHttpClient {
        public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient(new FaultingHandler()));

        public Task<HttpClient>  ForCommandAsync(CancellationToken ct = default)       => throw new NotSupportedException();
        public Task<HttpClient>  ForSessionAsync(CancellationToken ct = default)       => throw new NotSupportedException();
        public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default)          => throw new NotSupportedException();
        public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default)          => throw new NotSupportedException();
        public Task<AuthAttempt> ForProtectedReadAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpClient>  ForMemoryAsync(CancellationToken ct = default)        => throw new NotSupportedException();
        public HttpClient Anonymous() => throw new NotSupportedException();
        public HttpClient Loopback()  => throw new NotSupportedException();
        public HttpClient Bearer()    => throw new NotSupportedException();
    }

    sealed class FaultingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("socket reset by peer\nsecond line");
    }
}
