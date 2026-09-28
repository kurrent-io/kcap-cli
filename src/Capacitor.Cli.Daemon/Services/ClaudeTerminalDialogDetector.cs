using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

/// Reads Claude Code's select dialogs off the live screen by their layout, not their wording, so a
/// dialog no hook reports (workspace trust, a new project MCP server) still reaches Chat. The anchor
/// is the dialog footer; the options are the rows around the one row carrying the cursor, and the
/// heading is the first line under the rule that opens the dialog. When the rows cannot be read as
/// a single-choice list the dialog still comes back, with no options, so a surface can point at
/// the terminal instead of guessing keys.
internal static class ClaudeTerminalDialogDetector {
    const string Footer          = "Enter to confirm · Esc to cancel";
    const char   Cursor          = '❯';
    const int    MaxDialogLines  = 30;

    internal static TerminalDialogDto? Parse(string screen) {
        var lines  = screen.Split('\n');
        var footer = Array.FindLastIndex(lines, l => l.Trim().StartsWith(Footer, StringComparison.Ordinal));
        if (footer < 0) return null;

        var top    = Math.Max(0, footer - MaxDialogLines);
        var border = Array.FindLastIndex(lines, footer, footer - top + 1, IsRule);
        var region = lines[(border >= 0 ? border + 1 : top)..footer];

        var (options, selected, firstRow) = ReadOptions(region);
        var text = region[..firstRow].Select(l => l.Trim()).ToList();
        var heading = text.FirstOrDefault(l => l.Length > 0) ?? "";
        var body = Paragraphs(text.SkipWhile(l => l.Length == 0).Skip(1));
        var shown = string.Join('\n', region.Select(l => l.TrimEnd())).Trim('\n');

        return new TerminalDialogDto(heading, body, options, selected, shown);
    }

    /// Options share the text column of the cursor row; a deeper row continues the option above it.
    /// A checkbox list is multi-select, which arrows and Enter cannot answer.
    static (List<string> Options, int Selected, int FirstRow) ReadOptions(string[] region) {
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
            if (i != cursor && Indent(line) > column && options.Count > 0) {
                options[^1] += " " + line.Trim();
                continue;
            }
            if (i == cursor) selected = options.Count;
            options.Add(StripNumber(line.Trim()));
        }

        if (options.Count < 2 || options.Any(IsCheckbox)) return ([], -1, first);
        return (options, selected, first);
    }

    static string Paragraphs(IEnumerable<string> lines) {
        var paragraphs = new List<string>();
        var current    = new List<string>();
        foreach (var line in lines) {
            if (line.Length > 0) { current.Add(line); continue; }
            if (current.Count > 0) paragraphs.Add(string.Join(' ', current));
            current.Clear();
        }
        if (current.Count > 0) paragraphs.Add(string.Join(' ', current));
        return string.Join('\n', paragraphs);
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
