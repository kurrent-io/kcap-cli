using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

public class ServerCliSetupReportTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Posts_the_report_with_the_signed_in_bearer() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/me/cli-setup").UsingPost()
                  .WithHeader("Authorization", "Bearer tok")
                  .WithBody(b => b!.Contains("\"cliVersion\"") && !b.Contains("cli_version")))
              .RespondWith(Response.Create().WithStatusCode(204));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var reported = await ServerCliSetupReport.Sender(http, profiles, "1.2.3")(CancellationToken.None);

        await Assert.That(reported).IsTrue();
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/api/users/me/cli-setup")).IsEqualTo(1);
    }

    [Test]
    public async Task A_rejected_report_is_a_miss_not_a_throw() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/me/cli-setup").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(500));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var reported = await ServerCliSetupReport.Sender(http, profiles, "1.2.3")(CancellationToken.None);

        await Assert.That(reported).IsFalse();
    }

    [Test]
    public async Task An_unreachable_server_is_a_miss_not_a_throw() {
        var server = WireMockServer.Start();
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        server.Stop();
        server.Dispose();

        var reported = await ServerCliSetupReport.Sender(http, profiles, "1.2.3")(CancellationToken.None);

        await Assert.That(reported).IsFalse();
    }

    [Test]
    public async Task A_no_auth_server_gets_the_report_for_its_local_user() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/me/cli-setup").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(204));

        var reported = await ServerCliSetupReport.Sender(
            new NoAuthLane(), Resolutions.At(server.Url!, Config.Root), "1.2.3")(CancellationToken.None);

        await Assert.That(reported).IsTrue();
    }

    [Test]
    public async Task Without_a_server_or_client_nothing_is_sent() {
        var reported = await ServerCliSetupReport.Sender(null, null, "1.2.3")(CancellationToken.None);

        await Assert.That(reported).IsFalse();
    }

    sealed class NoAuthLane : ICapacitorHttpClient {
        public Task<AuthAttempt> ForWaitAsync(CancellationToken ct = default) =>
            Task.FromResult(new AuthAttempt(new HttpClient(), AuthStatus.NoAuthRequired, null, null));

        public Task<HttpClient> ForCommandAsync(CancellationToken ct = default)    => throw new NotSupportedException();
        public Task<HttpClient> ForBackgroundAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpClient> ForSessionAsync(CancellationToken ct = default)    => throw new NotSupportedException();
        public Task<AuthAttempt> ForHookAsync(CancellationToken ct = default)      => throw new NotSupportedException();
        public Task<AuthAttempt> ForProtectedReadAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HttpClient> ForMemoryAsync(CancellationToken ct = default)     => throw new NotSupportedException();
        public HttpClient Anonymous() => throw new NotSupportedException();
        public HttpClient Loopback()  => throw new NotSupportedException();
        public HttpClient Bearer()    => throw new NotSupportedException();
    }
}
