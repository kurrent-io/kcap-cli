namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>One server call the poller owes for a Bot. <see cref="After"/> is the state to persist once
/// the call succeeds, so a failure part-way leaves state at the last call that landed.</summary>
public abstract record GrokBotAction(GrokBotBotState After);

public sealed record GrokBotStartSession(string SessionId, long StartedAtMs, GrokBotBotState After) : GrokBotAction(After);

public sealed record GrokBotSendLines(string SessionId, string[] Lines, int[] Seqs, GrokBotBotState After) : GrokBotAction(After);

public sealed record GrokBotEndSession(string SessionId, long EndedAtMs, GrokBotBotState After) : GrokBotAction(After);
