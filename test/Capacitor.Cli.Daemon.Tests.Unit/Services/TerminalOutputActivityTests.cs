using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalOutputActivityTests {
    [Test]
    [Arguments("\x1b(B\x0f\x1b[?2004h\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h")]
    [Arguments("\x1b[?25l\x1b[H\r\x1b[39C\x1b[49B\x1b[K\x1b[55;1H\x1b[53;3H\x1b[?25h")]
    [Arguments("\x1b]0;window title\a\x1b]10;rgb:ffff/ffff/ffff\x1b\\")]
    [Arguments("\x1bPterminal payload\x1b\\\x1b_hidden payload\x1b\\")]
    [Arguments("\x1b^message\x1b\\\x1bXstring\x1b\\\r\n\t \b\x7f")]
    public async Task Housekeeping_does_not_report_task_output_at_any_chunk_boundary(string controls) {
        var bytes = Encoding.UTF8.GetBytes(controls);
        for (var split = 0; split <= bytes.Length; split++) {
            var activity = new TerminalOutputActivity();
            await Assert.That(activity.Observe(bytes.AsSpan(0, split))).IsFalse();
            await Assert.That(activity.Observe(bytes.AsSpan(split))).IsFalse();
            await Assert.That(activity.Observe("real output"u8)).IsTrue();
        }
    }

    [Test]
    [Arguments("new output")]
    [Arguments("\x1b[32mworking\x1b[0m")]
    [Arguments("\x1b[H✻ thinking")]
    [Arguments("\x1b]title\x18new output")]
    public async Task Visible_output_is_activity(string text) {
        var activity = new TerminalOutputActivity();
        await Assert.That(activity.Observe(Encoding.UTF8.GetBytes(text))).IsTrue();
    }

    [Test]
    public async Task Parsing_continues_after_text_into_a_trailing_control_sequence() {
        var activity = new TerminalOutputActivity();
        await Assert.That(activity.Observe("text\x1b]"u8)).IsTrue();
        await Assert.That(activity.Observe("only a title"u8)).IsFalse();
        await Assert.That(activity.Observe("\aactual output"u8)).IsTrue();
    }
}
