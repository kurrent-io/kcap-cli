using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Tests.Unit;

sealed class FakeAccountsService : IAccountsService {
    public readonly BehaviorSubject<AccountsSnapshotDto?> SnapshotSubject = new(null);
    public readonly BehaviorSubject<bool> SupportedSubject = new(false);

    public IObservable<AccountsSnapshotDto?> Snapshot => SnapshotSubject;
    public IObservable<bool> Supported => SupportedSubject;

    public Task<bool> ReachedAsync(long revision, TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(SnapshotSubject.Value is { } held && held.Revision >= revision);

    /// What the real service publishes once a daemon advertising accounts/1 has sent a snapshot.
    public void Publish(AccountsSnapshotDto snapshot) {
        SupportedSubject.OnNext(true);
        SnapshotSubject.OnNext(snapshot);
    }
}
