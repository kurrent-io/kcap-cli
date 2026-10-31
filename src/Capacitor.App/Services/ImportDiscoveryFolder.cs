namespace Capacitor.App.Services;

/// <summary>A folder whose sessions matched no repository, as <c>kcap import --discover --json</c> reports it.</summary>
/// <param name="Folder">The value <c>kcap import --folder</c> takes; null counts sessions with no folder recorded.</param>
/// <param name="Vendors">Null from a CLI that does not split a folder by harness.</param>
public sealed record ImportDiscoveryFolder(
    string? Folder, int Sessions, IReadOnlyList<ImportDiscoveryWindow>? Windows = null,
    IReadOnlyList<ImportDiscoveryVendor>? Vendors = null);
