namespace Capacitor.Cli.LocalEdition;

/// <summary><see cref="Unloaded"/>: a quick uninstall removed the unit while the server was still stopping, so
/// <c>bin/</c> and <c>run/</c> are left.</summary>
enum LocalUninstallOutcome { NotInstalled, Uninstalled, Purged, Unloaded }
