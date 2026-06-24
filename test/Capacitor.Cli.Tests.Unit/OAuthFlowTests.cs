using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class OAuthFlowTests {
    [Test]
    public async Task SwitchWorkOSOrg_posts_refresh_grant_with_org_and_returns_token() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x"},"organization_id":"org_a","access_token":"acc","refresh_token":"rt2"}"""));
        using var http = new HttpClient();

        var auth = await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, server.Urls[0], "client_d", "rt1", "org_a");

        await Assert.That(auth!.OrganizationId).IsEqualTo("org_a");
        await Assert.That(auth.RefreshToken).IsEqualTo("rt2");
        await Assert.That(auth.AccessToken).IsEqualTo("acc");
    }

    [Test]
    public async Task SwitchWorkOSOrg_returns_null_on_error() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401));
        using var http = new HttpClient();

        var auth = await OAuthLoginFlow.SwitchWorkOSOrgAsync(http, server.Urls[0], "client_d", "rt1", "org_a");

        await Assert.That(auth).IsNull();
    }

    [Test]
    public async Task GitHub_authorize_url_includes_all_required_params() {
        var url = OAuthLoginFlow.BuildGitHubAuthorizeUrl(
            clientId:     "Iv1.abc",
            redirectUri:  "http://127.0.0.1:54321/callback",
            state:        "state-xyz",
            codeChallenge:"challenge-123");

        await Assert.That(url).StartsWith("https://github.com/login/oauth/authorize?");
        await Assert.That(url).Contains("client_id=Iv1.abc");
        await Assert.That(url).Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A54321%2Fcallback");
        await Assert.That(url).Contains("state=state-xyz");
        await Assert.That(url).Contains("scope=read%3Auser%20read%3Aorg");
        await Assert.That(url).Contains("code_challenge=challenge-123");
        await Assert.That(url).Contains("code_challenge_method=S256");
        await Assert.That(url).Contains("response_type=code");
    }

    [Test]
    public async Task Callback_parser_returns_code_when_state_matches() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?code=abc&state=expected",
            expectedState:"expected");

        await Assert.That(result.Code).IsEqualTo("abc");
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task Callback_parser_rejects_state_mismatch() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?code=abc&state=attacker",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("state_mismatch");
    }

    [Test]
    public async Task Callback_parser_surfaces_provider_error() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?error=access_denied&state=expected",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("access_denied");
    }

    [Test]
    public async Task Callback_parser_reports_missing_state_when_state_param_absent() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?error=access_denied",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("missing_state");
    }

    [Test]
    public async Task Callback_parser_reports_state_mismatch_when_state_differs() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?error=access_denied&state=attacker",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("state_mismatch");
    }

    [Test]
    public async Task Callback_parser_rejects_missing_code() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?state=expected",
            expectedState:"expected");

        await Assert.That(result.Code).IsNull();
        await Assert.That(result.Error).IsEqualTo("missing_code");
    }

    [Test]
    public async Task ChooseDiscoveryProvider_honors_flags_and_default() {
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--github"], isInteractive: true)).IsEqualTo(AuthProvider.GitHubApp);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider(["--workos"], isInteractive: true)).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: false)).IsEqualTo(AuthProvider.WorkOS);
        await Assert.That(OAuthLoginFlow.ChooseDiscoveryProvider([], isInteractive: true)).IsNull();
    }

    [Test]
    public async Task ShouldDiscoverLogin_true_when_no_server_or_discover_flag() {
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin(null, [])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin(null, ["--device"])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin("https://x.example", ["--discover"])).IsTrue();
        await Assert.That(OAuthLoginFlow.ShouldDiscoverLogin("https://x.example", [])).IsFalse();
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_forced() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: true, isHeadless: false, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_headless() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: true, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_no_exchange_url() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false, hasExchangeUrl: false);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task IsValidExchangeUrl_accepts_https_url() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("https://auth.example/auth/github/code-exchange")).IsTrue();
    }

    [Test]
    public async Task IsValidExchangeUrl_accepts_http_url() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("http://localhost:8080/exchange")).IsTrue();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_null() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl(null)).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_empty_and_whitespace() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("")).IsFalse();
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("   ")).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_relative_path() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("/auth/exchange")).IsFalse();
    }

    [Test]
    public async Task IsValidExchangeUrl_rejects_non_http_scheme() {
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("javascript:alert(1)")).IsFalse();
        await Assert.That(OAuthLoginFlow.IsValidExchangeUrl("file:///etc/passwd")).IsFalse();
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_browser_when_interactive_and_server_supports_it() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false, hasExchangeUrl: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Browser);
    }
}
