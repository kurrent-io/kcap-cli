using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Every input submitted to Codex, in order, awaiting the user-message item Codex records for it. Codex
/// records the text it was given and nothing else, so the oldest unclaimed input with the same text owns an item,
/// and the item carries that input's dispatch, if it came from one.</summary>
internal sealed class CodexSubmittedInputs(TimeProvider time) {
    /// <summary>An input whose item never arrives is dropped after this long. Far past any turn queue: an item
    /// arrives when its turn starts or its steer is accepted.</summary>
    internal static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);

    readonly Lock _lock = new();
    readonly LinkedList<(string Text, Guid? DispatchId, DateTimeOffset At)> _sent = new();

    public LinkedListNode<(string Text, Guid? DispatchId, DateTimeOffset At)> Add(string text, Guid? dispatchId) {
        lock (_lock) {
            var now = time.GetUtcNow();
            DropExpired(now);
            return _sent.AddLast((text, dispatchId, now));
        }
    }

    /// <summary>For an input that never reached Codex, whose item can never arrive.</summary>
    public void Remove(LinkedListNode<(string Text, Guid? DispatchId, DateTimeOffset At)> sent) {
        lock (_lock) {
            if (sent.List is not null) _sent.Remove(sent);
        }
    }

    public AcpEventEnvelope Stamp(AcpEventEnvelope envelope) {
        if (envelope is not { Kind: AcpEventKind.UserMessage, Ephemeral: false, DispatchId: null, Text: { } text }) return envelope;
        lock (_lock) {
            DropExpired(time.GetUtcNow());
            for (var node = _sent.First; node is not null; node = node.Next) {
                if (!string.Equals(node.Value.Text, text, StringComparison.Ordinal)) continue;
                _sent.Remove(node);
                return node.Value.DispatchId is { } dispatch ? envelope with { DispatchId = dispatch } : envelope;
            }
        }
        return envelope;
    }

    void DropExpired(DateTimeOffset now) {
        while (_sent.First is { } oldest && now - oldest.Value.At > Expiry) _sent.RemoveFirst();
    }
}
