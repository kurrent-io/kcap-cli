namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>One <c>chat.read_messages</c> item, with the line the server receives for it.</summary>
public sealed record MuseAssistantItem(string TurnId, string Role, long Seq, long OccurredAtMs, string? Content, string Line);
