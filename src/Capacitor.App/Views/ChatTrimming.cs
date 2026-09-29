using Avalonia.Media;

namespace Capacitor.App.Views;

/// Overflow trimming for the one-line command and path rows in Chat. The prefix holds the program
/// and its first token; everything dropped comes out of the middle, so the target at the end of
/// the line — the file, the pattern, the argument the row is about — stays on screen.
public static class ChatTrimming {
    public static TextTrimming MiddleEllipsis { get; } = new TextLeadingPrefixTrimming("…", 24);
}
