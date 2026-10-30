namespace Capacitor.App.Services;

/// <summary>One harness's share of a repository's history, as <c>kcap import --discover --json</c> reports it.</summary>
/// <param name="Vendor">The id the harness's own vendor flag is spelled with.</param>
public sealed record ImportDiscoveryVendor(
    string Vendor, int Sessions, DateTimeOffset? LastSessionAt, IReadOnlyList<ImportDiscoveryWindow>? Windows);
