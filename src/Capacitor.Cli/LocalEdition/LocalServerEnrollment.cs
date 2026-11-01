namespace Capacitor.Cli.LocalEdition;

/// <summary>The current user's local server as <c>kcap-service</c> reads it from their own hive.
/// <see cref="Stopped"/> is a deliberate stop the service honours across sign-ins and its own restarts.</summary>
sealed record LocalServerEnrollment(string ServerPath, string Root, string CliPath, string EnvironmentFile, string LogFile, bool Stopped);
