namespace Capacitor.Cli.Commands;

/// <param name="Version">The version the daemon recorded at startup, when it recorded one.</param>
public sealed record StatusDaemonEntryJson(string Name, int Pid, string? Version = null);
