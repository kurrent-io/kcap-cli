using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

public class OAuthFlowTests {
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
