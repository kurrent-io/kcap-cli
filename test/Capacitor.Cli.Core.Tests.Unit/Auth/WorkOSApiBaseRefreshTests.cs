using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// A refresh re-applies the loopback rule to the token's own <c>server_url</c>, so a stored
/// <c>workos_api_base</c> is honoured only where a login could have stamped it.
/// </summary>
public class WorkOSApiBaseRefreshTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static StoredTokens Expired(string? serverUrl, string? apiBase) => new() {
        AccessToken    = "stale",
        RefreshToken   = "rt",
        ClientId       = "cid",
        ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(-1),
        GitHubUsername = "alice",
        Provider       = AuthProvider.WorkOS,
        ServerUrl      = serverUrl,
        WorkOSApiBase  = apiBase
    };

    [Test]
    public async Task A_loopback_token_refreshes_against_its_own_base() {
        using var workos = WireMockServer.Start();
        workos.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"access_token":"fresh","refresh_token":"rotated"}"""));

        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("default", Expired("http://127.0.0.1:5108", workos.Urls[0]));

        var refreshed = await store.GetValidTokensForProfileAsync("default");

        await Assert.That(refreshed!.AccessToken).IsEqualTo("fresh");
        await Assert.That(workos.LogEntries.Count).IsEqualTo(1);

        var persisted = await store.LoadAsync("default");
        await Assert.That(persisted!.WorkOSApiBase).IsEqualTo(workos.Urls[0]);
    }

    [Test]
    [Arguments("http://127.0.0.1:5108", "https://workos.attacker.example")]
    [Arguments("https://acme.kcap.ai", "http://127.0.0.1:4100")]
    [Arguments(null, "http://127.0.0.1:4100")]
    public async Task Any_other_stored_base_refreshes_against_production(string? serverUrl, string apiBase) {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Json("""{"access_token":"fresh","refresh_token":"rotated"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired(serverUrl, apiBase));

        await store.GetValidTokensForProfileAsync("default");

        await Assert.That(script.Seen).IsEquivalentTo(["POST https://api.workos.com/user_management/authenticate"]);
    }

    [Test]
    public async Task A_production_token_file_carries_no_base_field() {
        var store = AuthFixtures.NewTokenStore(Config.Root);
        await store.SaveAsync("default", Expired("https://acme.kcap.ai", apiBase: null));

        var json = await File.ReadAllTextAsync(Config.PathTo("tokens", "default.json"));

        await Assert.That(json).DoesNotContain("workos_api_base");
    }
}
