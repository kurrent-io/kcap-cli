using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

/// <summary>Only an answer the kcap server never gives reads as another program; a refusal, a silence or any answer
/// the server could give says nothing about who holds the port.</summary>
public class LocalPortProbeTests {
    sealed class Answering(Func<CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => answer(ct);
    }

    static Task<LocalPortAnswer> Probe(Func<CancellationToken, Task<HttpResponseMessage>> answer, TimeProvider? time = null) =>
        LocalPortProbe.ReadAsync(new HttpClient(new Answering(answer)), 9473, time ?? TimeProvider.System, CancellationToken.None);

    static Task<LocalPortAnswer> Probe(HttpResponseMessage response) => Probe(_ => Task.FromResult(response));

    [Test]
    [Arguments(HttpStatusCode.OK)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task An_answer_the_server_can_give_is_not_evidence_of_another_program(HttpStatusCode status) =>
        await Assert.That(await Probe(new HttpResponseMessage(status))).IsEqualTo(LocalPortAnswer.Answered.Instance);

    [Test]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Found)]
    [Arguments(HttpStatusCode.NoContent)]
    public async Task A_status_the_health_route_never_returns_is_another_programs(HttpStatusCode status) =>
        await Assert.That(await Probe(new HttpResponseMessage(status)))
            .IsEqualTo(new LocalPortAnswer.Foreign($"a web server that answers HTTP {(int)status}"));

    [Test]
    public async Task A_foreign_answer_is_named_by_its_server_header() {
        var response = new HttpResponseMessage(HttpStatusCode.NotFound);
        response.Headers.TryAddWithoutValidation("Server", "SimpleHTTP/0.6 Python/3.12.1");

        await Assert.That(await Probe(response)).IsEqualTo(new LocalPortAnswer.Foreign("a web server that calls itself SimpleHTTP/0.6 Python/3.12.1"));
    }

    [Test]
    public async Task A_refused_connection_is_nothing_listening() =>
        await Assert.That(await Probe(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused",
                new SocketException((int)SocketError.ConnectionRefused))))
            .IsEqualTo(LocalPortAnswer.Refused.Instance);

    [Test]
    public async Task A_connection_that_ends_without_an_answer_is_silent() =>
        await Assert.That(await Probe(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.")))
            .IsTypeOf<LocalPortAnswer.Silent>();

    [Test]
    public async Task No_answer_in_time_is_silent() {
        var time = new FakeTimeProvider();
        var probe = Probe(async ct => {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, time);

        time.Advance(LocalPortProbe.RequestTimeout);

        await Assert.That(await probe).IsTypeOf<LocalPortAnswer.Silent>();
    }

    /// <summary>A real listener that answers with something other than HTTP, as an SSH server or a raw socket does.</summary>
    [Test]
    public async Task A_listener_that_does_not_speak_http_is_another_program() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var serve = Task.Run(async () => {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                await stream.WriteAsync("SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray());
                await stream.FlushAsync();
                await Task.Delay(TimeSpan.FromSeconds(1));
            });

            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var answer = await LocalPortProbe.ReadAsync(http, port, TimeProvider.System, CancellationToken.None);

            await Assert.That(answer).IsEqualTo(new LocalPortAnswer.Foreign("a program that does not speak HTTP"));
            await serve;
        } finally {
            listener.Stop();
        }
    }
}
