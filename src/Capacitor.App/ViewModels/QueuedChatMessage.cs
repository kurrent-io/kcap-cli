namespace Capacitor.App.ViewModels;

/// An outgoing message stays here until the transcript acknowledges it. Transport acceptance
/// alone does not mean the runtime has started consuming a prompt queued behind its current turn.
public sealed class QueuedChatMessage {
    public string Text { get; }
    internal string? TranscriptPath { get; }
    internal long TranscriptOffset { get; }

    public QueuedChatMessage(string text, string? transcriptPath) {
        Text = text;
        TranscriptPath = transcriptPath;
        if (transcriptPath is null) return;
        try { TranscriptOffset = new FileInfo(transcriptPath).Length; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal bool Matches(string text, string? path, long offset) =>
        (TranscriptPath != path || offset > TranscriptOffset)
        && Normalize(Text) == Normalize(text);

    static string Normalize(string text) => text.Replace("\r\n", "\n").Trim();
}
