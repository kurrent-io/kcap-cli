namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary>A null <see cref="ResetsAt"/> is the unknown constraint, which never lapses by time.</summary>
public sealed record BlockConstraintDto(string? LimitId, int? WindowMinutes, DateTimeOffset? ResetsAt, DateTimeOffset SignaledAt);
