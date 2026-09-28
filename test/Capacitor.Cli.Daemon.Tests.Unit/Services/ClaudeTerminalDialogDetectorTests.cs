using System.Text;
using Capacitor.Cli.Daemon.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Pins the layout match for Claude's select dialogs against the bytes Claude Code writes for its
/// new-MCP-server dialog: heading, body and choices read without knowing the wording, the cursor
/// followed across an arrow key, the dialog gone once erased, and a checkbox list left unanswered.
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

        var dialog = ClaudeTerminalDialogDetector.Parse(screen);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("[ ] search");
    }
}
