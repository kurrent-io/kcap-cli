namespace Capacitor.Cli.LocalEdition;

/// <summary>What a purge journal records: the installation being retired, and the config root whose profile and
/// caches name it.</summary>
sealed record PurgeDetails(string InstallationId, int Port, string ServerUrl, string ConfigDir);
