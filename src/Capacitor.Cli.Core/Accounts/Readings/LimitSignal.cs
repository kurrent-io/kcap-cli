namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>A reported limit hit. Both ids set name one window, only <see cref="LimitId"/> names a
/// limit, neither names nothing. <see cref="Unbounded"/> is a block no quota reset lifts (credit
/// depletion, a spend cap), so it always adds the unknown constraint.</summary>
public sealed record LimitSignal(string? LimitId, int? WindowMinutes, bool Unbounded = false);
