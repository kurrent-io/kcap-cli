using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace Capacitor.App.ViewModels;

/// Running commands by source-scoped row key. Closing a workspace hands its live commands to a
/// transcript monitor until the session ends or the workspace reopens. Every call is on the UI thread.
public sealed class BackgroundCommandActivity(
        Func<string, Action<int>, Action, TranscriptFeedHandoff?, IDisposable>? watch = null) : IDisposable {
    readonly BehaviorSubject<IReadOnlyDictionary<string, int>> _running = new(FrozenDictionary<string, int>.Empty);
    readonly Dictionary<string, List<Registration>> _byKey = new(StringComparer.Ordinal);
    bool _disposed;

    sealed class Registration : IDisposable {
        public int Count;
        public bool Detached;
        public readonly SerialDisposable Watch = new();
        public IDisposable Tracking = Disposable.Empty;

        public void Dispose() {
            Tracking.Dispose();
            Watch.Dispose();
        }
    }

    public IObservable<IReadOnlyDictionary<string, int>> Running => _running;

    /// The newest registration for a key is the one counted: a reopened session's workspace is
    /// built before the outgoing one is torn down, and that teardown drops only its own entry.
    /// <paramref name="handOff"/> is asked only when a monitor takes over, for the feed to carry on from.
    public IDisposable Track(string rowKey, SessionRuns runs, Func<TranscriptFeedHandoff?>? handOff = null) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var registration = new Registration();
        if (!_byKey.TryGetValue(rowKey, out var registrations)) _byKey[rowKey] = registrations = [];
        foreach (var previous in registrations.Where(r => r.Detached).ToArray()) {
            previous.Dispose();
            registrations.Remove(previous);
        }
        registrations.Add(registration);
        void Changed() {
            registration.Count = runs.RunningCommandCount;
            Publish();
        }
        runs.Changed += Changed;
        registration.Tracking = Disposable.Create(() => runs.Changed -= Changed);
        Changed();
        return Disposable.Create(() => {
            registration.Tracking.Dispose();
            if (_disposed) return;
            if (watch is not null && ReferenceEquals(registrations[^1], registration)) {
                registration.Detached = true;
                var handoff = handOff?.Invoke();
                try {
                    registration.Watch.Disposable = watch(rowKey, count => {
                        if (_disposed || !registrations.Contains(registration)) return;
                        registration.Count = count;
                        Publish();
                    }, Remove, handoff);
                } catch {
                    // The handed feed is owned by the monitor that failed to start; nothing else will close it.
                    handoff?.Feed.Dispose();
                    Remove();
                    throw;
                }
                return;
            }
            Remove();
        });

        void Remove() {
            if (!registrations.Remove(registration)) return;
            registration.Dispose();
            if (registrations.Count == 0) _byKey.Remove(rowKey);
            Publish();
        }
    }

    void Publish() {
        var next = _byKey
            .Where(pair => pair.Value[^1].Count > 0)
            .ToFrozenDictionary(pair => pair.Key, pair => pair.Value[^1].Count, StringComparer.Ordinal);
        var current = _running.Value;
        if (next.Count == current.Count && next.All(pair => current.GetValueOrDefault(pair.Key) == pair.Value)) return;
        _running.OnNext(next.Count == 0 ? FrozenDictionary<string, int>.Empty : next);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        foreach (var registration in _byKey.Values.SelectMany(registrations => registrations))
            registration.Dispose();
        _byKey.Clear();
        Publish();
    }
}
