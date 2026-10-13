namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>What has been delivered for one Bot: the highest entry <c>seq</c> the server accepted, and
/// the session still open on its thread.</summary>
public sealed record GrokBotBotState(int LastSeq, GrokBotOpenSession? Open) {
    public static readonly GrokBotBotState Empty = new(-1, null);
}
