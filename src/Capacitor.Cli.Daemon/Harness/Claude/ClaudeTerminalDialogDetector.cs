using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// Reads Claude Code's select dialogs off the live screen by their layout, not their wording, so a
/// dialog no hook reports (workspace trust, a new project MCP server) still reaches Chat. A dialog
/// is a rule, then its text, then the footer as the last line on screen: the prompt box and status
/// line always sit below ordinary output, so a transcript that merely quotes the footer never
/// ends the screen. The options are the rows around the one row carrying the cursor. When the rows
/// cannot be read as a single-choice list the dialog still comes back, with no options, so a
/// surface can point at the terminal instead of guessing keys.
internal static class ClaudeTerminalDialogDetector {
    const string Footer = "Enter to confirm · Esc to cancel";
    const char   Cursor = '❯';

    /// <param name="cols">The width the screen was drawn at, which decides where a label wrapped.</param>
    internal static TerminalDialogDto? Parse(string screen, int cols) {
        var lines  = screen.Split('\n');
        var footer = Array.FindLastIndex(lines, l => l.Trim().Length > 0);
        if (footer < 0 || !lines[footer].Trim().StartsWith(Footer, StringComparison.Ordinal)) return null;

        var border = Array.FindLastIndex(lines, footer, IsRule);
        if (border < 0) return null;
        var region = lines[(border + 1)..footer];

        var (options, selected, firstRow) = ReadOptions(region, cols);
        var paragraphs = Paragraphs(region[..firstRow].Select(l => l.Trim()));
        var heading = paragraphs.FirstOrDefault() ?? "";
        var body = string.Join('\n', paragraphs.Skip(1));
        var shown = string.Join('\n', region.Select(l => l.TrimEnd())).Trim('\n');

        return new TerminalDialogDto(heading, body, options, selected, shown);
    }

    /// Options share the text column of the cursor row. A row continues the option above it when it
    /// is indented deeper, or when its first word could not have fit on that option's row: Claude
    /// wraps a long label at the label's own column. A checkbox list is multi-select, which arrows
    /// and Enter cannot answer.
    static (List<string> Options, int Selected, int FirstRow) ReadOptions(string[] region, int cols) {
        var cursorRows = Enumerable.Range(0, region.Length).Where(i => region[i].TrimStart().StartsWith(Cursor)).ToList();
        if (cursorRows.Count != 1) return ([], -1, region.Length);

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
            if (i != cursor && options.Count > 0 && (Indent(line) > column || Wrapped(region[i - 1], line, cols))) {
                options[^1] += " " + line.Trim();
                continue;
            }
            if (i == cursor) selected = options.Count;
            options.Add(StripNumber(line.Trim()));
        }

        if (options.Count < 2 || options.Any(IsCheckbox)) return ([], -1, first);
        return (options, selected, first);
    }

    static bool Wrapped(string above, string line, int cols) {
        var word = line.Trim().Split(' ')[0];
        return above.TrimEnd().Length + 1 + word.Length > cols;
    }

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
