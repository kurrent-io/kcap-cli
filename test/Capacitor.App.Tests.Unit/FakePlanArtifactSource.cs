using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// Scripted IPlanArtifactSource: reads answer from a queue, or park on a gate so a test can settle
/// them in a chosen order. Every read records the id it was asked for.
sealed class FakePlanArtifactSource : IPlanArtifactSource {
    readonly Queue<PlanArtifactsRead> _scripted = new();
    readonly Queue<(TaskCompletionSource<PlanArtifactsRead> Source, bool IgnoreCancellation)> _gates = new();

    public readonly List<string> Requested = [];
    public PlanArtifactsRead Default = new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto());

    public void Enqueue(params PlanArtifactsRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue; with
    /// ignoreCancellation a cancelled lease still receives the result, as a slow transport would.
    public TaskCompletionSource<PlanArtifactsRead> Gate(bool ignoreCancellation = false) {
        var gate = new TaskCompletionSource<PlanArtifactsRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue((gate, ignoreCancellation));
        return gate;
    }

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        if (_gates.Count > 0) {
            var (source, ignoreCancellation) = _gates.Dequeue();
            return ignoreCancellation ? await source.Task : await source.Task.WaitAsync(ct);
        }
        await Task.Yield();
        return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
    }
}
