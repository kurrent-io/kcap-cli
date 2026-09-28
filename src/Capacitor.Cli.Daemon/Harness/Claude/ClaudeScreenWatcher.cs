using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// Keeps one emulated copy of a Claude agent's screen and reads the menus it shows off it. Every
/// reader shares the screen, so a chunk is emulated once however many menus are looked for.
internal sealed class ClaudeScreenWatcher(int cols = PtyDefaults.Cols, int rows = PtyDefaults.Rows) {
    readonly AnsiScreen _screen = new(cols > 0 ? cols : PtyDefaults.Cols, rows > 0 ? rows : PtyDefaults.Rows);
    TerminalDialogDto? _dialog;

    /// The copy must stay the PTY's size: Claude's redraws move the cursor relative to the width it
    /// drew for, and that width decides where a label wrapped.
    public void Resize(int cols, int rows) {
        if (cols > 0 && rows > 0) _screen.Resize(cols, rows);
    }

    public (UsageLimitNoticeDto? UsageLimit, TerminalDialogDto? Dialog) Observe(ReadOnlySpan<byte> chunk) {
        _screen.Write(chunk);
        var text   = _screen.Text();
        var dialog = ClaudeTerminalDialogDetector.Parse(text, _screen.Cols);
        // A chunk can end mid-redraw, with the cursor drawn on its new row and not yet erased from
        // the old one. A dialog whose choices were readable keeps them until its heading changes.
        if (dialog is { Options.Count: 0 } && _dialog is { Options.Count: > 0 } && _dialog.Heading == dialog.Heading)
            dialog = _dialog;
        _dialog = dialog;
        return (ClaudeUsageLimitDetector.Parse(text), dialog);
    }
}
