namespace Capacitor.Cli.Harness.GrokBot;

/// <summary>The session currently open on one Bot's thread.</summary>
public sealed record GrokBotOpenSession(string SessionId, string FirstEntryId, long StartedAtMs, long LastEntryMs);
