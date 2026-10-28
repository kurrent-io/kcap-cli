namespace Capacitor.Cli.Core.Accounts;

/// <summary>The vendor config directory a launch runs under. <see cref="AccountId"/> is null when the
/// directory is not in the registry. <see cref="Generation"/> is the entry's at resolution, so a reading the
/// launch produces after a re-login is refused rather than written to the new login.</summary>
public sealed record ResolvedAccount(string Directory, string? AccountId, string Label, long Generation = 0);
