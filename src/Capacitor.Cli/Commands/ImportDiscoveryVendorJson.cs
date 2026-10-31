namespace Capacitor.Cli.Commands;

/// <summary>One harness's share of a repository in <c>kcap import --discover --json</c>.</summary>
/// <param name="Vendor">The id its <c>--&lt;vendor&gt;</c> flag is spelled with.</param>
/// <param name="Windows">In the order of the report's own <see cref="ImportDiscoveryJson.Windows"/>.</param>
public sealed record ImportDiscoveryVendorJson(
    string Vendor, int Sessions, string? LastSessionAt, IReadOnlyList<ImportDiscoveryWindowJson> Windows);
