namespace Capacitor.Cli.Commands;

/// <summary>What this CLI authenticates as.</summary>
/// <param name="State">A <see cref="StatusAuthState"/>, in its wire spelling.</param>
public sealed record StatusAuthJson(string State, string? Identity, DateTimeOffset? ExpiresAt);
