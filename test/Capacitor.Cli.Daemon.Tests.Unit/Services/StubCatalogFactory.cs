using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A runtime factory that only answers the catalog questions; launching through it throws.
internal sealed class StubCatalogFactory(string vendor, IReadOnlyList<VendorModelOption>? models, IReadOnlyList<string>? paths = null)
        : IHostedAgentRuntimeFactory {
    public string  Vendor             { get; } = vendor;
    public string? CliPathOverride    { get; init; }
    public string  CliPath            => CliPathOverride ?? "/bin/" + Vendor;
    public bool    SupportsUnattended => false;
    public int     ProbeCalls         { get; private set; }

    public IReadOnlyList<string> CatalogFingerprintPaths { get; } = paths ?? [];

    public bool IsAvailable() => true;

    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) {
        ProbeCalls++;
        return Task.FromResult(models);
    }

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
        throw new NotSupportedException("a catalog stub launches nothing");
}
