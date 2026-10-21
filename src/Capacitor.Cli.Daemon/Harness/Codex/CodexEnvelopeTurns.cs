using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>The hosted session's human user messages, newest last, by the item id the server stores beside each
/// and the turn it opened: what the hosted judge declares its turn set from.</summary>
internal sealed class CodexEnvelopeTurns {
    const int MaxTurns = 16;

    readonly Lock _gate = new();
    readonly List<(string ItemId, string? TurnId)> _turns = [];

    public void Observe(AcpEventEnvelope envelope) {
        if (envelope is not { Kind: AcpEventKind.UserMessage, Ephemeral: false, ItemId: { Length: > 0 } itemId }
         || !CodexPrelude.IsHuman(envelope.Text ?? "", null))
            return;

        lock (_gate) {
            if (_turns.Any(t => t.ItemId == itemId)) return;
            _turns.Add((itemId, envelope.TurnId));
            if (_turns.Count > MaxTurns) _turns.RemoveAt(0);
        }
    }

    public IReadOnlyList<(string ItemId, string? TurnId)> Recent() {
        lock (_gate) return [.. _turns];
    }
}
