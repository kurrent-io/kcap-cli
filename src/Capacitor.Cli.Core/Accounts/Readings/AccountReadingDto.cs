namespace Capacitor.Cli.Core.Accounts.Readings;

/// <summary><see cref="MetadataAt"/> is when <see cref="Plan"/> and <see cref="Credits"/> were observed; null is
/// earlier than any time. A reading written without it binds null.</summary>
public sealed record AccountReadingDto(
    long                Generation,
    UsageWindowDto[]    Windows,
    string?             Plan,
    CreditsDto?         Credits,
    BlockedMarkerDto?   Blocked,
    DateTimeOffset?     MetadataAt = null);
