namespace Capacitor.Cli.Core.Accounts.Readings;

public sealed record BlockedMarkerDto(DateTimeOffset? BlockedAt, DateTimeOffset? ClearedAt, BlockConstraintDto[] Constraints);
