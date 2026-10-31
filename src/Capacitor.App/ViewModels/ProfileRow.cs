using Capacitor.Cli.Core.Config;

namespace Capacitor.App.ViewModels;

/// One profile as the Settings list shows it. Active is what config.json names now; bound is the
/// profile this process resolved at startup, which an external `kcap use --global` can leave behind.
public sealed record ProfileRow(string Name, string? ServerUrl, bool IsActive, bool IsBound, ProfileCredentialStatus Status) {
    public string StatusLabel => Status switch {
        ProfileCredentialStatus.SignedIn       => "Signed in",
        ProfileCredentialStatus.Expired        => "Sign-in expired",
        ProfileCredentialStatus.SignedOut      => "Signed out",
        ProfileCredentialStatus.OtherServer    => "Signed in to another server",
        ProfileCredentialStatus.NoSignInNeeded => "No sign-in needed",
        ProfileCredentialStatus.NoServer       => "No server configured",
        _                                      => "Could not read sign-in status"
    };

    public bool HasServer => ServerUrl is { Length: > 0 };
    /// Signed in is the normal state, so only a status that needs something is shown, and a server that takes
    /// no sign-in, so its row is not read as one missing a credential.
    public bool ShowsStatus => Status is not (ProfileCredentialStatus.NoServer or ProfileCredentialStatus.SignedIn);

    /// How this app and the daemon are connected through this profile right now; null for a profile neither uses.
    /// Being in use says nothing about this, nor does a stored credential.
    public string? Connection { get; init; }

    /// The connection is down for a reason someone has to act on.
    public bool ConnectionNeedsAttention { get; init; }

    /// A profile in use that cannot reach its server is the one that needs you now.
    public bool StatusNeedsAttention => ShowsStatus && Status is not ProfileCredentialStatus.NoSignInNeeded && (IsActive || IsBound);

    /// The blank profile every config starts with, never given a server: nothing to show or do.
    public bool IsPlaceholder => Name == ProfileConfig.DefaultName && !HasServer && !IsActive && !IsBound;

    public string? UsageLabel => (IsActive, IsBound) switch {
        (true, true)  => "In use",
        (true, false) => "Command line",
        (false, true) => "This app",
        _             => null,
    };

    public string? UsageTip => (IsActive, IsBound) switch {
        (true, true)  => "The kcap command line and this app both use this profile",
        (true, false) => "The kcap command line uses this profile. Relaunch kcap to use it here too",
        (false, true) => "This app uses this profile. The command line has since switched to another",
        _             => null,
    };

    public bool CanSignIn => Status is not (ProfileCredentialStatus.NoServer or ProfileCredentialStatus.NoSignInNeeded);

    /// A profile that cannot reach its server until someone signs in gets the button up front.
    public bool NeedsSignIn => CanSignIn && Status is not ProfileCredentialStatus.SignedIn;

    // Offered on a signed-in row too, from the menu: the status is graded offline, so a credential
    // the server has since rejected still reads as signed in, and this is the only way to replace it.
    public bool CanSignInAgain => Status is ProfileCredentialStatus.SignedIn;

    public string SignInLabel => Status is ProfileCredentialStatus.Expired ? "Sign in again" : "Sign in";

    public bool HasMenu => CanSignInAgain || CanRemove;

    public bool CanRemove => !IsActive && !IsBound && Name != ProfileConfig.DefaultName;
}
