using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Why the daemon's own sign-in cannot reach the server, so a client can say "signed out"
/// rather than show an endless reconnect: SignalR keeps retrying a hub whose bearer will never be
/// accepted. Null while nothing says the credential is the problem.</summary>
sealed class DaemonCredentialHealth(Action changed) {
    /// <summary>The token store holds nothing this daemon's profile can send: none, expired past
    /// refresh, or minted by another server.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>The server refused the bearer the daemon sent.</summary>
    public const string Rejected = "rejected";

    string? _problem;

    public string? Problem => Volatile.Read(ref _problem);

    /// <summary>A resolve that yields a usable bearer clears only its own verdict: a rejection stands
    /// until the server accepts a connection.</summary>
    public void Observe(CredentialState state) {
        switch (state.Status) {
            case AuthStatus.NotAuthenticated or AuthStatus.Expired or AuthStatus.WrongServer:
                Set(SignedOut);
                break;
            // An unusable server URL is config's to fix, so it leaves no credential verdict standing.
            case AuthStatus.Ok or AuthStatus.NoAuthRequired or AuthStatus.UnusableServerUrl:
                // Compared and cleared in one step, so a rejection recorded meanwhile stands.
                if (Interlocked.CompareExchange(ref _problem, null, SignedOut) == SignedOut) changed();
                break;
        }
    }

    /// <summary>A refusal of the hub's own negotiate or socket; the caller reports no other lane's. It
    /// never replaces <see cref="SignedOut"/>: a hub refusing a request that carried no bearer says
    /// nothing new about the credential.</summary>
    public void ObserveRejection() {
        if (Interlocked.CompareExchange(ref _problem, Rejected, null) is null) changed();
    }

    public void ObserveConnected() => Set(null);

    void Set(string? problem) {
        if (Interlocked.Exchange(ref _problem, problem) != problem) changed();
    }
}
