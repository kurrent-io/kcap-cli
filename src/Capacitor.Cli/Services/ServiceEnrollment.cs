namespace Capacitor.Cli.Services;

/// <summary>The current user's opt-in to <c>kcap-service</c>, as it sits in their own registry hive</summary>
sealed record ServiceEnrollment(string Name, string DaemonPath, string? Profile, int? MaxAgents, string? EnvironmentFile);
