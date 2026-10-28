using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// The daemon client with its attach status replaced by <paramref name="status"/>, for the surfaces
/// that present it; everything else, including what the lifecycle controller acts on, stays the inner one's.
public sealed class StatusOverlayDaemonClientService(IDaemonClientService inner, IObservable<AttachStatus> status) : IDaemonClientService {
    public IObservable<AttachStatus> Status => status;
    public IObservable<DaemonStatusDto> Snapshots => inner.Snapshots;
    public SourceCache<AgentStatusDto, string> Agents => inner.Agents;
    public SourceCache<PendingLaunchDto, string> Pending => inner.Pending;
    public Task RestartLoopAsync() => inner.RestartLoopAsync();
    public Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct) => inner.StartDaemonAsync(ct);
    public string DaemonName => inner.DaemonName;
}
