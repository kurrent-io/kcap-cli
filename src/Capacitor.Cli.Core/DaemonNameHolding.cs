namespace Capacitor.Cli.Core;

/// <summary>A daemon connected under a name from another machine on this account.</summary>
public readonly record struct DaemonNameHolding(string Platform, string? Version);
