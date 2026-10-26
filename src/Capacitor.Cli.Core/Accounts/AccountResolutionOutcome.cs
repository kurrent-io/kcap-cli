namespace Capacitor.Cli.Core.Accounts;

/// <summary>Exactly one of <see cref="Account"/> and <see cref="Refusal"/> is set.</summary>
public sealed record AccountResolutionOutcome(ResolvedAccount? Account, string? Refusal) {
    public bool Refused => Refusal is not null;
}
