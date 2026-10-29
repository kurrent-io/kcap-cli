using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>This root's installation for the <c>kcap local</c> commands: a lock-free <see cref="Read"/> for reporting,
/// and <see cref="RunAsync{T}"/> for anything that changes it.</summary>
sealed class LocalStack(
    LocalRoot root, UserHome home, ILocalServerService service, HttpClient local, TimeProvider time, IOutgoingRecorders recorders, ConfigRoot config) {
    static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(10);

    public LocalRoot Root => root;

    public ILocalServerService Service => service;

    public UserHome Home => home;

    /// <summary>Sends no credential and follows no redirect.</summary>
    public HttpClient Local => local;

    public TimeProvider Time => time;

    /// <summary>What listens on a port; the upgrade's readiness wait gives up early on a stranger there.</summary>
    public Func<int, PortHolder?> FindHolder { get; init; } = PortHolder.Find;

    /// <summary>No lock and no writes, so another command may be changing what this reads. Without the identity probe
    /// a unit naming this root by another spelling is <see cref="OwnerDecision.Unknown"/>.</summary>
    public LocalStackState Read() =>
        LocalStateFiles.ReadJournal(root) switch {
            StateRead<LifecycleJournalEntry>.Present(var entry)             => new LocalStackState.Interrupted(entry.Operation, entry.Phase),
            StateRead<LifecycleJournalEntry>.Unreadable(var path, var why)  => new LocalStackState.Unreadable(path, why),
            _                                                               => ReadRegistry(mayWrite: false),
        };

    /// <summary>Runs <paramref name="command"/> under the lifecycle lease, after an interrupted setup has been finished
    /// or undone and an interrupted purge finished; recovery reports to <paramref name="progress"/>. The command does not run when the lease is still held
    /// after the wait or the journal cannot be acted on (<see cref="LocalLifecycleException"/>), or when the recovery
    /// stops short (<see cref="LocalInstallFailure"/>). <paramref name="force"/> lets a purge's recovery stop daemons that
    /// are running agents.</summary>
    public Task<T> RunAsync<T>(IProgress<LocalInstallEvent> progress, Func<LifecycleLease, LocalStackState, CancellationToken, Task<T>> command,
        CancellationToken ct, bool force = false, Action<LocalStackState>? beforeRecovery = null) {
        // A rolled-forward setup is already committed; the command reads the state it left.
        var recovery  = new LocalSetupRecovery(service, home, local, time, progress, _ => { });
        var lifecycle = new LocalLifecycle(root, [
            recovery,
            new LocalPurgeRecovery(service, home, recorders, config, mayStop: true, force, progress),
            new LocalUpgradeRecovery(this, progress),
        ], time);

        return lifecycle.RunAsync(LeaseWait, (lease, token) => command(lease, ReadRegistry(mayWrite: true), token), ct,
            beforeRecovery is null ? null : _ => beforeRecovery(Read()));
    }

    /// <summary>The registry as <see cref="RunAsync{T}"/> hands it to its command, for a command that changed it.</summary>
    public LocalStackState ReadUnderLease() => ReadRegistry(mayWrite: true);

    LocalStackState ReadRegistry(bool mayWrite) {
        switch (LocalStateFiles.ReadRegistry(root)) {
            case StateRead<InstallRegistry>.Unreadable(var path, var reason):
                return new LocalStackState.Unreadable(path, reason);
            case StateRead<InstallRegistry>.Present { Value.Active: { } active }:
                var unit = LocalServerUnit.ForInstallation(root, active.Port);
                var readings = service.Owner(unit);
                return new LocalStackState.Installed(active, unit, readings, LocalRootIdentity.Decide(root, readings, mayWrite));
            default:
                return LocalStackState.NotInstalled.Instance;
        }
    }
}
