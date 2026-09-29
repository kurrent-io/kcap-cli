namespace Capacitor.Cli.Commands;

/// <param name="UpdateAvailable">The version to move to, or null when current.</param>
/// <param name="ServerCapped">The target was held back to the connected server's version.</param>
public sealed record StatusVersionJson(
    string Current, string? UpdateAvailable, bool ServerCapped, bool Bundled);
