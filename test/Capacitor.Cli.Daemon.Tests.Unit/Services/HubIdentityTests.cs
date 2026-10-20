using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The identity a hub connection signs in as is the account and the server that issued its token,
/// not the token itself: a refresh changes the token and keeps the identity.</summary>
public class HubIdentityTests {
    static CredentialState Ok(string account, string token = "t1", string? server = "https://a.example") =>
        new(token, AuthStatus.Ok, new TokenResolution(
            new StoredTokens { AccessToken = token, ExpiresAt = DateTimeOffset.UnixEpoch, GitHubUsername = account, ServerUrl = server },
            AuthStatus.Ok, server, "default"));

    [Test]
    public async Task A_refreshed_token_for_the_same_account_is_the_same_identity() =>
        await Assert.That(HubIdentity.Of(Ok("ana", "t2"))).IsEqualTo(HubIdentity.Of(Ok("ana", "t1")));

    [Test]
    [Arguments("bob", "https://a.example")]
    [Arguments("ana", "https://b.example")]
    public async Task Another_account_or_issuing_server_is_another_identity(string account, string server) =>
        await Assert.That(HubIdentity.Of(Ok(account, server: server))).IsNotEqualTo(HubIdentity.Of(Ok("ana")));

    [Test]
    public async Task A_bearer_from_no_stored_token_has_no_identity() {
        await Assert.That(HubIdentity.Of(new CredentialState("machine", AuthStatus.Ok))).IsNull();
        await Assert.That(HubIdentity.Of(new CredentialState(null, AuthStatus.Expired, Ok("ana").Resolution))).IsNull();
    }

    /// <summary>A sign-out or a token for another server ends the old identity too; an expired token does
    /// not, because a refresh that only failed for want of a network looks the same.</summary>
    [Test]
    [Arguments(AuthStatus.NotAuthenticated, true)]
    [Arguments(AuthStatus.WrongServer, true)]
    [Arguments(AuthStatus.Expired, false)]
    [Arguments(AuthStatus.UnusableServerUrl, false)]
    public async Task A_lost_sign_in_ends_the_negotiated_identity_unless_it_merely_expired(AuthStatus status, bool changed) =>
        await Assert.That(HubIdentity.Changed(HubIdentity.Of(Ok("ana")), new CredentialState(null, status))).IsEqualTo(changed);

    [Test]
    public async Task Only_a_hub_that_negotiated_with_a_stored_token_can_change() {
        await Assert.That(HubIdentity.Changed(null, Ok("bob"))).IsFalse();
        await Assert.That(HubIdentity.Changed(HubIdentity.Of(Ok("ana")), Ok("ana", "t2"))).IsFalse();
        await Assert.That(HubIdentity.Changed(HubIdentity.Of(Ok("ana")), Ok("bob"))).IsTrue();
    }
}
