using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A daemon whose sign-in cannot reach the server says so, instead of leaving a client to read an
/// endless reconnect as a passing outage.
public class DaemonCredentialHealthTests {
    static CredentialState State(AuthStatus status) => new(status == AuthStatus.Ok ? "token" : null, status);

    [Test]
    [Arguments(AuthStatus.NotAuthenticated, DaemonCredentialHealth.SignedOut)]
    [Arguments(AuthStatus.Expired, DaemonCredentialHealth.SignedOut)]
    [Arguments(AuthStatus.WrongServer, DaemonCredentialHealth.SignedOut)]
    [Arguments(AuthStatus.Ok, null)]
    [Arguments(AuthStatus.NoAuthRequired, null)]
    [Arguments(AuthStatus.UnusableServerUrl, null)]
    public async Task A_resolve_with_nothing_usable_is_signed_out(AuthStatus status, string? problem) {
        var health = new DaemonCredentialHealth(() => { });
        health.Observe(State(status));
        await Assert.That(health.Problem).IsEqualTo(problem);
    }

    [Test]
    public async Task A_usable_resolve_clears_signed_out_but_only_a_connection_clears_a_rejection() {
        var changes = 0;
        var health = new DaemonCredentialHealth(() => changes++);

        health.Observe(State(AuthStatus.NotAuthenticated));
        health.Observe(State(AuthStatus.NotAuthenticated));
        health.Observe(State(AuthStatus.Ok));
        await Assert.That(health.Problem).IsNull();

        health.ObserveRejection();
        health.Observe(State(AuthStatus.Ok));
        await Assert.That(health.Problem).IsEqualTo(DaemonCredentialHealth.Rejected)
            .Because("the server refused that very token; a fresh read of it proves nothing");

        health.ObserveConnected();
        await Assert.That(health.Problem).IsNull();
        await Assert.That(changes).IsEqualTo(4).Because("only real changes pulse the status stream");
    }
}
