namespace Capacitor.App.ViewModels;

/// Shortens a one-line detail to a character budget. Which end goes depends on what the line is:
/// a command or a path is identified by both ends — the program and the argument, the root and
/// the file name — so the middle goes, while prose is read left to right and keeps its opening.
public static class TextElision {
    public static string Middle(string text, int max) {
        if (text.Length <= max || max < 3) return text;
        var head = max / 2;
        var tail = max - head - 1;
        // A cut between the two halves of a surrogate pair renders as a replacement box; give up
        // the char on whichever side is split rather than emit one.
        if (char.IsHighSurrogate(text[head - 1])) head--;
        if (char.IsLowSurrogate(text[^tail])) tail--;
        return string.Concat(text.AsSpan(0, head), "\u2026", text.AsSpan(text.Length - tail));
    }

    /// One cut that keeps the last two path segments — the parent directory and the file — and a
    /// short head. A second ellipsis must not be applied on top of this.
    public static string PathTail(string text, int max) {
        if (text.Length <= max || max < 4) return text;
        var file = text.LastIndexOf('/');
        var parent = file > 0 ? text.LastIndexOf('/', file - 1) : -1;
        var tail = parent >= 0 ? text[parent..] : file >= 0 ? text[file..] : text;
        if (tail.Length >= max - 1) {
            var start = text.Length - max + 1;
            if (char.IsLowSurrogate(text[start])) start++;
            return "…" + text[start..];
        }
        var head = max - tail.Length - 1;
        if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
        if (head < 1) return "…" + tail;
        return string.Concat(text.AsSpan(0, head), "…", tail);
    }

    public static string End(string text, int max) {
        if (text.Length <= max || max < 2) return text;
        var head = max - 1;
        if (char.IsHighSurrogate(text[head - 1])) head--;
        return string.Concat(text.AsSpan(0, head), "\u2026");
    }
}
