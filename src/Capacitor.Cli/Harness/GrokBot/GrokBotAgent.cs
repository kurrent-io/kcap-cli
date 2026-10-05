namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>A chat as <c>listAgents</c> reports it: a Bot's own thread or a group chat.
/// <see cref="Fingerprint"/> changes whenever the chat gains an entry, so an unchanged one needs no
/// transcript read.</summary>
public sealed record GrokBotAgent(string Id, string Name, bool IsRunningTurn, string Fingerprint);
