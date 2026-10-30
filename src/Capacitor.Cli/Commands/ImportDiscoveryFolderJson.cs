namespace Capacitor.Cli.Commands;

/// <summary>One folder whose sessions matched no repository, in <c>kcap import --discover --json</c>.</summary>
/// <param name="Folder">Null for sessions with no folder recorded, which no flag can name.</param>
/// <param name="Windows">In the order of the report's own <see cref="ImportDiscoveryJson.Windows"/>; null when
/// <paramref name="Folder"/> is.</param>
public sealed record ImportDiscoveryFolderJson(
    string? Folder, int Sessions, IReadOnlyList<ImportDiscoveryWindowJson>? Windows);
