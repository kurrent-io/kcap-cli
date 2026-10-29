namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>One rate-limit window, keyed by <see cref="LimitId"/> and <see cref="WindowMinutes"/>.
/// A null <see cref="ConfirmedAt"/> means no fresh vendor response has confirmed the values.</summary>
public sealed record UsageWindowDto(string LimitId, int WindowMinutes, double UsedPercent, DateTimeOffset? ResetsAt, DateTimeOffset? ConfirmedAt);
