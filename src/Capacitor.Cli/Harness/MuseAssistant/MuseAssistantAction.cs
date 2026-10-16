namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>One step of a run. <see cref="Intent"/> is written before the post (a lifecycle intent a
/// rerun replays), <see cref="After"/> once the server accepted it.</summary>
public abstract record MuseAssistantAction(MuseAssistantChatState? Intent, MuseAssistantChatState After);

public sealed record MuseAssistantStartSession(MuseAssistantOpenSession Session, MuseAssistantChatState? Intent, MuseAssistantChatState After)
    : MuseAssistantAction(Intent, After);

public sealed record MuseAssistantSendLines(string SessionId, string[] Lines, int[] Seqs, MuseAssistantChatState After)
    : MuseAssistantAction(null, After);

public sealed record MuseAssistantEndSession(MuseAssistantOpenSession Session, long EndedAtMs, MuseAssistantChatState? Intent, MuseAssistantChatState After)
    : MuseAssistantAction(Intent, After);

/// <summary>Nothing to post: new items, none of them selected.</summary>
public sealed record MuseAssistantAdvanceWatermark(MuseAssistantChatState After)
    : MuseAssistantAction(null, After);
