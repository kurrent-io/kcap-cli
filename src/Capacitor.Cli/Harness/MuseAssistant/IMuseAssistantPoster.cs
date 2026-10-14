namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>Delivers one action to the server. Null means accepted; otherwise why it was not, in words a
/// person reads in the cron's log.</summary>
public interface IMuseAssistantPoster {
    Task<string?> SendAsync(MuseAssistantAction action, string chatId, CancellationToken ct);
}
