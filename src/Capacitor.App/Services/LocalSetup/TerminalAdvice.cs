using System.Text.RegularExpressions;

namespace Capacitor.App.Services.LocalSetup;

/// The installer's messages are written for a terminal; the app shows their causes, not their commands.
public static partial class TerminalAdvice {
    /// Drops every sentence or <c>;</c>-clause that names a kcap command or a terminal step, keeping
    /// the rest. Null when nothing but advice was left.
    public static string? Strip(string? message) {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var sentences = new List<string>();
        foreach (var sentence in SentenceBreak().Split(message.Trim())) {
            var clauses = sentence.TrimEnd('.').Split("; ").Where(c => !Advice().IsMatch(c)).ToList();
            if (clauses.Count == 0) continue;

            var kept = string.Join("; ", clauses);
            sentences.Add(char.ToUpperInvariant(kept[0]) + kept[1..] + (kept[^1] is '!' or '?' ? "" : "."));
        }

        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(
        @"\bkcap\s+(setup|local|upgrade|uninstall|login|logout|daemon|import|status|start|stop|agent|mcp|hook)\b|\brun setup again\b|\bin your terminal\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Advice();
}
