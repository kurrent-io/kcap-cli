using System.Net;
using System.Text;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

public class LocalServerProbeTests {
    const string None = """{"provider":"None","pull_request_reads_versions":[1]}""";

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    static HttpResponseMessage Json(HttpStatusCode status, string body, string? header = null) {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (header is not null) response.Headers.Add(LocalServerProbe.PendingHeader, header);
        return response;
    }

    static async Task<AuthConfigAnswer> Probe(Func<HttpRequestMessage, HttpResponseMessage> respond, string url = "http://localhost:9473") {
        using var http = new HttpClient(new Handler(respond));
        return await LocalServerProbe.AuthConfigAsync(http, url, CancellationToken.None);
    }

    [Test]
    public async Task Lifted_is_200_none_without_the_header() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.OK, None))).IsEqualTo(AuthConfigAnswer.Lifted);

    [Test]
    public async Task Pending_is_200_none_with_the_header() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.OK, None, "pending"))).IsEqualTo(AuthConfigAnswer.Pending);

    [Test]
    public async Task Any_other_header_value_is_other() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.OK, None, "ready"))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task A_403_is_other() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.Forbidden, None))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task A_302_is_other() =>
        await Assert.That(await Probe(_ => new HttpResponseMessage(HttpStatusCode.Found))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task Malformed_json_is_other() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.OK, "{nope"))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task A_connection_error_is_other() =>
        await Assert.That(await Probe(_ => throw new HttpRequestException("refused"))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task A_foreign_provider_is_other() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.OK, """{"provider":"workos"}"""))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task A_non_object_body_is_other() =>
        await Assert.That(await Probe(_ => Json(HttpStatusCode.OK, "[1]"))).IsEqualTo(AuthConfigAnswer.Other);

    [Test]
    public async Task The_url_is_the_base_with_the_path_appended() {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, None));
        using var http = new HttpClient(handler);

        await LocalServerProbe.AuthConfigAsync(http, "http://localhost:9473/", CancellationToken.None);

        await Assert.That(handler.Requests[0].ToString()).IsEqualTo("http://localhost:9473/auth/config");
    }

    [Test]
    public async Task Caller_cancellation_propagates() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, None)));

        var cancelled = false;

        try {
            await LocalServerProbe.AuthConfigAsync(http, "http://localhost:9473", cts.Token);
        }
        catch (OperationCanceledException) {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();
    }
}
