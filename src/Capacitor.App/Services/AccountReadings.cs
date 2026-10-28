using System.Reactive.Linq;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// The drawable account snapshots and the clock that ages them: the one source every usage
/// surface reads. A host the app may not draw readings for reads as null.
public sealed class AccountReadings(
    IObservable<AccountsSnapshotDto?>              local,
    IObservable<IReadOnlyList<DaemonAccountsDto>>  drawableRemote,
    IObservable<DateTimeOffset>                    clock
) {
    public IObservable<DateTimeOffset> Clock => clock;

    public IObservable<AccountsSnapshotDto?> Local => local;

    public IObservable<AccountsSnapshotDto?> Remote(string daemonName) =>
        drawableRemote
            .Select(list => list.FirstOrDefault(d => d.DaemonName == daemonName)?.ToSnapshot())
            .DistinctUntilChanged(SnapshotEquality.Instance);

    sealed class SnapshotEquality : IEqualityComparer<AccountsSnapshotDto?> {
        public static readonly SnapshotEquality Instance = new();

        public bool Equals(AccountsSnapshotDto? x, AccountsSnapshotDto? y) =>
            x is null || y is null ? x is null && y is null : AccountSnapshotBuilder.Equivalent(x, y);

        public int GetHashCode(AccountsSnapshotDto? obj) => obj?.Accounts.Length ?? 0;
    }
}
