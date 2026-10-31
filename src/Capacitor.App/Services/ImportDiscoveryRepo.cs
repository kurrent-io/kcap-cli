namespace Capacitor.App.Services;

/// <param name="Vendors">Null from a CLI that does not split a repository by harness.</param>
public sealed record ImportDiscoveryRepo(
    string Owner, string Name, int Sessions, DateTimeOffset? LastSessionAt, IReadOnlyList<ImportDiscoveryWindow>? Windows,
    IReadOnlyList<ImportDiscoveryVendor>? Vendors = null) {
    public string Slug => $"{Owner}/{Name}";
}
