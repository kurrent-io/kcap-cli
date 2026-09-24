using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Services;

/// Reads Claude Code's usage-limit menu off the live PTY screen. The menu is terminal chrome:
/// the option set depends on the account, and the transcript never records it. A match is the
/// title plus at least two of the choices the CLI actually offers, so a sentence that merely
/// quotes one of them does not raise a question.
internal sealed class ClaudeUsageLimitDetector {
    public const string Prompt = "What do you want to do?";

    readonly AnsiScreen _screen = new(PtyDefaults.Cols, PtyDefaults.Rows);

    public UsageLimitNoticeDto? Observe(ReadOnlySpan<byte> chunk) {
        _screen.Write(chunk);
        return Parse(_screen.Text());
    }

    internal static UsageLimitNoticeDto? Parse(string screen) {
        var lines = screen.Split('\n');
        var title = -1;
        for (var i = 0; i < lines.Length; i++)
            if (Normalize(lines[i]) == Prompt) title = i;
        if (title < 0) return null;

        var options = new List<UsageLimitOptionDto>();
        var known = 0;
        for (var i = title + 1; i < lines.Length && options.Count < 9; i++) {
            var line = Normalize(lines[i]);
            if (line.Length == 0) continue;
            if (!TryOption(line, out var index, out var label)) {
                if (options.Count > 0) break;
                continue;
            }
            if (IsKnown(label)) known++;
            options.Add(new UsageLimitOptionDto(index, label));
        }
        if (known < 2) return null;

        string? summary = null;
        for (var i = title - 1; i >= 0 && title - i <= 8; i--) {
            var line = Normalize(lines[i]);
            if (line.Length == 0) continue;
            if (line.Contains("limit", StringComparison.OrdinalIgnoreCase)
                || line.Contains("usage", StringComparison.OrdinalIgnoreCase)
                || line.Contains("reset", StringComparison.OrdinalIgnoreCase)) {
                summary = line.Length <= 160 ? line : line[..160];
                break;
            }
        }

        return new UsageLimitNoticeDto(UsageLimitKinds.Blocked, summary ?? "Usage limit reached", Prompt, options);
    }

    static bool TryOption(string line, out int index, out string label) {
        index = 0;
        label = "";
        var i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        var start = i;
        while (i < line.Length && char.IsAsciiDigit(line[i])) i++;
        if (i == start || i >= line.Length || line[i] != '.') return false;
        if (!int.TryParse(line.AsSpan(start, i - start), out index) || index is < 1 or > 9) return false;
        label = line[(i + 1)..].Trim();
        return label.Length > 0;
    }

    static bool IsKnown(string label) =>
        label.Equals("Stop", StringComparison.Ordinal)
        || label.StartsWith("Stop and wait for limit to reset", StringComparison.Ordinal)
        || label.StartsWith("Wait here, then continue automatically", StringComparison.Ordinal)
        || label.StartsWith("Don't continue automatically", StringComparison.Ordinal)
        || label.StartsWith("Don\u2019t continue automatically", StringComparison.Ordinal)
        || label.StartsWith("Ask your admin for more usage", StringComparison.Ordinal)
        || label.StartsWith("Add funds to continue", StringComparison.Ordinal)
        || label.StartsWith("Switch to usage", StringComparison.Ordinal)
        || label.StartsWith("Upgrade your plan", StringComparison.Ordinal);

    static string Normalize(string line) {
        var builder = new System.Text.StringBuilder(line.Length);
        foreach (var c in line) {
            if (c is '│' or '┃' or '┌' or '┐' or '└' or '┘' or '─' or '━' or '├' or '┤'
                or '┬' or '┴' or '╭' or '╮' or '╰' or '╯' or '❯' or '›')
                builder.Append(' ');
            else if (c >= ' ')
                builder.Append(c);
        }
        return builder.ToString().Trim();
    }
}
