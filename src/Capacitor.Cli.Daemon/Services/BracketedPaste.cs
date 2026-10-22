namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Frames text as one bracketed paste (ESC[200~ … ESC[201~), which a TUI takes as a single block. A
/// paste delimiter inside the text, such as captured terminal output, would end the paste early and
/// turn the rest into keystrokes, so its ESC is dropped: the composer shows "[201~" where the
/// sequence was and the paste stays whole.
/// </summary>
internal static class BracketedPaste {
    const string Start = "\x1b[200~";
    const string End   = "\x1b[201~";

    internal static string Frame(string text) =>
        Start
      + text.Replace(Start, Start[1..], StringComparison.Ordinal).Replace(End, End[1..], StringComparison.Ordinal)
      + End;
}
