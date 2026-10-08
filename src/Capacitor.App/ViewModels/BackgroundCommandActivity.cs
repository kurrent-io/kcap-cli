using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Subjects;

namespace Capacitor.App.ViewModels;

/// Running background commands per agent id, as the open sessions read them from their
/// transcripts. No hook reports a command to the daemon, so its subagent count never covers one,
/// and a session that is not open is absent here. Every call is made on the UI thread.
public sealed class BackgroundCommandActivity {
    readonly BehaviorSubject<IReadOnlyDictionary<string, int>> _running = new(FrozenDictionary<string, int>.Empty);

    public IObservable<IReadOnlyDictionary<string, int>> Running => _running;

    /// Reports the session's running commands as they change; disposing drops the agent.
    public IDisposable Track(string agentId, SessionRuns runs) {
        void Changed() => Report(agentId, runs.RunningCommandCount);
        runs.Changed += Changed;
        Changed();
        return Disposable.Create(() => {
            runs.Changed -= Changed;
            Report(agentId, 0);
        });
    }

    void Report(string agentId, int running) {
        var current = _running.Value;
        if (current.GetValueOrDefault(agentId) == running) return;
        var next = current.Where(pair => pair.Key != agentId).ToDictionary(StringComparer.Ordinal);
        if (running > 0) next[agentId] = running;
        _running.OnNext(next.Count == 0 ? FrozenDictionary<string, int>.Empty : next.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
