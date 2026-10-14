namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>How far back a grant reaches, in the chat's own order: everything above a seq, every
/// future item of a chat that had none, or the whole chat.</summary>
public static class MuseAssistantGrantBoundaries {
    public const string AfterSeq  = "after_seq";
    public const string EmptyChat = "empty_chat";
    public const string History   = "history";
}
