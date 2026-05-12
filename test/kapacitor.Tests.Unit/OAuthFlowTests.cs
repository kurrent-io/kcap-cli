using kapacitor.Auth;

namespace kapacitor.Tests.Unit;

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
    public async Task Callback_parser_rejects_error_without_valid_state() {
        var result = OAuthLoginFlow.ParseCallback(
            queryString:  "?error=access_denied",
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
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: true, isHeadless: false);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_device_when_headless() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: true);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Device);
    }

    [Test]
    public async Task ChooseGitHubFlow_returns_browser_when_interactive() {
        var choice = OAuthLoginFlow.ChooseGitHubFlow(forceDevice: false, isHeadless: false);
        await Assert.That(choice).IsEqualTo(GitHubFlow.Browser);
    }
}
