using Capacitor.Cli.Core.Accounts.Readings;

namespace Capacitor.Cli.Core.Accounts;

/// <summary>One refresh's vendor answers. A null <see cref="IdentityError"/> means the identity step answered:
/// <see cref="SignIn"/> is then <see cref="SignInStates.SignedIn"/> or <see cref="SignInStates.SignedOut"/>, or null
/// when the vendor would not say, and a null <see cref="Identity"/> names no login (signed out). The limits step
/// is independent: either <see cref="LimitsError"/> or a possibly-null <see cref="Limits"/>, with <see cref="LimitsNote"/> saying
/// why a step that did not need to ask has none. A null <see cref="HookTrust"/> is "no answer".</summary>
public sealed record ProbeResult(
    string?             SignIn,
    AccountIdentity?    Identity,
    string?             IdentityError,
    ReadingObservation? Limits,
    string?             LimitsError,
    HookTrustObservation? HookTrust = null,
    string?             LimitsNote = null);
