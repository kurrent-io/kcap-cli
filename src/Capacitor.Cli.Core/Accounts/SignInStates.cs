namespace Capacitor.Cli.Core.Accounts;

/// <summary>The sign-in states only an authoritative vendor answer sets; an account never probed, or whose
/// probe failed, keeps a null sign-in, which reads as unknown.</summary>
public static class SignInStates {
    public const string SignedIn  = "signed_in";
    public const string SignedOut = "signed_out";
}
