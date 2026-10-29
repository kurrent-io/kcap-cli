using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Pi;

/// One stdout line classified: not the response (keep reading), the response with an invalid
/// envelope (null models), or the response with its catalog (possibly empty).
internal readonly record struct PiModelCatalogParse(bool IsResponse, IReadOnlyList<VendorModelOption>? Models) {
    public static readonly PiModelCatalogParse NotResponse = new(false, null);
}
