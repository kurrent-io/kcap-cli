using Capacitor.Cli.Harness.MuseAssistant;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Stands in for the server: records what it accepted, refuses what the test says, and can die
/// right after accepting a call — the moment between a 2xx and the state write.</summary>
sealed class RecordingPoster : IMuseAssistantPoster {
    public readonly List<MuseAssistantAction> Accepted = [];

    public Func<MuseAssistantAction, int, string?>? Refuse { get; init; }
    public int CrashAfterAccepting { get; init; } = -1;

    public Task<string?> SendAsync(MuseAssistantAction action, string chatId, CancellationToken ct) {
        var index = Accepted.Count;
        if (Refuse?.Invoke(action, index) is { } refusal) return Task.FromResult<string?>(refusal);

        Accepted.Add(action);
        if (index == CrashAfterAccepting) throw new SimulatedCrash();

        return Task.FromResult<string?>(null);
    }

    public IEnumerable<string> Starts => Accepted.OfType<MuseAssistantStartSession>().Select(a => a.Session.SessionId);
    public IEnumerable<string> Ends   => Accepted.OfType<MuseAssistantEndSession>().Select(a => a.Session.SessionId);
    public IEnumerable<int>    Seqs   => Accepted.OfType<MuseAssistantSendLines>().SelectMany(a => a.Seqs);
    public IEnumerable<string> Kinds  => Accepted.Select(a => a.GetType().Name);
}
