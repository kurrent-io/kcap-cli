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

    public string ServerLabel => ServerUrl is { Length: > 0 } url ? url : "Add a server with kcap profile add";

    public bool CanSignIn => Status is not (ProfileCredentialStatus.NoServer or ProfileCredentialStatus.NoSignInNeeded);

    // Offered on a signed-in row too: the status is graded offline, so a credential the server has
    // since rejected still reads as signed in, and the button is the only way to replace it.
    public string SignInLabel => Status is ProfileCredentialStatus.SignedIn or ProfileCredentialStatus.Expired
        ? "Sign in again"
        : "Sign in";

    public bool CanRemove => !IsActive && !IsBound && Name != ProfileConfig.DefaultName;
}
