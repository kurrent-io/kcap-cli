using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// Keeps one emulated copy of a Claude agent's screen and reads the menus it shows off it. Every
/// reader shares the screen, so a chunk is emulated once however many menus are looked for.
internal sealed class ClaudeScreenWatcher {
    readonly AnsiScreen _screen;
    TerminalDialogDto? _dialog;

    public ClaudeScreenWatcher(int cols = PtyDefaults.Cols, int rows = PtyDefaults.Rows) {
        if (!AnsiScreen.Fits(cols, rows)) (cols, rows) = (PtyDefaults.Cols, PtyDefaults.Rows);
        _screen = new AnsiScreen(cols, rows);
    }

    /// The copy must stay the PTY's size: Claude's redraws move the cursor relative to the width it
    /// drew for, and that width decides where a label wrapped. A size that cannot be allocated is
    /// ignored, so the read loop that owns this screen keeps running.
    public void Resize(int cols, int rows) {
        if (AnsiScreen.Fits(cols, rows)) _screen.Resize(cols, rows);
    }

    public (UsageLimitNoticeDto? UsageLimit, TerminalDialogDto? Dialog) Observe(ReadOnlySpan<byte> chunk) {
        _screen.Write(chunk);
        var text = _screen.Text();
        var read = ClaudeTerminalDialogDetector.Read(text, _screen.Cols);
        var dialog = read.Dialog;
        // A chunk can end with the cursor on its new row and still on the old one, so the choices
        // already read stay until the heading changes. A finished menu with no choices, such as a
        // checkbox list, is a different dialog and must not inherit them.
        if (read.IncompleteRedraw && dialog is not null
            && _dialog is { Options.Count: > 0 } && _dialog.Heading == dialog.Heading)
            dialog = _dialog;
        _dialog = dialog;
        return (ClaudeUsageLimitDetector.Parse(text), dialog);
    }
}
