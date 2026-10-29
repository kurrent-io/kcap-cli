using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Rounds sent to a flow participant while its resume has not yet published it, in arrival
/// order. Answering them <c>unknown_agent</c> would be read by the server as proof the participant is
/// gone. Guarded by the orchestrator's ledger lock.</summary>
internal sealed class HeldParticipantInput {
    internal const int Capacity = 8;

    readonly Queue<SendInputCommand> _held = new();

    public bool TryHold(SendInputCommand cmd) {
        if (_held.Count >= Capacity) return false;

        _held.Enqueue(cmd);

        return true;
    }

    public bool TryTake(out SendInputCommand cmd) => _held.TryDequeue(out cmd);
}
