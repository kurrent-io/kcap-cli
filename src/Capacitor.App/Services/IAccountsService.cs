using Capacitor.Cli.Core.Accounts;

namespace Capacitor.App.Services;

public interface IAccountsService {
    /// Replay-1. The connected daemon's latest snapshot: null until it advertises accounts/1 and the
    /// first one arrives, and null again when the connection or the capability goes. Published on
    /// background continuations — consumers ObserveOn(RxSchedulers.MainThreadScheduler).
    IObservable<AccountsSnapshotDto?> Snapshot { get; }

    /// Replay-1. Whether the connected daemon advertises accounts/1; false while not connected.
    IObservable<bool> Supported { get; }

    /// True once a snapshot at or past <paramref name="revision"/> is held; false when
    /// <paramref name="timeout"/> passes first, or when the subscription is lost (the snapshot goes
    /// null) before one arrives. A caller's cancellation throws.
    Task<bool> ReachedAsync(long revision, TimeSpan timeout, CancellationToken ct);
}
