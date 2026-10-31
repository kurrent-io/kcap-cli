using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// Scripted IPlanArtifactSource: reads answer from a queue, or park on a gate so a test can settle
/// them in a chosen order. Every read records the id it was asked for. Pages script separately.
sealed class FakePlanArtifactSource : IPlanArtifactSource {
    readonly Queue<PlanArtifactsRead> _scripted = new();
    readonly Queue<(TaskCompletionSource<PlanArtifactsRead> Source, bool IgnoreCancellation)> _gates = new();
    readonly Queue<SessionPagesRead> _scriptedPages = new();
    readonly Queue<(TaskCompletionSource<SessionPagesRead> Source, bool IgnoreCancellation)> _pageGates = new();

    public readonly List<string> Requested = [];
    public readonly List<string> RequestedPages = [];
    public PlanArtifactsRead Default = new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto());
    public SessionPagesRead DefaultPages = SessionPagesRead.Of(SessionPlansReadKind.Ready);

    public void Enqueue(params PlanArtifactsRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    public void EnqueuePages(params SessionPagesRead[] reads) {
        foreach (var read in reads) _scriptedPages.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue; with
    /// ignoreCancellation a cancelled lease still receives the result, as a slow transport would.
    public TaskCompletionSource<PlanArtifactsRead> Gate(bool ignoreCancellation = false) {
        var gate = new TaskCompletionSource<PlanArtifactsRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue((gate, ignoreCancellation));
        return gate;
    }

    public TaskCompletionSource<SessionPagesRead> GatePages(bool ignoreCancellation = false) {
        var gate = new TaskCompletionSource<SessionPagesRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pageGates.Enqueue((gate, ignoreCancellation));
        return gate;
    }

    public PlanArtifactsRead? Cached(string sessionId) => null;

    public SessionPagesRead? CachedPages(string sessionId) => null;

    public async Task<PlanArtifactsRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        if (_gates.Count > 0) {
            var (source, ignoreCancellation) = _gates.Dequeue();
            return ignoreCancellation ? await source.Task : await source.Task.WaitAsync(ct);
        }
        await Task.Yield();
        return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
    }

    public async Task<SessionPagesRead> ReadPagesAsync(string sessionId, CancellationToken ct) {
        RequestedPages.Add(sessionId);
        if (_pageGates.Count > 0) {
            var (source, ignoreCancellation) = _pageGates.Dequeue();
            return ignoreCancellation ? await source.Task : await source.Task.WaitAsync(ct);
        }
        await Task.Yield();
        return _scriptedPages.Count > 0 ? _scriptedPages.Dequeue() : DefaultPages;
    }
}
