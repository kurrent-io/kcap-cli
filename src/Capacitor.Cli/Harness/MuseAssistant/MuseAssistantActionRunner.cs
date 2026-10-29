namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Runs planned actions in order: the intent is on disk before the post, the result after the
/// server accepted it, and the first refusal stops the run with the state at the last accepted step.</summary>
public sealed class MuseAssistantActionRunner(IMuseAssistantPoster poster, TimeProvider time) {
    public async Task<string?> ExecuteAsync(MuseAssistantStateStore store, IReadOnlyList<MuseAssistantAction> actions, string chatId, CancellationToken ct) {
        foreach (var action in actions) {
            if (action.Intent is { } intent) store.SaveState(intent);

            if (action is not MuseAssistantAdvanceWatermark && await poster.SendAsync(action, chatId, ct) is { } refused)
                return refused;

            store.SaveState(action.After);
            Console.Error.WriteLine($"{time.GetUtcNow():O} {Describe(action)}");
        }

        return null;
    }

    static string Describe(MuseAssistantAction action) => action switch {
        MuseAssistantStartSession a     => $"opened session {a.Session.SessionId} at {Iso(a.Session.StartedAtMs)}",
        MuseAssistantSendLines a        => $"sent {a.Seqs.Length} item{(a.Seqs.Length == 1 ? "" : "s")} (seq {a.Seqs[0]}..{a.Seqs[^1]}) to {a.SessionId}",
        MuseAssistantEndSession a       => $"ended session {a.Session.SessionId} at {Iso(a.EndedAtMs)}",
        MuseAssistantAdvanceWatermark a => $"watermark {a.After.Watermark}, nothing to send",
        _                               => action.GetType().Name
    };

    static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");
}
