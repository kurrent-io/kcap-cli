using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// Reads Claude Code's dialogs from the live screen. The footer must end the screen: ordinary
/// output has the prompt box below it. Forms and unreadable lists return no choices so clients
/// show the screen and direct the user to Terminal.
internal static class ClaudeTerminalDialogDetector {
    const string SelectFooter = "Enter to confirm · Esc to cancel";
    const string FormFooter = "←/→ to change · Enter to continue · Esc to cancel";
    const char   Cursor = '❯';
    // The fullscreen TUI tops a panel (settings, status, usage, pickers) with this edge in place of
    // the prompt box. Every such panel closes with Esc, whatever else its footer offers, and its
    // lists scroll, so the rows on screen are not all the choices there are.
    const string PanelEdge = "▔▔▔▔▔▔▔▔";
    // The viewport marker is padded apart from content; arrows inside text are not controls.
    const string ScrollMarker = "  ↓";

    /// <param name="cols">The width the screen was drawn at, which decides where a label wrapped.</param>
    internal static TerminalDialogDto? Parse(string screen, int cols) => Read(screen, cols).Dialog;

    /// IncompleteRedraw is set when the cursor is not on exactly one row: the chunk ended between
    /// drawing the new cursor and erasing the old one.
    internal static (TerminalDialogDto? Dialog, bool IncompleteRedraw) Read(string screen, int cols) {
        var lines  = screen.Split('\n');
        var footer = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
        if (footer < 0) return (null, false);
        var footerText = lines[footer].Trim();
        var select = footerText.StartsWith(SelectFooter, StringComparison.Ordinal);
        string[] region;
        if (PanelTop(lines, footer) is { } top && (ClosesWithEsc(footerText) || ScrolledPastFooter(footerText))) {
            // A panel scrolled past its footer still holds the keyboard; its last row is content.
            region = ClosesWithEsc(footerText) ? lines[(top + 1)..footer] : lines[(top + 1)..(footer + 1)];
            select = false;
        } else {
            if (!select && !footerText.StartsWith(FormFooter, StringComparison.Ordinal)) return (null, false);
            var border = Array.FindLastIndex(lines, footer, IsRule);
            if (border < 0) return (null, false);
            region = lines[(border + 1)..footer];
        }

        // Forms edit individual settings; moving the cursor and pressing Enter is not one answer.
        (List<string> options, int selected, int firstRow, bool incomplete) = select
            ? ReadOptions(region, cols)
            : ([], -1, region.Length, false);
        var paragraphs = Paragraphs(region[..firstRow].Select(l => l.Trim()));
        var heading = paragraphs.FirstOrDefault() ?? "";
        var body = string.Join('\n', paragraphs.Skip(1));
        var shown = string.Join('\n', region.Select(l => l.TrimEnd())).Trim('\n');

        return (new TerminalDialogDto(heading, body, options, selected, shown), incomplete);
    }

    /// Same-column rows that could wrap are also possible choices. Guessing that boundary can
    /// merge a decline choice into an approval, so ambiguous lists use the screen-only fallback.
    static (List<string> Options, int Selected, int FirstRow, bool Incomplete) ReadOptions(string[] region, int cols) {
        var cursorRows = Enumerable.Range(0, region.Length).Where(i => region[i].TrimStart().StartsWith(Cursor)).ToList();
        if (cursorRows.Count != 1) return ([], -1, region.Length, true);

        var cursor = cursorRows[0];
        var mark   = region[cursor].IndexOf(Cursor);
        var column = mark + 1 + Indent(region[cursor][(mark + 1)..]);

        var first = cursor;
        while (first > 0 && region[first - 1].Trim().Length > 0 && Indent(region[first - 1]) >= column) first--;
        var last = cursor;
        while (last + 1 < region.Length && region[last + 1].Trim().Length > 0 && Indent(region[last + 1]) >= column) last++;

        var options  = new List<string>();
        var selected = -1;
        for (var i = first; i <= last; i++) {
            var line = i == cursor ? region[i][(mark + 1)..] : region[i];
            if (i != cursor && options.Count > 0 && Indent(line) > column) {
                options[^1] += " " + line.Trim();
                continue;
            }
            if (i != cursor && options.Count > 0 && CouldWrap(region[i - 1], line, cols))
                return ([], -1, first, false);
            if (i == cursor) selected = options.Count;
            options.Add(StripNumber(line.Trim()));
        }

        if (options.Count < 2 || options.Any(IsCheckbox)) return ([], -1, first, false);
        return (options, selected, first, false);
    }

    static bool CouldWrap(string above, string line, int cols) {
        var word = line.Trim().Split(' ')[0];
        return above.TrimEnd().Length + 1 + word.Length > cols;
    }

    /// The panel edge nearest the footer, unless a prompt box sits between them.
    static int? PanelTop(string[] lines, int footer) {
        for (var i = footer - 1; i >= 0; i--) {
            if (IsPromptRule(lines[i])) return null;
            if (lines[i].TrimStart().StartsWith(PanelEdge, StringComparison.Ordinal)) return i;
        }
        return null;
    }

    static bool ClosesWithEsc(string footer) =>
        footer.Split(" · ").Any(hint => hint is "Esc to cancel" or "Esc to close");

    static bool ScrolledPastFooter(string footer) =>
        footer.EndsWith(ScrollMarker, StringComparison.Ordinal)
        && !footer.AsSpan(0, footer.Length - ScrollMarker.Length).Trim().IsEmpty;

    static bool IsPromptRule(string line) => line.Length > 0 && !char.IsWhiteSpace(line[0]) && IsRule(line);

    static List<string> Paragraphs(IEnumerable<string> lines) {
        var paragraphs = new List<string>();
        var current    = new List<string>();
        foreach (var line in lines) {
            if (line.Length > 0) { current.Add(line); continue; }
            if (current.Count > 0) paragraphs.Add(string.Join(' ', current));
            current.Clear();
        }
        if (current.Count > 0) paragraphs.Add(string.Join(' ', current));
        return paragraphs;
    }

    static int Indent(string line) {
        var i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    static bool IsRule(string line) {
        var trimmed = line.Trim();
        return trimmed.Length >= 8 && trimmed.All(c => c is '─' or '━' or '╌' or '┄');
    }

    static string StripNumber(string label) {
        var i = 0;
        while (i < label.Length && char.IsAsciiDigit(label[i])) i++;
        return i > 0 && i + 1 < label.Length && label[i] == '.' && label[i + 1] == ' ' ? label[(i + 2)..] : label;
    }

    static bool IsCheckbox(string label) =>
        label.Length > 0 && label[0] is '[' or '◯' or '◉' or '○' or '●' or '☐' or '☑' or '☒';
}
