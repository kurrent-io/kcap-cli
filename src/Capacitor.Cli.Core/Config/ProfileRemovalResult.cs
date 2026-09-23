namespace Capacitor.Cli.Core.Config;

/// <param name="Detail">For <see cref="ProfileRemovalOutcome.RemovedTokenRetained"/>: the token file
/// that stayed and why.</param>
public sealed record ProfileRemovalResult(ProfileRemovalOutcome Outcome, string? Detail = null);
