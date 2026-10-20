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
            case AuthStatus.Ok or AuthStatus.NoAuthRequired when Problem == SignedOut:
                Set(null);
                break;
        }
    }

    public void ObserveRejection() => Set(Rejected);

    public void ObserveConnected() => Set(null);

    void Set(string? problem) {
        if (Interlocked.Exchange(ref _problem, problem) != problem) changed();
    }
}
