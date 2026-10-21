using System.Text.RegularExpressions;

namespace Capacitor.App.Services.LocalSetup;

/// The installer's messages are written for a terminal; the app shows their causes, not their commands.
public static partial class TerminalAdvice {
    /// Drops every sentence or <c>;</c>-clause that names a kcap command, opens with "Run", or sets an
    /// environment variable, keeping the rest verbatim. Null when nothing but advice was left.
    public static string? Strip(string? message) {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var sentences = new List<string>();
        foreach (var sentence in SentenceBreak().Split(message.Trim())) {
            var clauses = sentence.TrimEnd('.').Split("; ");
            var kept    = clauses.Where(c => !Advice().IsMatch(c)).ToList();
            if (kept.Count == clauses.Length) {
                sentences.Add(sentence);
                continue;
            }
            if (kept.Count == 0) continue;

            var text = string.Join("; ", kept);
            if (!ReferenceEquals(kept[0], clauses[0])) text = char.ToUpperInvariant(text[0]) + text[1..];
            sentences.Add(text + (text[^1] is '!' or '?' ? "" : "."));
        }

        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(
        @"\bkcap\s+(setup|upgrade|uninstall|login|logout|daemon|import|status|start|stop|agent|mcp|hook)\b"
      + @"|\bkcap\s+local(?:\s+(status|start|stop|restart|open|logs|upgrade|uninstall|help)\b|\s+--[\w-]+|(?=\s*([.!?]|$)))"
      + @"|^\s*run\s|\b(set|unset|export)\s+KCAP_[A-Z_]+|\brun setup again\b|\bin your terminal\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Advice();
}
