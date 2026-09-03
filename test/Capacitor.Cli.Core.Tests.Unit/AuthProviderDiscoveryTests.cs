using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// <see cref="HttpClientExtensions.DiscoverProviderAsync"/>'s in-process memo. It sits in front of
/// <see cref="AuthProviderCache"/>'s on-disk store, so if it answers for the wrong server the
/// correctly-keyed disk lookup never gets a chance to.
///
/// <para><c>[NotInParallel]</c>: the memo is a process-wide static.</para>
/// </summary>
[NotInParallel]
public class AuthProviderDiscoveryTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _a = WireMockServer.Start();
    readonly WireMockServer _b = WireMockServer.Start();

    public void Dispose() {
        _a.Stop();
        _b.Stop();
        HttpClientExtensions.ResetProviderCacheForTesting();
    }

    static void StubProvider(WireMockServer server, string provider) =>
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($"{{\"provider\":\"{provider}\"}}"));

    Task<string> Discover(WireMockServer server) =>
        HttpClientExtensions.DiscoverProviderAsync(
                server.Urls[0], Config.Root, Resolutions.At(server.Urls[0], Config.Root),
                AuthFixtures.NewTokenStore(Config.Root));

    /// <summary>
    /// Two servers in one process must each get their own answer. `kcap setup` reaches this whenever
    /// auth returns a Retarget: it resolves one server, then resolves the tenant it was pointed at.
    /// </summary>
    [Test]
    public async Task Discovery_is_memoized_per_base_url() {
        HttpClientExtensions.ResetProviderCacheForTesting();
        StubProvider(_a, "WorkOS");
        StubProvider(_b, "GitHub");

        await Assert.That(await Discover(_a)).IsEqualTo("WorkOS");
        await Assert.That(await Discover(_b)).IsEqualTo("GitHub")
            .Because("a memo shared across base URLs hands the first server's provider to the second");
    }

    /// <summary>The memo must still spare a repeat call its round trip.</summary>
    [Test]
    public async Task A_repeat_discovery_against_one_server_does_not_probe_twice() {
        HttpClientExtensions.ResetProviderCacheForTesting();
        StubProvider(_a, "WorkOS");

        await Discover(_a);
        await Discover(_a);

        await Assert.That(_a.LogEntries.Count).IsEqualTo(1)
            .Because("the memo exists to skip the /auth/config round trip");
    }
}
