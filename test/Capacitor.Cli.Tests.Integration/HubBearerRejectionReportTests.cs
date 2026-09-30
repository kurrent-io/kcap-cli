using System.Text;
using Capacitor.Cli.Core.Http;
using Microsoft.AspNetCore.SignalR.Client;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Installed through <c>HttpMessageHandlerFactory</c>, the report handler sits inside SignalR's own
/// token handler, so a refused negotiate is reported with the bearer SignalR actually sent. If the
/// handler sat outside it, every report would read "no bearer sent" and hide the one fact it exists for.
/// The WebSocket upgrade bypasses every message handler, so it is reported by the WebSocket factory.
/// </summary>
public class HubBearerRejectionReportTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    static string Token(string sub) {
        static string B64Url(string s) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return $"{B64Url("""{"alg":"RS256"}""")}.{B64Url($$"""{"sub":"{{sub}}","exp":1790000000}""")}.sig";
    }

    [Test]
    public async Task A_refused_negotiate_is_reported_with_the_bearer_signalr_sent() {
        _server.Given(Request.Create().WithPath("/hubs/sessions/negotiate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":"invalid_token","message":"Invalid"}"""));

        var reports = new List<string>();

        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{_server.Url}/hubs/sessions", options => {
                options.AccessTokenProvider = () => Task.FromResult<string?>(Token("user_hub"));
                options.HttpMessageHandlerFactory = inner =>
                    new BearerRejectionReportHandler(TimeProvider.System, r => { lock (reports) reports.Add(r); }) { InnerHandler = inner };
            })
            .Build();

        try {
            await hub.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        } catch (Exception) { }

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("server error=invalid_token; bearer sub=user_hub");
    }

    [Test]
    public async Task A_refused_websocket_upgrade_is_reported_with_the_bearer_sent() {
        _server.Given(Request.Create().WithPath("/hubs/sessions/negotiate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"negotiateVersion":1,"connectionId":"c1","connectionToken":"t1","availableTransports":[{"transport":"WebSockets","transferFormats":["Text","Binary"]}]}"""));
        _server.Given(Request.Create().WithPath("/hubs/sessions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(401));

        var reports = new List<string>();
        void Report(string r) { lock (reports) reports.Add(r); }

        await using var hub = new HubConnectionBuilder()
            .WithUrl($"{_server.Url}/hubs/sessions", options => {
                options.AccessTokenProvider = () => Task.FromResult<string?>(Token("user_ws"));
                options.HttpMessageHandlerFactory = inner =>
                    new BearerRejectionReportHandler(TimeProvider.System, Report) { InnerHandler = inner };
                options.WebSocketFactory = (context, ct) => BearerRejectionReportHandler.ConnectWebSocketAsync(
                    context.Uri, context.Options.AccessTokenProvider, TimeProvider.System, Report, ct);
            })
            .Build();

        try {
            await hub.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        } catch (Exception) { }

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("401 from GET /hubs/sessions: server error=-; bearer sub=user_ws");

        var upgrade = _server.LogEntries.Single(e => e.RequestMessage.Method == "GET");

        await Assert.That(upgrade.RequestMessage.Headers!["Authorization"].Single()).IsEqualTo("Bearer " + Token("user_ws"))
            .Because("the factory replaces SignalR's own, so it must still send the bearer");
        await Assert.That(upgrade.RequestMessage.Headers!["X-Requested-With"].Single()).IsEqualTo("XMLHttpRequest");
    }
}
