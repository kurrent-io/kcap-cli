using System.Collections.Concurrent;
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

    /// Answers a probe of an account's directory; unset, it answers like the default probe.
    public Func<string, Task<IReadOnlyList<VendorModelOption>?>>? ProbeDirectory { get; init; }

    /// An account directory's catalog files; unset, they are the default ones.
    public Func<string, IReadOnlyList<string>>? PathsFor { get; init; }

    public ConcurrentQueue<string> ProbedDirectories { get; } = new();

    public IReadOnlyList<string> CatalogFingerprintPaths { get; } = paths ?? [];

    public bool IsAvailable() => true;

    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(CancellationToken ct) {
        ProbeCalls++;
        return Task.FromResult(models);
    }

    public Task<IReadOnlyList<VendorModelOption>?> ProbeModelsAsync(string? configDirectory, CancellationToken ct) {
        if (configDirectory is null) return ProbeModelsAsync(ct);

        ProbedDirectories.Enqueue(configDirectory);
        return ProbeDirectory?.Invoke(configDirectory) ?? Task.FromResult(models);
    }

    public IReadOnlyList<string> CatalogFingerprintPathsFor(string? configDirectory) =>
        configDirectory is not null && PathsFor is { } pathsFor ? pathsFor(configDirectory) : CatalogFingerprintPaths;

    public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
        throw new NotSupportedException("a catalog stub launches nothing");
}
