using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Dispatched inputs awaiting the user-message item Codex records for each. Codex records the text it
/// was given and nothing else, so the oldest unclaimed input with the same text owns an item.</summary>
internal sealed class CodexDispatchedInputs {
    /// <summary>An input whose item never arrives is evicted past this many, rather than held for the
    /// agent's lifetime.</summary>
    internal const int Capacity = 64;

    readonly Lock _lock = new();
    readonly LinkedList<(string Text, Guid DispatchId)> _sent = new();

    public LinkedListNode<(string Text, Guid DispatchId)> Add(string text, Guid dispatchId) {
        lock (_lock) {
            var node = _sent.AddLast((text, dispatchId));
            if (_sent.Count > Capacity) _sent.RemoveFirst();
            return node;
        }
    }

    /// <summary>For an input that never reached Codex, whose item can never arrive.</summary>
    public void Remove(LinkedListNode<(string Text, Guid DispatchId)> sent) {
        lock (_lock) {
            if (sent.List is not null) _sent.Remove(sent);
        }
    }

    public AcpEventEnvelope Stamp(AcpEventEnvelope envelope) {
        if (envelope is not { Kind: AcpEventKind.UserMessage, Ephemeral: false, DispatchId: null, Text: { } text }) return envelope;
        lock (_lock) {
            for (var node = _sent.First; node is not null; node = node.Next) {
                if (!string.Equals(node.Value.Text, text, StringComparison.Ordinal)) continue;
                _sent.Remove(node);
                return envelope with { DispatchId = node.Value.DispatchId };
            }
        }
        return envelope;
    }
}
