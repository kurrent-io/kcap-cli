namespace Capacitor.Cli.Core.Accounts;

/// <summary>The vendor config directory a launch runs under. <see cref="AccountId"/> is null when the
/// directory is not in the registry.</summary>
public sealed record ResolvedAccount(string Directory, string? AccountId, string Label);
