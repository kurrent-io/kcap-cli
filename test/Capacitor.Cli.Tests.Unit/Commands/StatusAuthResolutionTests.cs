using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;

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

    Task<StatusCommand.AuthSnapshot> ResolveAsync(
            MachineAuth? machine = null, StatusCommand.ServerReach? server = null, LocalEndpointClass? endpoint = null) =>
        StatusCommand.ResolveAuthAsync(
            Tokens, machine ?? MachineAuth.None, Profile, server ?? Reached, endpoint ?? new LocalEndpointClass.Ordinary());

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

    /// <summary>Through an outage the provider is the last one cached, and a token left behind by a
    /// cloud setup is still bound to this URL's server: neither may make status read or refresh it.</summary>
    [Test]
    public async Task A_local_installation_needs_no_credential_whatever_the_cache_and_the_store_say() {
        await SaveTokenAsync(Server, TimeSpan.FromHours(1));

        var auth = await ResolveAsync(
            server: new StatusCommand.ServerReach(Server, false, null, AuthProvider.WorkOS),
            endpoint: new LocalEndpointClass.Local(new LocalInstallation("inst-1", 51234, Server, "1.0.0")));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.NotRequired);
    }

    /// <summary>Whoami and skills refuse these, so status may not call them fine; and the remedy is
    /// setup, not the login a token state would suggest.</summary>
    [Test]
    public async Task A_removed_or_undeterminable_installation_is_a_problem_not_a_login() {
        await SaveTokenAsync(Server, TimeSpan.FromHours(1));

        var retired      = await ResolveAsync(endpoint: new LocalEndpointClass.Retired(new RetiredInstallation("inst-0", 51234, Server)));
        var undetermined = await ResolveAsync(endpoint: new LocalEndpointClass.Undetermined("the registry cannot be read"));

        await Assert.That(retired.State).IsEqualTo(StatusAuthState.LocalUnavailable);
        await Assert.That(StatusCommand.FormatAuthLine(retired, DateTimeOffset.UtcNow)).Contains("removed local installation");
        await Assert.That(undetermined.State).IsEqualTo(StatusAuthState.LocalUnavailable);
        await Assert.That(StatusCommand.FormatAuthLine(undetermined, DateTimeOffset.UtcNow)).Contains("registry cannot be read");
    }

    // An unanswered probe knows nothing about the provider. Reading that as "no auth" would call
    // every unreachable server with an empty token store set up.
    [Test]
    public async Task An_unknown_provider_is_never_read_as_no_auth() {
        var auth = await ResolveAsync(server: new StatusCommand.ServerReach(Server, false, null, Provider: null));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.None);
    }

    /// <summary>A sign-in cannot reach a server that gave no answer, so a missing token is not
    /// reported as the problem to fix.</summary>
    [Test]
    public async Task No_token_against_an_unanswering_server_is_not_sent_to_login() {
        var auth = await ResolveAsync(server: new StatusCommand.ServerReach(Server, false, null, Failure: ConnectionFailure.Refused));
        var line = StatusCommand.FormatAuthLine(auth, DateTimeOffset.UtcNow);

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.None);
        await Assert.That(auth.ServerUnanswered).IsTrue();
        await Assert.That(line).DoesNotContain("kcap login");
        await Assert.That(line).Contains("the server gave no answer");
    }

    [Test]
    public async Task An_expired_token_against_an_unanswering_server_waits_for_the_server() {
        await SaveTokenAsync(Server, TimeSpan.FromHours(-1));

        var auth = await ResolveAsync(server: new StatusCommand.ServerReach(Server, false, null, Failure: ConnectionFailure.TimedOut));
        var line = StatusCommand.FormatAuthLine(auth, DateTimeOffset.UtcNow);

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.Expired);
        await Assert.That(line).IsEqualTo("alice ✗ token expired; it cannot be renewed until the server answers");
    }

    /// <summary>A server that answered, even with a failure status, can take a login: the advice stays.</summary>
    [Test]
    public async Task A_server_that_answered_keeps_the_login_advice() {
        var answered = await ResolveAsync(server: new StatusCommand.ServerReach(Server, false, 502));
        var reached  = await ResolveAsync();

        await Assert.That(StatusCommand.FormatAuthLine(answered, DateTimeOffset.UtcNow)).IsEqualTo("not authenticated (run: kcap login)");
        await Assert.That(StatusCommand.FormatAuthLine(reached, DateTimeOffset.UtcNow)).IsEqualTo("not authenticated (run: kcap login)");
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

    /// <summary>A fresh or uninstalled configuration has no server for `kcap login` to sign in to.</summary>
    [Test]
    public async Task With_no_server_and_no_token_the_auth_line_points_at_setup() {
        var auth = await ResolveAsync(server: new StatusCommand.ServerReach(null, false, null));

        await Assert.That(auth.State).IsEqualTo(StatusAuthState.None);
        await Assert.That(StatusCommand.FormatAuthLine(auth, DateTimeOffset.UtcNow)).IsEqualTo("not set up (run: kcap setup)");
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
