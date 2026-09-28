using System.Text;
using Capacitor.Cli.Daemon.Harness.Claude;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// Pins the layout match for Claude's select dialogs against the bytes Claude Code writes for its
/// new-MCP-server dialog: heading, body and choices read without knowing the wording, the cursor
/// followed across an arrow key, the dialog gone once erased, a checkbox list left unanswered, and
/// labels Claude wrapped in a narrow terminal read whole.
public class ClaudeTerminalDialogDetectorTests {
    // The first render, word by word at absolute columns as Claude Code draws it.
    static readonly string Dialog =
        "\x1b[?2004h\r\r\n\x1b[38;2;255;193;7m" + new string('─', 120) + "\x1b[39m\r\r\n" +
        "\x1b[3G\x1b[1mNew\x1b[7GMCP\x1b[11Gserver\x1b[18Gfound\x1b[24Gin\x1b[27Gthis\x1b[32Gproject:\x1b[41Gavalonia-docs\x1b[22m\r\r\n\r\r\n" +
        "\x1b[3GMCP\x1b[7Gservers\x1b[15Gmay\x1b[19Gexecute\x1b[27Gcode\x1b[32Gor\x1b[35Gaccess\x1b[42Gsystem\x1b[49Gresources.\x1b[60GAll tool calls require approval. Learn more in the MCP\r\r\n" +
        "\x1b[3Gdocumentation.\r\r\n\r\r\n" +
        "\x1b[5GUse\x1b[9Gthis\x1b[14GMCP\x1b[18Gserver\r\r\n" +
        "\x1b[5GUse\x1b[9Gthis\x1b[14Gand\x1b[18Gall\x1b[22Gfuture\x1b[29GMCP\x1b[33Gservers\x1b[41Gin\x1b[44Gthis\x1b[49Gproject\r\r\n" +
        "\x1b[3G\x1b[38;2;177;185;249m❯\x1b[5GContinue\x1b[14Gwithout\x1b[22Gusing\x1b[28Gthis\x1b[33GMCP\x1b[37Gserver\x1b[39m\r\r\n\r\r\n" +
        "\x1b[3G\x1b[3mEnter\x1b[9Gto\x1b[12Gconfirm\x1b[20G·\x1b[22GEsc\x1b[26Gto\x1b[29Gcancel\x1b[23m\r\r\n\x1b[2C\x1b[3A\x1b[>0q\x1b[?u\x1b[c";

    // The redraw after one Up arrow.
    const string CursorUp =
        "\x1b[2D\x1b[3B\r\x1b[2C\x1b[4A\x1b[38;2;177;185;249m❯\x1b[5GUse this and all future MCP servers in this project" +
        "\r\x1b[2C\x1b[1B\x1b[39m \x1b[5GContinue without using this MCP server\r\r\n\r\n\r\n\x1b[2C\x1b[4A";

    static readonly string Erase =
        "\x1b[2D\x1b[4B" + string.Concat(Enumerable.Repeat("\x1b[2K\x1b[1A", 12)) + "\x1b[2K\x1b[G";

    // The same dialog drawn in a 40-column PTY, where the heading and two of the labels wrap.
    const string Narrow =
        "\u001b[?2004h\u001b[?2031h\u001b[?1004h\r\r\n" +
        "\u001b[38;5;220m────────────────────────────────────────\u001b[39m\r\r\n" +
        "\u001b[3G\u001b[38;5;220m\u001b[1mNew\u001b[7GMCP\u001b[11Gserver\u001b[18Gfound\u001b[24Gin\u001b[27Gthis\u001b[22m\u001b[39m\r\r\n" +
        "\u001b[3G\u001b[38;5;220m\u001b[1mproject:\u001b[12Ga-server-with-a-rather-long\u001b[22m\u001b[39m\r\r\n" +
        "\u001b[3G\u001b[38;5;220m\u001b[1m-name-for-wrapping-tests\u001b[22m\u001b[39m\r\r\n" +
        "\r\r\n" +
        "\u001b[3GMCP\u001b[7Gservers\u001b[15Gmay\u001b[19Gexecute\u001b[27Gcode\u001b[32Gor\r\r\n" +
        "\u001b[3Gaccess\u001b[10Gsystem\u001b[17Gresources.\u001b[28GAll\u001b[32Gtool\r\r\n" +
        "\u001b[3Gcalls\u001b[9Grequire\u001b[17Gapproval.\u001b[27GLearn\u001b[33Gmore\r\r\n" +
        "\u001b[3Gin\u001b[6Gthe\u001b[10GMCP\u001b[14Gdocumentation.\r\r\n" +
        "\r\r\n" +
        "\u001b[5GUse\u001b[9Gthis\u001b[14GMCP\u001b[18Gserver\r\r\n" +
        "\u001b[5GUse\u001b[9Gthis\u001b[14Gand\u001b[18Gall\u001b[22Gfuture\u001b[29GMCP\u001b[33Gservers\r\r\n" +
        "\u001b[5Gin\u001b[8Gthis\u001b[13Gproject\r\r\n" +
        "\u001b[3G\u001b[38;5;153m❯\u001b[5GContinue\u001b[14Gwithout\u001b[22Gusing\u001b[28Gthis\u001b[33GMCP\u001b[39m\r\r\n" +
        "\u001b[5G\u001b[38;5;153mserver\u001b[39m\r\r\n" +
        "\r\r\n" +
        "\u001b[3G\u001b[38;5;246m\u001b[3mEnter\u001b[9Gto\u001b[12Gconfirm\u001b[20G·\u001b[22GEsc\u001b[26Gto\u001b[29Gcancel\u001b[23m\u001b[39m\r\r\n" +
        "\u001b[2C\u001b[4A\u001b[>0q\u001b[c";

    // The redraw after one Up arrow in that PTY, cut where the new cursor is drawn and the old one
    // not yet erased.
    const string NarrowCursorUpDrawn =
        "\u001b[2D\u001b[4B\r\u001b[2C\u001b[6A\u001b[38;5;153m❯\u001b[5GUse this and all future MCP servers \r\u001b[4C\u001b[1Bin this project";
    const string NarrowCursorUpErased =
        "\r\u001b[2C\u001b[1B\u001b[39m \u001b[5GContinue without using this MCP\u001b[K\r\u001b[4C\u001b[1Bserver\r\r\n\r\n\r\n\u001b[2C\u001b[6A";

    static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Test]
    public async Task A_dialog_is_read_by_its_layout_with_the_cursor_on_the_default() {
        var dialog = new ClaudeScreenWatcher().Observe(Utf8(Dialog)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("New MCP server found in this project: avalonia-docs");
        await Assert.That(dialog.Body).IsEqualTo("MCP servers may execute code or access system resources. All tool calls require approval. Learn more in the MCP documentation.");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] {
            "Use this MCP server",
            "Use this and all future MCP servers in this project",
            "Continue without using this MCP server",
        }, CollectionOrdering.Matching);
        await Assert.That(dialog.Selected).IsEqualTo(2);
    }

    [Test]
    public async Task The_selection_follows_the_cursor_and_the_erased_dialog_clears() {
        var screen = new ClaudeScreenWatcher();
        screen.Observe(Utf8(Dialog));

        await Assert.That(screen.Observe(Utf8(CursorUp)).Dialog?.Selected).IsEqualTo(1);
        await Assert.That(screen.Observe(Utf8(Erase)).Dialog).IsNull();
    }

    [Test]
    public async Task A_checkbox_list_comes_back_with_its_screen_and_no_choices() {
        var screen = string.Join('\n',
            new string('─', 40),
            "  2 new MCP servers found in this project",
            "",
            "  ❯ [ ] docs",
            "    [ ] search",
            "",
            "  Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("[ ] search");
    }

    [Test]
    public async Task Labels_wrapped_at_the_terminal_width_are_one_choice_each() {
        var screen = new ClaudeScreenWatcher(40, 40);

        var dialog = screen.Observe(Utf8(Narrow)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).StartsWith("New MCP server found in this project: a-server-with-a-rather-long");
        await Assert.That(dialog.Body).StartsWith("MCP servers may execute code or access system resources.");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] {
            "Use this MCP server",
            "Use this and all future MCP servers in this project",
            "Continue without using this MCP server",
        }, CollectionOrdering.Matching);
        await Assert.That(dialog.Selected).IsEqualTo(2);
    }

    /// The PTY follows whichever viewer attached, so the copy is resized with it; read at a width
    /// other than the one Claude drew for, a wrapped label would count as a choice of its own.
    [Test]
    public async Task A_resized_screen_reads_the_dialog_at_its_new_width() {
        var screen = new ClaudeScreenWatcher();
        screen.Resize(40, 40);

        var dialog = screen.Observe(Utf8(Narrow)).Dialog;

        await Assert.That(dialog!.Options).Count().IsEqualTo(3);
    }

    [Test]
    public async Task A_chunk_that_ends_mid_redraw_keeps_the_readable_choices() {
        var screen = new ClaudeScreenWatcher(40, 40);
        screen.Observe(Utf8(Narrow));

        var midway = screen.Observe(Utf8(NarrowCursorUpDrawn)).Dialog;
        await Assert.That(midway!.Options).Count().IsEqualTo(3);
        await Assert.That(midway.Selected).IsEqualTo(2);

        await Assert.That(screen.Observe(Utf8(NarrowCursorUpErased)).Dialog?.Selected).IsEqualTo(1);
    }

    [Test]
    public async Task Output_that_quotes_the_footer_is_not_a_dialog() {
        var quoted = string.Join('\n',
            new string('─', 40),
            "  The footer reads:",
            "  Enter to confirm · Esc to cancel",
            "",
            new string('─', 40),
            "❯ ",
            new string('─', 40),
            "  ? for shortcuts");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(quoted, 120)).IsNull();
    }

    [Test]
    public async Task A_footer_with_no_rule_above_it_is_not_a_dialog() {
        var screen = string.Join('\n', "  Use this one", "  ❯ Or this one", "", "  Enter to confirm · Esc to cancel");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(screen, 120)).IsNull();
    }
}
