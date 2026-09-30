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
        } catch (Exception) {
            // Refused at negotiate, as stubbed.
        }

        await Assert.That(reports).Count().IsEqualTo(1);
        await Assert.That(reports[0]).Contains("server error=invalid_token; bearer sub=user_hub");
    }
}
