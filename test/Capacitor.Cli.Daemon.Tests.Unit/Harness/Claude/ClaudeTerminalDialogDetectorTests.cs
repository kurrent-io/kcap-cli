using System.Text;
using Capacitor.Cli.Daemon.Harness.Claude;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// Pins dialog detection and the screen-only fallback when keystrokes cannot be inferred safely.
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
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task An_auto_mode_settings_form_has_a_screen_but_no_single_choice_answers(int selected) {
        string[] settings = [
            "How you use Claude here     Mixed",
            "Also scan shell history     true",
            "Also scan your other repos  false",
        ];
        var form = string.Join('\n', new[] {
            new string('─', 120),
            "  Teach auto mode about your environment?",
            "",
            "  Claude Code reads this project, your recent Claude sessions, and optionally your shell history and other repositories.",
            "  Claude analyzes this data and customizes auto mode to make better decisions.",
            "",
        }.Concat(settings.Select((setting, i) => (selected == i ? "  ❯ " : "    ") + setting)).Concat([
            "",
            (selected == 3 ? "  ❯ " : "    ") + "Continue",
            "",
            "  ←/→ to change · Enter to continue · Esc to cancel",
        ]));
        var screen = new ClaudeScreenWatcher();
        var split = form.IndexOf("Enter to continue", StringComparison.Ordinal);
        await Assert.That(screen.Observe(Utf8(form[..split])).Dialog).IsNull();

        var dialog = screen.Observe(Utf8(form[split..])).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Teach auto mode about your environment?");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Selected).IsEqualTo(-1);
        await Assert.That(dialog.Screen).Contains("Also scan shell history     true");
        await Assert.That(dialog.Screen).Contains("Continue");
        await Assert.That(screen.Observe(Utf8("\x1b[2Jready\r\n")).Dialog).IsNull();
    }

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

    /// A checkbox list is a finished menu, not a cursor redraw. Same heading or not, it must not
    /// keep the select choices the previous screen had, or Chat would send those keys to it.
    [Test]
    public async Task A_checkbox_dialog_does_not_keep_the_previous_select_choices() {
        var screen = new ClaudeScreenWatcher();
        var select = string.Join('\n',
            new string('─', 40),
            "  Pick a server",
            "",
            "    Use this one",
            "  ❯ Use the other",
            "",
            "  Enter to confirm · Esc to cancel");
        screen.Observe(Utf8(select));
        await Assert.That(screen.Observe(Utf8("")).Dialog!.Options).Count().IsEqualTo(2);

        var checkbox = "\x1b[2J" + string.Join('\n',
            new string('─', 40),
            "  Pick a server",
            "",
            "  ❯ [ ] docs",
            "    [ ] search",
            "",
            "  Enter to confirm · Esc to cancel");
        var dialog = screen.Observe(Utf8(checkbox)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("[ ] search");
    }

    /// The cell count is the allocation. A winsize at the top of ushort wraps that product.
    [Test]
    public async Task A_resize_whose_cell_count_does_not_fit_keeps_the_dialog() {
        var screen = new ClaudeScreenWatcher(ushort.MaxValue, ushort.MaxValue);
        screen.Observe(Utf8(Dialog));
        await Assert.That(screen.Observe(Utf8("")).Dialog!.Options).Count().IsEqualTo(3);

        screen.Resize(ushort.MaxValue, ushort.MaxValue);
        await Assert.That(screen.Observe(Utf8("")).Dialog!.Options).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Same_column_wrapping_uses_the_screen_instead_of_guessing_choice_boundaries() {
        var screen = new ClaudeScreenWatcher(40, 40);

        var dialog = screen.Observe(Utf8(Narrow)).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).StartsWith("New MCP server found in this project: a-server-with-a-rather-long");
        await Assert.That(dialog.Body).StartsWith("MCP servers may execute code or access system resources.");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Selected).IsEqualTo(-1);
        await Assert.That(dialog.Screen).Contains("in this project");
        await Assert.That(dialog.Screen).Contains("Continue without using this MCP");
        await Assert.That(screen.Observe(Utf8(NarrowCursorUpDrawn)).Dialog!.Options).IsEmpty();
        await Assert.That(screen.Observe(Utf8(NarrowCursorUpErased)).Dialog!.Options).IsEmpty();
    }

    [Test]
    [Arguments(60, 0, 0)]
    [Arguments(60, 1, 0)]
    [Arguments(60, 2, 3)]
    [Arguments(120, 0, 3)]
    [Arguments(120, 1, 3)]
    [Arguments(120, 2, 3)]
    public async Task An_ambiguous_row_never_merges_the_decline_choice_into_an_approval(int cols, int selected, int count) {
        string[] labels = [
            "Use this MCP server",
            "Use this and all future MCP servers in this project",
            "Continue without using this MCP server",
        ];
        string Menu(int cursor) => string.Join('\n', new[] {
            new string('─', cols),
            "  New MCP server found in this project: docs",
            "",
        }.Concat(labels.Select((label, i) => (cursor == i ? "  ❯ " : "    ") + label)).Concat([
            "", "  Enter to confirm · Esc to cancel",
        ]));
        var screen = new ClaudeScreenWatcher();
        screen.Resize(cols, 40);
        await Assert.That(screen.Observe(Utf8(Menu(2))).Dialog!.Options).Count().IsEqualTo(3);

        var dialog = screen.Observe(Utf8("\x1b[2J" + Menu(selected))).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).Count().IsEqualTo(count);
        await Assert.That(dialog.Selected).IsEqualTo(count == 0 ? -1 : selected);
        await Assert.That(dialog.Screen).Contains(labels[1] + "\n");
        await Assert.That(dialog.Screen).Contains(labels[2]);
        if (count > 0)
            await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(labels, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_chunk_that_ends_mid_redraw_keeps_the_readable_choices() {
        var screen = new ClaudeScreenWatcher();
        screen.Observe(Utf8(Dialog));
        var split = CursorUp.IndexOf("\r\x1b[2C\x1b[1B", StringComparison.Ordinal);

        var midway = screen.Observe(Utf8(CursorUp[..split])).Dialog;
        await Assert.That(midway!.Options).Count().IsEqualTo(3);
        await Assert.That(midway.Selected).IsEqualTo(2);

        await Assert.That(screen.Observe(Utf8(CursorUp[split..])).Dialog?.Selected).IsEqualTo(1);
    }

    [Test]
    [Arguments("Enter to confirm · Esc to cancel")]
    [Arguments("←/→ to change · Enter to continue · Esc to cancel")]
    public async Task Output_that_quotes_the_footer_is_not_a_dialog(string footer) {
        var quoted = string.Join('\n',
            new string('─', 40),
            "  The footer reads:",
            "  " + footer,
            "",
            new string('─', 40),
            "❯ ",
            new string('─', 40),
            "  ? for shortcuts");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(quoted, 120)).IsNull();
    }

    [Test]
    [Arguments("Enter to confirm · Esc to cancel")]
    [Arguments("←/→ to change · Enter to continue · Esc to cancel")]
    public async Task A_footer_with_no_rule_above_it_is_not_a_dialog(string footer) {
        var screen = string.Join('\n', "  Use this one", "  ❯ Or this one", "", "  " + footer);

        await Assert.That(ClaudeTerminalDialogDetector.Parse(screen, 120)).IsNull();
    }

    // The fullscreen TUI's settings panel as /status draws it, at the daemon's 120 columns.
    static string Panel(params string[] rows) => string.Join('\n', new[] {
        "❯ /status",
        "",
        new string('▔', 120),
        "   Settings  Status   Config   Usage   Stats",
        "",
    }.Concat(rows));

    /// /status and /cost open a panel that writes nothing to the transcript, and a prompt pasted
    /// while it is open is swallowed. The panel takes no answer, so only its screen comes back.
    [Test]
    [Arguments("Esc to cancel")]
    [Arguments("Enter/Space to change · / to search · Esc to close")]
    [Arguments("Enter to set as default · s to use this session only · Esc to cancel")]
    public async Task A_fullscreen_panel_comes_back_with_its_screen_and_no_choices(string footer) {
        var screen = Panel(
            "   Version:           2.1.310",
            "   Session kind:      interactive",
            "   Model:             Default",
            "",
            "   " + footer);

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Settings  Status   Config   Usage   Stats");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Session kind:      interactive");
        await Assert.That(dialog.Screen).DoesNotContain(footer);
    }

    /// The usage tab outgrows a 40-row screen, so its footer is scrolled off and the last row is
    /// content ending in the scroll mark.
    [Test]
    public async Task A_panel_scrolled_past_its_footer_still_holds_the_keyboard() {
        var screen = Panel(
            "   Current session",
            "   █████████████████████                             42% used",
            "",
            "   Trim ~/repo/CLAUDE.md" + new string(' ', 80) + "↓");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Trim ~/repo/CLAUDE.md");
    }

    [Test]
    [Arguments("   Build finished ↓")]
    [Arguments("   ↓ Build details")]
    [Arguments("   ↓")]
    public async Task A_retained_panel_edge_does_not_make_ordinary_arrow_output_a_dialog(string output) {
        var screen = Panel("   Build output", output);

        await Assert.That(ClaudeTerminalDialogDetector.Parse(screen, 120)).IsNull();
    }

    [Test]
    [Arguments(40)]
    [Arguments(120)]
    public async Task A_padded_scroll_marker_can_arrive_after_the_panel_content(int cols) {
        var watcher = new ClaudeScreenWatcher(cols, 40);
        var screen = string.Join('\n', new string('▔', cols), "   Usage", "", "   More details          ↓");
        var arrow = screen.IndexOf('↓');

        await Assert.That(watcher.Observe(Utf8(screen[..arrow])).Dialog).IsNull();
        var dialog = watcher.Observe(Utf8(screen[arrow..])).Dialog;

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Screen).Contains("More details");
        await Assert.That(dialog.Options).IsEmpty();
    }

    [Test]
    [Arguments(120, 40)]
    [Arguments(40, 12)]
    public async Task A_short_external_rule_separates_a_select_dialog_from_a_retained_panel(int cols, int ruleLength) {
        var screen = string.Join('\n',
            new string('▔', cols),
            "   Settings  Status",
            new string('─', ruleLength),
            "  Pick a server",
            "",
            "    Use this one",
            "  ❯ Use the other",
            "",
            "  Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, cols);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Heading).IsEqualTo("Pick a server");
        await Assert.That(dialog.Options.ToArray()).IsEquivalentTo(new[] { "Use this one", "Use the other" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task An_indented_separator_keeps_a_panel_list_screen_only() {
        var screen = Panel(
            "   Release notes",
            "   " + new string('─', 40),
            "   ❯ Version 2.1.310",
            "     Version 2.1.309",
            "",
            "   Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog).IsNotNull();
        await Assert.That(dialog!.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Version 2.1.309");
    }

    /// The rows on screen are a window onto a list that scrolls, so they are not offered as the
    /// panel's whole set of answers even under a select footer.
    [Test]
    public async Task A_list_in_a_panel_comes_back_as_its_screen() {
        var screen = string.Join('\n',
            new string('▔', 120),
            "   Release notes",
            "",
            "   ❯ Version 2.1.310",
            "     Version 2.1.309",
            "     ↓ 402 more",
            "",
            "   Enter to confirm · Esc to cancel");

        var dialog = ClaudeTerminalDialogDetector.Parse(screen, 120);

        await Assert.That(dialog!.Heading).IsEqualTo("Release notes");
        await Assert.That(dialog.Options).IsEmpty();
        await Assert.That(dialog.Screen).Contains("Version 2.1.309");
    }

    /// A panel edge left in the scrollback is not the dialog: the prompt box below it, or a ruled
    /// dialog drawn after it, is what the screen shows now.
    [Test]
    public async Task A_panel_edge_above_the_prompt_box_is_not_a_dialog() {
        var idle = string.Join('\n',
            new string('▔', 120),
            "   Esc to cancel",
            new string('─', 120),
            "❯ ",
            new string('─', 120),
            "  Esc to cancel");
        var ruled = string.Join('\n',
            new string('▔', 120),
            "   Settings  Status",
            new string('─', 120),
            "  Pick a server",
            "",
            "    Use this one",
            "  ❯ Use the other",
            "",
            "  Enter to confirm · Esc to cancel");

        await Assert.That(ClaudeTerminalDialogDetector.Parse(idle, 120)).IsNull();
        var dialog = ClaudeTerminalDialogDetector.Parse(ruled, 120);
        await Assert.That(dialog!.Heading).IsEqualTo("Pick a server");
        await Assert.That(dialog.Options).Count().IsEqualTo(2);
    }
}
