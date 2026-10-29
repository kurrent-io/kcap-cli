namespace Capacitor.Cli.Commands;

/// <summary>The configured server and whether it answered.</summary>
/// <param name="Url">Null when no server is configured, which is what an unset-up machine looks like.</param>
/// <param name="Reachable">Null when there was nothing to probe. Reachability, not authorization.</param>
/// <param name="StatusCode">Set only when the server answered with a failure status.</param>
public sealed record StatusServerJson(string? Url, bool? Reachable, int? StatusCode);
