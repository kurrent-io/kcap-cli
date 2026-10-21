namespace Capacitor.Cli.LocalEdition;

/// <summary>What an upgrade journal records. <paramref name="WasRunning"/> decides whether a failure before the switch
/// starts the old version again.</summary>
sealed record UpgradeDetails(string From, string To, int Port, bool WasRunning);
