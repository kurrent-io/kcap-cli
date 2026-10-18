using System.Net;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The refreshes run on a client the store is handed, never one it builds. A store that makes its
/// own reaches the real WorkOS host from a unit test, and the only way to tell the two apart is to
/// hand it a client and see whether the request arrives.
/// </summary>
public class TokenStoreRefreshLaneTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static StoredTokens Expired => new() {
        AccessToken    = "stale",
        RefreshToken   = "rt",
        ClientId       = "cid",
        ExpiresAt      = DateTimeOffset.UtcNow.AddMinutes(-1),
        GitHubUsername = "alice",
        Provider       = AuthProvider.WorkOS
    };

    [Test]
    public async Task The_workos_refresh_reaches_the_client_it_was_given() {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Json("""{"access_token":"fresh","refresh_token":"rotated"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);

        var refreshed = await store.GetValidTokensForProfileAsync("default");

        await Assert.That(script.Seen)
            .Contains("POST https://api.workos.com/user_management/authenticate");
        await Assert.That(refreshed!.AccessToken).IsEqualTo("fresh");
    }

    /// The rotated refresh token is persisted, not just returned: WorkOS spends the old one on use,
    /// so a store that returned the new pair without writing it would re-send a consumed token.
    [Test]
    public async Task The_rotated_refresh_token_is_written_back() {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Json("""{"access_token":"fresh","refresh_token":"rotated"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);

        await store.GetValidTokensForProfileAsync("default");

        var persisted = await store.LoadAsync("default");
        await Assert.That(persisted!.RefreshToken).IsEqualTo("rotated");
    }

    /// A refresh token WorkOS refused is retired on disk, so the next read reports Expired without
    /// presenting it again, while the identity stays for `kcap status` to name.
    [Test]
    public async Task A_refused_refresh_token_is_retired_and_never_sent_again() {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Status(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);

        await Assert.That(await store.GetValidTokensForProfileAsync("default")).IsNull();

        var later = await store.GetValidTokensForServerAsync("default", "https://kcap.example.com");

        await Assert.That(later.Status).IsEqualTo(AuthStatus.Expired);
        await Assert.That(script.Seen.Count(s => s.EndsWith("/user_management/authenticate", StringComparison.Ordinal))).IsEqualTo(1);

        var persisted = await store.LoadAsync("default");
        await Assert.That(persisted!.RefreshToken).IsNull();
        await Assert.That(persisted.GitHubUsername).IsEqualTo("alice");
    }

    /// Once WorkOS has refused the token, the retirement is written even if the caller cancels:
    /// a cancelled write would leave the refused token on disk to be sent again.
    [Test]
    public async Task A_refused_refresh_token_is_retired_when_the_caller_cancels_after_the_refusal() {
        using var cts    = new CancellationTokenSource();
        using var script = new AuthHttpScript(_ => {
            cts.Cancel();

            return AuthHttp.Status(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        });
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);

        try {
            await store.ForceRefreshAsync("default", "stale", ct: cts.Token);
        } catch (OperationCanceledException) { }

        await Assert.That(script.Seen.Count).IsEqualTo(1);
        await Assert.That((await store.LoadAsync("default"))!.RefreshToken).IsNull();
    }

    /// A reader that saw a refresh token before the lock, and finds it retired by a peer once it holds
    /// the lock, reports the token expired without sending anything.
    [Test]
    public async Task A_reader_queued_behind_a_retirement_sends_nothing() {
        using var script = new AuthHttpScript(
            _ => AuthHttp.Json("""{"access_token":"fresh","refresh_token":"rotated"}"""));
        var store = AuthFixtures.NewTokenStore(Config.Root, script);
        await store.SaveAsync("default", Expired);
        var tokenFile = store.TokenPath("default");
        var retired   = (await File.ReadAllTextAsync(tokenFile)).Replace("\"rt\"", "null", StringComparison.Ordinal);

        Task<StoredTokens?> reader;
        using (new FileStream(Path.ChangeExtension(tokenFile, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
            reader = store.GetValidTokensForProfileAsync("default");
            await Task.Delay(200);
            await File.WriteAllTextAsync(tokenFile, retired);
        }

        await Assert.That(await reader).IsNull();
        await Assert.That(script.Seen).IsEmpty();
    }
}
