using System.Reactive.Linq;

namespace Capacitor.App.Services;

public static class SignedInSubject {
    /// The account the app is signed in as: a Connected status's subject, kept while the lane
    /// reconnects and dropped on sign-out.
    public static IObservable<string?> From(IObservable<ServerLaneStatus> status) =>
        status.Scan((string?)null, (last, s) => s.State switch {
            ServerLaneState.Connected => s.Subject,
            ServerLaneState.SignedOut or ServerLaneState.Dormant => null,
            _ => last,
        }).DistinctUntilChanged();
}
