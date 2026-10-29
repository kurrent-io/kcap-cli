using System.Buffers.Text;
using System.Text;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>The canonical user id comes from the stored access token, never from a network call, and
/// matches what the server's own user-id resolution yields for that bearer: <c>github:{github_id}</c>
/// for a GitHub-issued token, the subject for a WorkOS one.</summary>
public class MuseAssistantIdentityTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string Token(string payloadJson) =>
        $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson))}.sig";

    [Test]
    public async Task A_github_token_maps_its_github_id_to_the_canonical_id_and_a_workos_token_keeps_its_subject() {
        await Assert.That(MuseAssistantIdentity.UserIdOf(Token("""{"sub":"github|42","github_id":"42","github_username":"tony"}"""))).IsEqualTo("github:42");
        await Assert.That(MuseAssistantIdentity.UserIdOf(AccessTokens.ForSubject("user_01HXYZ"))).IsEqualTo("user_01HXYZ");
    }

    [Test]
    public async Task A_token_naming_nobody_or_not_a_jwt_has_no_user() {
        await Assert.That(MuseAssistantIdentity.UserIdOf(Token("""{"exp":1}"""))).IsNull();
        await Assert.That(MuseAssistantIdentity.UserIdOf("not-a-jwt")).IsNull();
    }

    [Test]
    public async Task Resolves_the_normalized_server_and_the_signed_in_user() {
        var profiles = Resolutions.At("https://acme.kcap.ai/", Config.Root);
        var tokens   = AuthFixtures.NewTokenStore(Config.Root);
        await tokens.SaveAsync(profiles.Name, new StoredTokens {
            AccessToken = AccessTokens.ForSubject("user_01HXYZ"), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "tony", Provider = "WorkOS" });

        var (principal, problem) = await new MuseAssistantIdentity(profiles, tokens).ResolveAsync(default);

        await Assert.That(problem).IsNull();
        await Assert.That(principal).IsEqualTo(new MuseAssistantPrincipal("https://acme.kcap.ai", "user_01HXYZ"));
    }

    [Test]
    public async Task Not_signed_in_names_kcap_login_and_no_server_names_setup() {
        var signedOut = await new MuseAssistantIdentity(Resolutions.At("https://acme.kcap.ai", Config.Root), AuthFixtures.NewTokenStore(Config.Root)).ResolveAsync(default);
        var noServer  = await new MuseAssistantIdentity(Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root)).ResolveAsync(default);

        await Assert.That(signedOut.Principal).IsNull();
        await Assert.That(signedOut.Problem).Contains("kcap login");
        await Assert.That(noServer.Principal).IsNull();
        await Assert.That(noServer.Problem).Contains("kcap setup");
    }
}
