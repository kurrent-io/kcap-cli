using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit;

public class WhoAmIRendererTests {
    static StoredTokens MakeTokens(string accountType, string accountLogin, string gitHubUsername = "alice") =>
        new() {
            AccessToken    = "tok",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(23),
            GitHubUsername = gitHubUsername,
            Provider       = "GitHubApp",
            AccountType    = accountType,
            AccountLogin   = accountLogin
        };

    // ── RenderAccountLine ───────────────────────────────────────────────────

    [Test]
    public async Task RenderAccountLine_user_tenant_shows_personal_account_label() {
        var tokens = MakeTokens("User", "alice");
        var line   = WhoAmIRenderer.RenderAccountLine(tokens);
        await Assert.That(line).IsEqualTo("Personal account: @alice");
    }

    [Test]
    public async Task RenderAccountLine_org_tenant_shows_organization_label() {
        var tokens = MakeTokens("Organization", "acme");
        var line   = WhoAmIRenderer.RenderAccountLine(tokens);
        await Assert.That(line).IsEqualTo("Organization: acme");
    }

    [Test]
    public async Task RenderAccountLine_unknown_type_falls_back_to_username_label() {
        var tokens = MakeTokens("", "", "bob");
        var line   = WhoAmIRenderer.RenderAccountLine(tokens);
        await Assert.That(line).IsEqualTo("Username: bob");
    }

    [Test]
    public async Task RenderAccountLine_user_with_empty_account_login_falls_back_to_github_username() {
        var tokens = MakeTokens("User", "", "charlie");
        var line   = WhoAmIRenderer.RenderAccountLine(tokens);
        await Assert.That(line).IsEqualTo("Personal account: @charlie");
    }

    [Test]
    public async Task RenderAccountLine_org_with_empty_account_login_falls_back_to_github_username() {
        var tokens = MakeTokens("Organization", "", "myorg");
        var line   = WhoAmIRenderer.RenderAccountLine(tokens);
        await Assert.That(line).IsEqualTo("Organization: myorg");
    }

    // ── Render (full output) ────────────────────────────────────────────────

    [Test]
    public async Task Render_user_tenant_contains_personal_account_line() {
        var tokens = MakeTokens("User", "alice");
        var output = WhoAmIRenderer.Render(tokens, "https://kapacitor.alice.example");
        await Assert.That(output).Contains("Personal account: @alice");
    }

    [Test]
    public async Task Render_org_tenant_contains_organization_line() {
        var tokens = MakeTokens("Organization", "acme");
        var output = WhoAmIRenderer.Render(tokens, "https://kapacitor.acme.example");
        await Assert.That(output).Contains("Organization: acme");
    }

    [Test]
    public async Task Render_includes_provider_and_server() {
        var tokens = MakeTokens("User", "alice");
        var output = WhoAmIRenderer.Render(tokens, "https://kapacitor.alice.example");
        await Assert.That(output).Contains("Provider: GitHubApp");
        await Assert.That(output).Contains("Server: https://kapacitor.alice.example");
    }

    [Test]
    public async Task Render_shows_token_expiry_text() {
        var tokens = MakeTokens("Organization", "acme");
        var output = WhoAmIRenderer.Render(tokens, "https://kapacitor.acme.example");
        await Assert.That(output).Contains("Token expires in");
    }
}
