using System.Text.Json;
using Capacitor.Cli.Core.MessageSenders;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.MessageSenders;

/// The message-senders route, the body it posts, and the status each outcome totalizes to.
public class MessageSendersClientTests {
    const string Dashed   = "01234567-89ab-cdef-0123-456789abcdef";
    const string Dashless = "0123456789abcdef0123456789abcdef";

    sealed class ThrowingHandler(Exception exception) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    sealed class CancellingHandler : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    static WireMockServer Serve(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/sessions/{Dashless}/message-senders").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
        return server;
    }

    static readonly MessageSendersRequest AnyKey = new() { EventNumbers = [12] };

    [Test]
    public async Task Posts_every_key_list_and_parses_each_sender_with_its_keys() {
        var eventId = Guid.NewGuid();
        var dispatchId = Guid.NewGuid();
        using var server = Serve(200, $$"""
            {"senders":[
              {"event_number":12,"event_id":"{{eventId}}","dispatch_id":null,"sender_user_id":"github:1","sender_name":"Tony","avatar_url":"https://a/1","sent_by_caller":false},
              {"event_number":null,"event_id":null,"dispatch_id":"{{dispatchId}}","sender_user_id":"github:2","sender_name":null,"avatar_url":null,"sent_by_caller":true}]}
            """);
        using var http = new HttpClient();

        var read = await new MessageSendersClient(http, server.Urls[0] + "/").ReadAsync(Dashed,
            new MessageSendersRequest { EventIds = [eventId], EventNumbers = [12] }, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(MessageSendersReadKind.Ready);
        await Assert.That(read.Senders.Count).IsEqualTo(2);
        await Assert.That(read.Senders[0].EventNumber).IsEqualTo(12L);
        await Assert.That(read.Senders[0].EventId).IsEqualTo(eventId);
        await Assert.That(read.Senders[0].SenderName).IsEqualTo("Tony");
        await Assert.That(read.Senders[0].SentByCaller).IsFalse();
        await Assert.That(read.Senders[1].EventNumber).IsNull();
        await Assert.That(read.Senders[1].DispatchId).IsEqualTo(dispatchId);
        await Assert.That(read.Senders[1].SenderName).IsNull();
        await Assert.That(read.Senders[1].SentByCaller).IsTrue();

        using var body = JsonDocument.Parse(server.LogEntries.Single().RequestMessage.Body!);
        await Assert.That(body.RootElement.GetProperty("event_ids")[0].GetGuid()).IsEqualTo(eventId);
        await Assert.That(body.RootElement.GetProperty("event_numbers")[0].GetInt64()).IsEqualTo(12L);
        await Assert.That(body.RootElement.GetProperty("dispatch_ids").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    [Arguments(401, MessageSendersReadKind.SignedOut)]
    [Arguments(403, MessageSendersReadKind.Unavailable)]
    [Arguments(404, MessageSendersReadKind.Unavailable)]
    [Arguments(500, MessageSendersReadKind.Unreachable)]
    [Arguments(503, MessageSendersReadKind.Unreachable)]
    public async Task A_refusal_totalizes_by_status(int status, MessageSendersReadKind expected) {
        using var server = Serve(status, """{"error":"nope"}""");
        using var http = new HttpClient();

        var read = await new MessageSendersClient(http, server.Urls[0]).ReadAsync(Dashed, AnyKey, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(expected);
        await Assert.That(read.Senders.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_success_that_does_not_parse_is_unreachable() {
        using var server = Serve(200, "<html>proxy</html>");
        using var http = new HttpClient();

        var read = await new MessageSendersClient(http, server.Urls[0]).ReadAsync(Dashed, AnyKey, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(MessageSendersReadKind.Unreachable);
    }

    [Test]
    [Arguments("""{"senders":null}""")]
    [Arguments("{}")]
    public async Task A_success_without_a_sender_list_is_unreachable(string body) {
        using var server = Serve(200, body);
        using var http = new HttpClient();

        var read = await new MessageSendersClient(http, server.Urls[0]).ReadAsync(Dashed, AnyKey, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(MessageSendersReadKind.Unreachable);
    }

    [Test]
    public async Task A_transport_failure_is_unreachable() {
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        var read = await new MessageSendersClient(http, "http://localhost:1").ReadAsync(Dashed, AnyKey, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(MessageSendersReadKind.Unreachable);
    }

    [Test]
    [Arguments("..")]
    [Arguments("")]
    public async Task An_id_that_would_escape_or_empty_the_route_is_refused_before_any_request(string id) {
        using var http = new HttpClient(new ThrowingHandler(new InvalidOperationException("a request was sent")));

        var read = await new MessageSendersClient(http, "http://localhost:1").ReadAsync(id, AnyKey, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(MessageSendersReadKind.Unavailable);
    }

    [Test]
    public async Task The_callers_own_cancellation_propagates_instead_of_reading_as_an_outage() {
        using var http = new HttpClient(new CancellingHandler());
        using var cts = new CancellationTokenSource();
        var pending = new MessageSendersClient(http, "http://localhost:1").ReadAsync(Dashed, AnyKey, cts.Token);

        await cts.CancelAsync();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }
}
