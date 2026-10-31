using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalModeTrackerTests {
    [Test]
    [Arguments("\x1b[?1006h", false)]
    [Arguments("\x1b[?1000;1006h", true)]
    [Arguments("\x1b[?1000h\x1b[?1000l", false)]
    [Arguments("\x1b[?1000h\x1b" + "c", false)]
    [Arguments("\x1b[?1000h\x1b[!p", false)]
    [Arguments("\x1b]title \x1b[?1000h\x07", false)]
    [Arguments("\x1bP\x1b[?1000h\x1b\\", false)]
    [Arguments("\x1b[?1000h\x1b]title \x1b[?1000l\x1b\\", true)]
    [Arguments("\x1b[?1000\x18h", false)]
    [Arguments("\x1b[?1000$h", false)]
    [Arguments("\x1b]cancelled\x18\x1b[?1000h", true)]
    [Arguments("\x1b[?2004h", false)]
    public async Task Mouse_modes_survive_chunk_boundaries_and_ignore_unrelated_control_sequences(string output, bool expected) {
        var tracker = new TerminalModeTracker();
        foreach (var b in Encoding.UTF8.GetBytes(output)) tracker.Feed([b]);
        await Assert.That(tracker.IsMouseReporting).IsEqualTo(expected);
    }

    /// <summary>ConPTY's own startup output carries focus reporting and win32-input mode but never
    /// bracketed paste, so only the agent itself can turn this on.</summary>
    [Test]
    [Arguments("\x1b[?2004h", true)]
    [Arguments("\x1b[?2004h\x1b[?2031h\x1b[?1004h", true)]
    [Arguments("\x1b[?2031;2004h", true)]
    [Arguments("\x1b[?9001h\x1b[?1004h\x1b[?25l\x1b[2J\x1b[m\x1b[H\x1b]0;claude\x07\x1b[?25h", false)]
    [Arguments("\x1b[?2004h\x1b[?2004l", false)]
    [Arguments("\x1b[?2004h\x1b[!p", false)]
    [Arguments("\x1b]title \x1b[?2004h\x07", false)]
    [Arguments("\x1b[?1000h", false)]
    public async Task Bracketed_paste_mode_survives_chunk_boundaries(string output, bool expected) {
        var tracker = new TerminalModeTracker();
        foreach (var b in Encoding.UTF8.GetBytes(output)) tracker.Feed([b]);
        await Assert.That(tracker.IsBracketedPasteEnabled).IsEqualTo(expected);
    }
}
