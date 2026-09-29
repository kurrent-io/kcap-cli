using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands.Capture;

internal sealed class RepairImportSource : IImportSource {
    public HarnessId Vendor { get; init; } = HarnessId.Claude;
    public bool IsAvailable => true;
    public bool SupportsTitleGeneration => false;
    public bool AttachesChildContentOnReplay => false;
    public IReadOnlyList<DiscoveredSession> Sessions { get; set; } = [];
    public int DiscoveryCount { get; private set; }
    public Task<IReadOnlyList<DiscoveredSession>> DiscoverAsync(DiscoveryFilters filters, CancellationToken ct) {
        DiscoveryCount++;
        return Task.FromResult(Sessions);
    }
    public Task<IReadOnlyList<ImportCommand.SessionClassification>> ClassifyAsync(
        IReadOnlyList<DiscoveredSession> sessions, ClassifyContext ctx, CancellationToken ct) => throw new NotSupportedException();
    public Task<ImportSessionResult> ImportSessionAsync(
        ImportCommand.SessionClassification classification, ImportContext ctx, CancellationToken ct) => throw new NotSupportedException();
}
