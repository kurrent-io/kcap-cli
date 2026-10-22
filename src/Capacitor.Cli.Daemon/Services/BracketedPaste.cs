namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Frames text as one bracketed paste (ESC[200~ … ESC[201~), which a TUI takes as a single block. A
/// paste delimiter inside the text, such as captured terminal output, would end the paste early and
/// turn the rest into keystrokes, so every ESC is dropped: removing only a delimiter's own ESC lets
/// the one before it form a new delimiter, and a composer has no use for an escape sequence anyway.
/// The composer shows "[201~" where the sequence was and the paste stays whole.
/// </summary>
internal static class BracketedPaste {
    const string Start = "\x1b[200~";
    const string End   = "\x1b[201~";

    internal static string Frame(string text) => Start + text.Replace("\x1b", "", StringComparison.Ordinal) + End;
}
