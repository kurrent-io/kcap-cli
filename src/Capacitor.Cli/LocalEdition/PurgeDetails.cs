namespace Capacitor.Cli.LocalEdition;

/// <summary>What a purge journal records: the installation being retired, the config root whose profile and caches
/// name it, and the directory of the watchers that were stopped for it.</summary>
sealed record PurgeDetails(string InstallationId, int Port, string ServerUrl, string ConfigDir, string WatcherDir);
