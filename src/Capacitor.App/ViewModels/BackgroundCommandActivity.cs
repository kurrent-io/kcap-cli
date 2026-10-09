using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace Capacitor.App.ViewModels;

/// Running background commands per source-scoped row key ("local:{id}" / "remote:{id}"), as the
/// open sessions read them from their transcripts. No hook reports a command to the daemon, so
/// its subagent count never covers one, and a session that is not open is absent here. An
/// unproven twin pair shares an id across lanes, which is why the key carries the lane. Every
/// call is made on the UI thread.
public sealed class BackgroundCommandActivity {
    readonly BehaviorSubject<IReadOnlyDictionary<string, int>> _running = new(FrozenDictionary<string, int>.Empty);
    readonly Dictionary<string, List<Registration>> _byKey = new(StringComparer.Ordinal);

    sealed class Registration {
        public int Count;
    }

    public IObservable<IReadOnlyDictionary<string, int>> Running => _running;

    /// The newest registration for a key is the one counted: a reopened session's workspace is
    /// built before the outgoing one is torn down, and that teardown drops only its own entry.
    public IDisposable Track(string rowKey, SessionRuns runs) {
        var registration = new Registration();
        if (!_byKey.TryGetValue(rowKey, out var registrations)) _byKey[rowKey] = registrations = [];
        registrations.Add(registration);
        void Changed() {
            registration.Count = runs.RunningCommandCount;
            Publish();
        }
        runs.Changed += Changed;
        Changed();
        return Disposable.Create(() => {
            runs.Changed -= Changed;
            registrations.Remove(registration);
            if (registrations.Count == 0) _byKey.Remove(rowKey);
            Publish();
        });
    }

    void Publish() {
        var next = _byKey
            .Where(pair => pair.Value[^1].Count > 0)
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value[^1].Count, StringComparer.Ordinal);
        var current = _running.Value;
        if (next.Count == current.Count && next.All(pair => current.GetValueOrDefault(pair.Key) == pair.Value)) return;
        _running.OnNext(next.Count == 0 ? FrozenDictionary<string, int>.Empty : next);
    }
}
