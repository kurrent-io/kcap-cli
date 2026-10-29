using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace Capacitor.App.Services.Notifications;

public sealed class NotificationSessionSubscriptions : IDisposable {
    readonly Dictionary<string, IDisposable> _leases = new(StringComparer.Ordinal);
    readonly IDisposable _subscription;

    public NotificationSessionSubscriptions(IObservable<IReadOnlyDictionary<string, string>> sessions,
            Func<string, IDisposable> acquire, IScheduler scheduler) {
        _subscription = sessions.ObserveOn(scheduler).Subscribe(current => {
            foreach (var id in _leases.Keys.Where(id => !current.ContainsKey(id)).ToArray()) {
                _leases[id].Dispose();
                _leases.Remove(id);
            }
            foreach (var id in current.Keys)
                if (!_leases.ContainsKey(id)) _leases.Add(id, acquire(id));
        });
    }

    public void Dispose() {
        _subscription.Dispose();
        foreach (var lease in _leases.Values) lease.Dispose();
        _leases.Clear();
    }
}
