using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalMouseModeTrackerTests {
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
    public async Task Mouse_modes_survive_chunk_boundaries_and_ignore_unrelated_control_sequences(string output, bool expected) {
        var tracker = new TerminalMouseModeTracker();
        foreach (var b in Encoding.UTF8.GetBytes(output)) tracker.Feed([b]);
        await Assert.That(tracker.IsActive).IsEqualTo(expected);
    }
}
