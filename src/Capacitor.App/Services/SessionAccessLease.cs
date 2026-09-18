using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// One consumer's hold on a session's hub subscriptions. Disposing it more than once is a no-op.
public sealed class SessionAccessLease : IDisposable {
    readonly Action<SessionAccessLease> _release;
    int _released;

    internal SessionAccessLease(string sessionId, BehaviorSubject<SessionAccessState> state, Action<SessionAccessLease> release) {
        SessionId = sessionId;
        State = state.AsObservable();
        _release = release;
    }

    public string SessionId { get; }
    /// Replays the current state on subscribe.
    public IObservable<SessionAccessState> State { get; }

    public void Dispose() {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release(this);
    }
}
