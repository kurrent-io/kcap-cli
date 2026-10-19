using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

sealed class ChatPromptFeed : IChatTranscriptFeed {
    readonly object _gate = new();
    readonly List<ProjectedLine> _lines = [];
    long _offset;
    bool _reset;
    public long? CurrentOffset { get { lock (_gate) return _offset + 1; } }
    public void Enqueue(ChatProjectionResult projection) { lock (_gate) _lines.Add(new(projection, ++_offset)); }
    public void Reset(ChatProjectionResult projection) { lock (_gate) { _lines.Clear(); _lines.Add(new(projection, _offset)); _reset = true; } }
    public FeedRead ReadAppended() {
        lock (_gate) {
            var read = new FeedRead(_reset ? FeedStatus.Reset : FeedStatus.Ok, _lines.ToArray(), _reset ? _offset + 1 : null);
            _reset = false;
            _lines.Clear();
            return read;
        }
    }
    public void Dispose() { }
}
