using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The status probe reports which auth provider the server announces, because a server that asks
/// for none is set up without a credential. Only an answer counts: the probe never guesses a
/// provider for a server it could not ask.
/// </summary>
public sealed class StatusServerProbeTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    string Url => _server.Urls[0];

    void StubAuthConfig(int status, string body) =>
        _server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));

    Task<StatusCommand.ServerReach> ProbeAsync(string? url) =>
        StatusCommand.ProbeServerAsync(new FixedCapacitorHttpClient(), url, Config.Root, TimeProvider.System);

    [Test]
    [Arguments(AuthProvider.None)]
    [Arguments(AuthProvider.WorkOS)]
    public async Task A_server_that_answers_announces_its_provider(string provider) {
        StubAuthConfig(200, $$"""{"provider":"{{provider}}"}""");

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Reachable).IsTrue();
        await Assert.That(reach.Provider).IsEqualTo(provider);
    }

    // A dropped VPN must not turn a set-up machine into an unconfigured one, and for a server that
    // asks for no auth the provider is the only evidence of setup there is.
    [Test]
    public async Task A_failing_server_is_described_by_its_last_answer_on_disk() {
        StubAuthConfig(503, "{}");
        AuthProviderCache.Set(Url, AuthProvider.None, Config.Root, TimeProvider.System);

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Reachable).IsFalse();
        await Assert.That(reach.StatusCode).IsEqualTo(503);
        await Assert.That(reach.Provider).IsEqualTo(AuthProvider.None);
    }

    [Test]
    public async Task An_unreachable_server_is_described_by_its_last_answer_on_disk() {
        AuthProviderCache.Set(Url, AuthProvider.None, Config.Root, TimeProvider.System);
        _server.Stop();

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Reachable).IsFalse();
        await Assert.That(reach.Provider).IsEqualTo(AuthProvider.None);
    }

    // Status may be the only thing that ever asked this server, so its own answer has to be what a
    // later failed probe falls back on: nothing about setup changed in between.
    [Test]
    public async Task An_answer_is_remembered_for_a_later_outage() {
        StubAuthConfig(200, $$"""{"provider":"{{AuthProvider.None}}"}""");
        await ProbeAsync(Url);
        _server.Stop();

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Reachable).IsFalse();
        await Assert.That(reach.Provider).IsEqualTo(AuthProvider.None);
    }

    // A captive portal answers 200 for every host. It names no provider, so it must not replace
    // the one the real server announced.
    [Test]
    public async Task An_answer_that_does_not_parse_leaves_the_last_answer_alone() {
        AuthProviderCache.Set(Url, AuthProvider.None, Config.Root, TimeProvider.System);
        StubAuthConfig(200, "<html>captive portal</html>");

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Provider).IsEqualTo(AuthProvider.None);
        await Assert.That(AuthProviderCache.TryGet(Url, Config.Root, TimeProvider.System)).IsEqualTo(AuthProvider.None);
    }

    [Test]
    public async Task An_unreachable_server_nobody_ever_asked_has_no_known_provider() {
        _server.Stop();

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Reachable).IsFalse();
        await Assert.That(reach.Provider).IsNull();
    }

    [Test]
    public async Task An_answer_that_does_not_parse_is_reachable_with_no_known_provider() {
        StubAuthConfig(200, "<html>captive portal</html>");

        var reach = await ProbeAsync(Url);

        await Assert.That(reach.Reachable).IsTrue();
        await Assert.That(reach.Provider).IsNull();
    }

    [Test]
    public async Task No_server_means_nothing_is_probed() {
        var reach = await ProbeAsync(null);

        await Assert.That(reach.Url).IsNull();
        await Assert.That(reach.Provider).IsNull();
        await Assert.That(_server.LogEntries).IsEmpty();
    }
}
