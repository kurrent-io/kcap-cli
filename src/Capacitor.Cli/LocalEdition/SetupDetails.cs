namespace Capacitor.Cli.LocalEdition;

/// <summary>What a setup journal records about the installation it is creating.</summary>
/// <param name="ConfigDir">Absolute, so a recovery from another working directory or config override publishes the
/// profile where the interrupted setup meant to.</param>
sealed record SetupDetails(string InstallationId, int Port, string Version, string ServerUrl, string ConfigDir, string Profile);
