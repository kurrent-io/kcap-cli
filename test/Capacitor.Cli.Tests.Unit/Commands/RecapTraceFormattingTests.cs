using System.Text.Json;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>How <c>kcap recap --get-turn</c> and <c>--full</c> spell a trace: headings in one style,
/// and arguments as their text rather than as escaped JSON.</summary>
public class RecapTraceFormattingTests {
    static string Trace(string traceArrayJson) {
        using var doc = JsonDocument.Parse(traceArrayJson);

        return RecapTranscript.RenderTrace(doc.RootElement);
    }

    [Test]
    public async Task Thinking_gets_a_heading_like_the_others() {
        var text = Trace("""[ {"kind":"assistant_thinking","text":"Let me look."}, {"kind":"compaction_summary","text":"Earlier."} ]""");

        await Assert.That(text).Contains("## Thinking" + Environment.NewLine + "Let me look.");
        await Assert.That(text).Contains("## Compaction summary");
        await Assert.That(text).DoesNotContain("assistant_thinking");
    }

    [Test]
    public async Task A_windows_path_argument_prints_with_single_backslashes() {
        var text = Trace("""[ {"kind":"tool_invocation","tool":"Read","arguments":{"file_path":"C:\\work\\shop-api\\Program.cs","limit":40}} ]""");

        await Assert.That(text).Contains(@"file_path: C:\work\shop-api\Program.cs");
        await Assert.That(text).Contains("limit: 40");
        await Assert.That(text).DoesNotContain(@"C:\\work");
    }

    [Test]
    public async Task A_multi_line_argument_is_indented_under_its_name() {
        var args = JsonDocument.Parse("""{"old_string":"a\nb","new_string":"c"}""").RootElement;

        await Assert.That(RecapTranscript.Arguments(args)).IsEqualTo(
            string.Join(Environment.NewLine, "old_string:", "  a", "  b", "new_string: c", ""));
    }
}
