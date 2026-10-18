using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>This root's installation for the <c>kcap local</c> commands: a lock-free <see cref="Read"/> for reporting,
/// and <see cref="RunAsync{T}"/> for anything that changes it.</summary>
sealed class LocalStack(LocalRoot root, UserHome home, ILocalServerService service, HttpClient local, TimeProvider time) {
    static readonly TimeSpan LeaseWait = TimeSpan.FromSeconds(10);

    public LocalRoot Root => root;

    public ILocalServerService Service => service;

    /// <summary>No lock and no writes, so another command may be changing what this reads.</summary>
    public LocalStackState Read() =>
        LocalStateFiles.ReadJournal(root) switch {
            StateRead<LifecycleJournalEntry>.Present(var entry)             => new LocalStackState.Interrupted(entry.Operation, entry.Phase),
            StateRead<LifecycleJournalEntry>.Unreadable(var path, var why)  => new LocalStackState.Unreadable(path, why),
            _                                                               => ReadRegistry(),
        };

    /// <summary>Runs <paramref name="command"/> under the lifecycle lease, after an interrupted setup has been finished
    /// or undone; recovery reports to <paramref name="progress"/>. The command does not run when the lease is still held
    /// after the wait or the journal cannot be acted on (<see cref="LocalLifecycleException"/>), or when the recovery
    /// stops short (<see cref="LocalInstallFailure"/>).</summary>
    public Task<T> RunAsync<T>(IProgress<LocalInstallEvent> progress, Func<LifecycleLease, LocalStackState, CancellationToken, Task<T>> command,
        CancellationToken ct) {
        // A rolled-forward setup is already committed; the command reads the state it left.
        var recovery  = new LocalSetupRecovery(service, home, local, time, progress, _ => { });
        var lifecycle = new LocalLifecycle(root, [recovery], time);

        return lifecycle.RunAsync(LeaseWait, (lease, token) => command(lease, ReadRegistry(), token), ct);
    }

    LocalStackState ReadRegistry() {
        switch (LocalStateFiles.ReadRegistry(root)) {
            case StateRead<InstallRegistry>.Unreadable(var path, var reason):
                return new LocalStackState.Unreadable(path, reason);
            case StateRead<InstallRegistry>.Present { Value.Active: { } active }:
                var unit = LocalServerUnit.ForInstallation(root, active.Port);
                return new LocalStackState.Installed(active, unit, service.Owner(unit));
            default:
                return LocalStackState.NotInstalled.Instance;
        }
    }
}
