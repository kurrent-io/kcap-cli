using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// What <c>kcap status</c> says this CLI authenticates as, against the real token store. The auth
/// state is what <c>configured</c> is decided from, so a state the credential lane would not act
/// on is a wrong answer about whether setup still has to run.
/// </summary>
public class StatusAuthResolutionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Profile = "default";
    const string Server  = "https://acme.kcap.ai";

    static readonly StatusCommand.ServerReach Reached = new(Server, true, null);

    TokenStore Tokens => AuthFixtures.NewTokenStore(Config.Root);

    // WorkOS with no refresh token: an expired one has no refresh lane, so nothing here reaches
    // for the network.
    Task SaveTokenAsync(string? issuedBy, TimeSpan expiresIn) =>
        Tokens.SaveAsync(Profile, new StoredTokens {
            AccessToken    = "tok",
            ExpiresAt      = DateTimeOffset.UtcNow + expiresIn,
            GitHubUsername = "alice",
            Provider       = AuthProvider.WorkOS,
            ServerUrl      = issuedBy,
        });

    Task<StatusCommand.AuthSnapshot> ResolveAsync(MachineAuth? machine = null, StatusCommand.ServerReach? server = null) =>
        StatusCommand.ResolveAuthAsync(Tokens, machine ?? MachineAuth.None, Profile, server ?? Reached);

    // A server announcing no auth provider is usable as it stands, which is the first thing the
    // credential lane checks — so neither an empty token store nor a machine credential changes it.
    [Test]
    public async Task A_server_that_asks_for_no_auth_needs_no_credential() {
        var auth = await ResolveAsync(server: Reached with { Provider = AuthProvider.None });

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.NotRequired);
    }

    [Test]
    public async Task No_auth_outranks_a_machine_credential() {
        var auth = await ResolveAsync(
            new MachineAuth("client-id", "client-secret", null), Reached with { Provider = AuthProvider.None });

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.NotRequired);
    }

    // An unanswered probe knows nothing about the provider. Reading that as "no auth" would call
    // every unreachable server with an empty token store set up.
    [Test]
    public async Task An_unknown_provider_is_never_read_as_no_auth() {
        var auth = await ResolveAsync(server: new StatusCommand.ServerReach(Server, false, null, Provider: null));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.None);
    }

    [Test]
    [Arguments("client-id", null)]
    [Arguments(null, "client-secret")]
    public async Task One_machine_variable_is_incomplete_and_never_machine(string? id, string? secret) {
        var auth = await ResolveAsync(new MachineAuth(id, secret, null));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.MachineIncomplete);
        await Assert.That(auth.MachineLine).Contains("incomplete");
    }

    [Test]
    public async Task Both_machine_variables_replace_the_token_store_answer() {
        await SaveTokenAsync(Server, TimeSpan.FromHours(1));

        var auth = await ResolveAsync(new MachineAuth("client-id", "client-secret", null));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.Machine);
        await Assert.That(auth.Identity).IsNull();
    }

    [Test]
    public async Task A_token_bound_to_this_server_is_valid() {
        await SaveTokenAsync(Server, TimeSpan.FromHours(1));

        var auth = await ResolveAsync();

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.Valid);
        await Assert.That(auth.Identity).IsEqualTo("alice");
        await Assert.That(auth.ExpiresAt).IsNotNull();
    }

    [Test]
    public async Task A_token_issued_by_another_server_is_not_valid() {
        await SaveTokenAsync("https://other.kcap.ai", TimeSpan.FromHours(1));

        var auth = await ResolveAsync();

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.WrongServer);
        await Assert.That(auth.IssuedServerUrl).IsEqualTo("https://other.kcap.ai");
    }

    // A token file written before the binding existed carries no server, and stays usable.
    [Test]
    public async Task A_token_with_no_recorded_server_is_valid() {
        await SaveTokenAsync(null, TimeSpan.FromHours(1));

        await Assert.That((await ResolveAsync()).State).IsEqualTo(StatusAuthState.Valid);
    }

    [Test]
    public async Task An_expired_token_keeps_its_identity() {
        await SaveTokenAsync(Server, TimeSpan.FromHours(-1));

        var auth = await ResolveAsync();

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.Expired);
        await Assert.That(auth.Identity).IsEqualTo("alice");
    }

    [Test]
    public async Task No_stored_token_is_none() {
        await Assert.That((await ResolveAsync()).State).IsEqualTo(StatusAuthState.None);
    }

    // There is nothing to bind the token to, so expiry is all that can be judged.
    [Test]
    public async Task With_no_server_a_token_is_judged_on_expiry_alone() {
        await SaveTokenAsync("https://other.kcap.ai", TimeSpan.FromHours(1));

        var auth = await ResolveAsync(server: new StatusCommand.ServerReach(null, false, null));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.Valid);
    }

    [Test]
    public async Task The_auth_line_names_the_server_a_foreign_token_came_from() {
        var now = DateTimeOffset.UtcNow;

        await Assert.That(StatusCommand.FormatAuthLine(
                new StatusCommand.AuthSnapshot(StatusAuthState.WrongServer, IssuedServerUrl: "https://other.kcap.ai"), now))
            .IsEqualTo("✗ token was issued by https://other.kcap.ai (run: kcap login)");
        await Assert.That(StatusCommand.FormatAuthLine(new StatusCommand.AuthSnapshot(StatusAuthState.WrongServer), now))
            .IsEqualTo("✗ token was issued by another server (run: kcap login)");
    }

    [Test]
    public async Task The_auth_line_for_a_valid_token_carries_identity_and_expiry() {
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        await Assert.That(StatusCommand.FormatAuthLine(
                new StatusCommand.AuthSnapshot(StatusAuthState.Valid, "alice", now.AddHours(5)), now))
            .IsEqualTo("alice ✓ token valid (expires in 5h)");
    }
}
