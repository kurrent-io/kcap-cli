namespace Capacitor.Cli.Core.Accounts.Readings;

public sealed record CreditsDto(bool HasCredits, bool Unlimited, string? Balance);
