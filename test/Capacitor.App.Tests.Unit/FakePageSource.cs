using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Pages;
using Capacitor.Cli.Core.Plans;

namespace Capacitor.App.Tests.Unit;

/// Scripted IPageSource: reads answer from a queue, or park on a gate so a test can settle them in
/// a chosen order. Every read records the id it was asked for.
sealed class FakePageSource : IPageSource {
    readonly Queue<SessionPagesRead> _scripted = new();
    readonly Queue<(TaskCompletionSource<SessionPagesRead> Source, bool IgnoreCancellation)> _gates = new();

    public readonly List<string> Requested = [];
    public SessionPagesRead Default = SessionPagesRead.Of(SessionPlansReadKind.Ready);

    public void Enqueue(params SessionPagesRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue; with
    /// ignoreCancellation a cancelled lease still receives the result, as a slow transport would.
    public TaskCompletionSource<SessionPagesRead> Gate(bool ignoreCancellation = false) {
        var gate = new TaskCompletionSource<SessionPagesRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue((gate, ignoreCancellation));
        return gate;
    }

    public SessionPagesRead? Cached(string sessionId) => null;

    public async Task<SessionPagesRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        if (_gates.Count > 0) {
            var (source, ignoreCancellation) = _gates.Dequeue();
            return ignoreCancellation ? await source.Task : await source.Task.WaitAsync(ct);
        }
        await Task.Yield();
        return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
    }

    public readonly List<(string Id, int? Version)> ContentRequested = [];
    public readonly List<(string Call, string Id, int Version, JsonElement? Payload)> BridgeRequested = [];
    readonly Queue<PageContentRead> _contents = new();
    readonly Dictionary<string, Queue<PageBridgeReply>> _replies = new();
    readonly Queue<PageResultsAccess> _accesses = new();
    public PageContentRead DefaultContent = new PageContentRead.NotFound();
    public PageBridgeReply DefaultReply = new(200, null);
    public PageResultsAccess Access = PageResultsAccess.Visible;
    public int AccessChecks;
    public string ViewerName = "";

    public void EnqueueContent(params PageContentRead[] reads) {
        foreach (var read in reads) _contents.Enqueue(read);
    }

    readonly Queue<TaskCompletionSource<PageContentRead>> _contentGates = new();

    /// The next content read awaits the returned source instead of answering from the queue.
    public TaskCompletionSource<PageContentRead> ContentGate() {
        var gate = new TaskCompletionSource<PageContentRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _contentGates.Enqueue(gate);
        return gate;
    }

    /// Replies for one call ("submit", "mine" or "results"), answered in order before DefaultReply.
    public void EnqueueReply(string call, params PageBridgeReply[] replies) {
        if (!_replies.TryGetValue(call, out var queue)) _replies[call] = queue = new();
        foreach (var reply in replies) queue.Enqueue(reply);
    }

    public void EnqueueAccess(params PageResultsAccess[] accesses) {
        foreach (var access in accesses) _accesses.Enqueue(access);
    }

    public async Task<PageContentRead> ReadContentAsync(string artefactId, int? version, CancellationToken ct) {
        ContentRequested.Add((artefactId, version));
        if (_contentGates.Count > 0) return await _contentGates.Dequeue().Task.WaitAsync(ct);
        await Task.Yield();
        return _contents.Count > 0 ? _contents.Dequeue() : DefaultContent;
    }

    public Task<PageBridgeReply> SubmitAsync(string artefactId, int version, JsonElement payload, CancellationToken ct) => Reply("submit", artefactId, version, payload);
    public Task<PageBridgeReply> MineAsync(string artefactId, int version, CancellationToken ct) => Reply("mine", artefactId, version, null);
    public Task<PageBridgeReply> ResultsAsync(string artefactId, int version, CancellationToken ct) => Reply("results", artefactId, version, null);

    public async Task<PageResultsAccess> ResultsAccessAsync(string artefactId, ArtefactDto page, CancellationToken ct) {
        AccessChecks++;
        await Task.Yield();
        return _accesses.Count > 0 ? _accesses.Dequeue() : Access;
    }

    public Task<string> ViewerNameAsync(CancellationToken ct) => Task.FromResult(ViewerName);

    async Task<PageBridgeReply> Reply(string call, string artefactId, int version, JsonElement? payload) {
        BridgeRequested.Add((call, artefactId, version, payload));
        await Task.Yield();
        return _replies.TryGetValue(call, out var queue) && queue.Count > 0 ? queue.Dequeue() : DefaultReply;
    }
}
