namespace Capacitor.Cli.Core.Accounts;

/// <summary>Whether Codex trusted kcap's hooks (a <see cref="HookTrustStates"/> value), and the fingerprint of the
/// Codex files that answer was given for.</summary>
public sealed record HookTrustObservation(string Trust, string Fingerprint);
