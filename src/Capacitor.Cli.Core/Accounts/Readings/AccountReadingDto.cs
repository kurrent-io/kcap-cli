namespace Capacitor.Cli.Core.Accounts.Readings;

public sealed record AccountReadingDto(long Generation, UsageWindowDto[] Windows, string? Plan, CreditsDto? Credits, BlockedMarkerDto? Blocked);
