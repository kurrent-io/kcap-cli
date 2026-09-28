using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Services;

/// Keeps one emulated copy of a Claude agent's screen and reads the menus it shows off it. Every
/// reader shares the screen, so a chunk is emulated once however many menus are looked for.
internal sealed class ClaudeScreenWatcher {
    readonly AnsiScreen _screen = new(PtyDefaults.Cols, PtyDefaults.Rows);
    TerminalDialogDto? _dialog;

    public (UsageLimitNoticeDto? UsageLimit, TerminalDialogDto? Dialog) Observe(ReadOnlySpan<byte> chunk) {
        _screen.Write(chunk);
        var text   = _screen.Text();
        var dialog = ClaudeTerminalDialogDetector.Parse(text);
        // A chunk can end mid-redraw, with the cursor drawn on its new row and not yet erased from
        // the old one. A dialog whose choices were readable keeps them until its heading changes.
        if (dialog is { Options.Count: 0 } && _dialog is { Options.Count: > 0 } && _dialog.Heading == dialog.Heading)
            dialog = _dialog;
        _dialog = dialog;
        return (ClaudeUsageLimitDetector.Parse(text), dialog);
    }
}
