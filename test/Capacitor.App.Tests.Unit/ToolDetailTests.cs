using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ToolDetailTests {
    [Test]
    public async Task Expanded_commands_preserve_newlines_and_cmd_fallback() {
        await Assert.That(ToolDetail.CommandOf("""{"command":"echo first\n echo second"}""")).IsEqualTo("echo first\n echo second");
        await Assert.That(ToolDetail.CommandOf("""{"command":" ","cmd":"cat <<EOF\nhello\nEOF"}""")).IsEqualTo("cat <<EOF\nhello\nEOF");
        await Assert.That(ToolDetail.From("""{"command":"echo first\n echo second"}""")).IsEqualTo("echo first");
    }

    [Test]
    public async Task Path_elision_preserves_surrogate_boundaries() {
        var tail = "/" + new string('a', 48) + "/file";
        await Assert.That(TextElision.PathTail("😀a" + tail, 56)).IsEqualTo("…" + tail);
        var path = "abcdef😀" + new string('a', 54);
        var cut = TextElision.PathTail(path, 56);
        await Assert.That(cut.Length).IsLessThanOrEqualTo(56);
        await Assert.That(char.IsLowSurrogate(cut[1])).IsFalse();
    }

    [Test]
    public async Task Picks_the_first_present_key_in_priority_order() {
        await Assert.That(ToolDetail.From("""{"command":"ls","description":"List files"}""")).IsEqualTo("List files");
        await Assert.That(ToolDetail.From("""{"file_path":"/a/b.cs","command":"x"}""")).IsEqualTo("x");
        await Assert.That(ToolDetail.From("""{"pattern":"*.cs"}""")).IsEqualTo("*.cs");
        await Assert.That(ToolDetail.From("""{"input":"const r = 1;"}""")).IsEqualTo("const r = 1;");
    }

    /// The cut lands in the middle, so the tail of a long command — the file, the flag, the
    /// argument it acts on — survives the 80-character budget.
    [Test]
    public async Task Keeps_the_first_line_and_elides_the_middle_at_80_characters() {
        await Assert.That(ToolDetail.From("""{"command":"first line\nsecond"}""")).IsEqualTo("first line");
        var longLine = new string('x', 60) + new string('y', 40);
        var detail = ToolDetail.From($$"""{"command":"{{longLine}}"}""");
        await Assert.That(detail.Length).IsEqualTo(80);
        await Assert.That(detail).IsEqualTo(new string('x', 40) + "…" + new string('y', 39));
    }

    [Test]
    public async Task AskUserQuestion_uses_the_first_question_text() {
        await Assert.That(ToolDetail.From("""{"questions":[{"question":"Pick one","options":[{"label":"A"}]}]}"""))
            .IsEqualTo("Pick one");
    }

    [Test]
    public async Task Request_user_input_prompt_uses_question_prose_elision() {
        var prompt = new string('q', 450);
        var detail = ToolDetail.From($$"""{"prompt":"{{prompt}}"}""", category: ToolCategory.Question);
        await Assert.That(detail.Length).IsEqualTo(400);
        await Assert.That(detail).IsEqualTo(new string('q', 399) + "\u2026");
    }

    [Test]
    public async Task A_long_path_is_cut_once_and_keeps_its_parent_and_file_name() {
        var path = "/private/tmp/claude-501/sessions/very-long-middle-segment-that-must-go/scratchpad/demo.txt";
        var detail = ToolDetail.From($$"""{"file_path":"{{path}}"}""");
        await Assert.That(detail.Count(c => c == '…')).IsEqualTo(1);
        await Assert.That(detail.EndsWith("/scratchpad/demo.txt", StringComparison.Ordinal)).IsTrue();
        await Assert.That(detail.Length).IsLessThanOrEqualTo(56);
    }

    [Test]
    public async Task CommandOf_keeps_the_command_when_the_row_shows_the_description() {
        await Assert.That(ToolDetail.CommandOf("""{"description":"List files","command":"ls /missing"}""")).IsEqualTo("ls /missing");
        await Assert.That(ToolDetail.ShowsPath("""{"file_path":"/tmp/a.png"}""")).IsTrue();
        await Assert.That(ToolDetail.ShowsPath("""{"command":"ls"}""")).IsFalse();
    }

    [Test]
    public async Task Empty_when_nothing_applies() {
        await Assert.That(ToolDetail.From("""{"other":"x"}""")).IsEqualTo("");
        await Assert.That(ToolDetail.From("""{"command":"   "}""")).IsEqualTo("");
        await Assert.That(ToolDetail.From("not json")).IsEqualTo("");
        await Assert.That(ToolDetail.From(null)).IsEqualTo("");
    }

    /// Pins path display: a path under the session's root reads relative to it — the daemon's
    /// per-agent worktree under the repository counts as the root — while other keys and paths
    /// elsewhere are left alone.
    [Test]
    public async Task File_paths_read_relative_to_the_sessions_root() {
        const string repo = "/Users/me/dev/repo";
        await Assert.That(ToolDetail.From("""{"file_path":"/Users/me/dev/repo/.capacitor/worktrees/agent-1/src/Foo.cs"}""", repo)).IsEqualTo("src/Foo.cs");
        await Assert.That(ToolDetail.From("""{"file_path":"/Users/me/dev/repo/src/Foo.cs"}""", repo)).IsEqualTo("src/Foo.cs");
        await Assert.That(ToolDetail.From("""{"path":"/Users/me/dev/repo/.capacitor/worktrees/agent-1/docs"}""", repo)).IsEqualTo("docs");
        await Assert.That(ToolDetail.From("""{"notebook_path":"/Users/me/dev/repo/n.ipynb"}""", repo)).IsEqualTo("n.ipynb");
        await Assert.That(ToolDetail.From("""{"file_path":"/elsewhere/x.cs"}""", repo)).IsEqualTo("/elsewhere/x.cs");
        await Assert.That(ToolDetail.From("""{"command":"cat /Users/me/dev/repo/x"}""", repo)).IsEqualTo("cat /Users/me/dev/repo/x");
        await Assert.That(ToolDetail.From("""{"file_path":"/Users/me/dev/repo/src/Foo.cs"}""", null)).IsEqualTo("/Users/me/dev/repo/src/Foo.cs");
    }

    [Test]
    public async Task ForKey_reads_the_named_string_first_line_elided_and_nothing_else() {
        var long120 = new string('a', 120);
        await Assert.That(ToolDetail.ForKey("""{"issue_key":"WK-3084","title":"x"}""", "issue_key")).IsEqualTo("WK-3084");
        await Assert.That(ToolDetail.ForKey("""{"prompt":"Fix the flaky test\nthen push"}""", "prompt")).IsEqualTo("Fix the flaky test");
        await Assert.That(ToolDetail.ForKey("""{"title":"x"}""", "issue_key")).IsEqualTo("");
        await Assert.That(ToolDetail.ForKey("""{"title":"x"}""", null)).IsEqualTo("");
        await Assert.That(ToolDetail.ForKey("not json", "title")).IsEqualTo("");
        await Assert.That(ToolDetail.ForKey($$"""{"query":"{{long120}}"}""", "query").Length).IsLessThanOrEqualTo(80);
    }

    [Test]
    public async Task FirstString_takes_the_first_non_empty_string_property() {
        await Assert.That(ToolDetail.FirstString("""{"n":1,"team":"","title":"Add tests","body":"long"}""")).IsEqualTo("Add tests");
        await Assert.That(ToolDetail.FirstString("""{"n":1}""")).IsEqualTo("");
        await Assert.That(ToolDetail.FirstString(null)).IsEqualTo("");
    }
}
