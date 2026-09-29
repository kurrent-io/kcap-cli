using Capacitor.App.Services;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// Scripted IPlanSource: reads answer from a queue, or park on a gate so a test can settle them in
/// a chosen order. Every read records the id it was asked for.
sealed class FakePlanSource : IPlanSource {
    readonly Queue<SessionPlansRead> _scripted = new();
    readonly Queue<TaskCompletionSource<SessionPlansRead>> _gates = new();

    public readonly List<string> Requested = [];
    public SessionPlansRead Default = new(SessionPlansReadKind.Ready, []);

    public void Enqueue(params SessionPlansRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue.
    public TaskCompletionSource<SessionPlansRead> Gate() {
        var gate = new TaskCompletionSource<SessionPlansRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue(gate);
        return gate;
    }

    public async Task<SessionPlansRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        if (_gates.Count > 0) return await _gates.Dequeue().Task.WaitAsync(ct);
        await Task.Yield();
        return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
    }
}
