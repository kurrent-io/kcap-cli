using System.Text.RegularExpressions;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.App.ViewModels;

/// One carried-over item. Carried reads as success; refused, failed and unknown are the ones that
/// need a look; skipped is neutral.
public sealed partial record HandoffOutcomeRow(string Text, bool IsCarried, bool NeedsAttention) {
    public bool IsNeutral => !IsCarried && !NeedsAttention;

    public static HandoffOutcomeRow From(HandoffOutcomeItemDto item) {
        var result = item.Result switch {
            "carried" => "carried over",
            "refused" => "refused",
            "skipped" => "skipped",
            "failed"  => "failed",
            _         => "couldn't be checked",
        };

        var label = string.IsNullOrWhiteSpace(item.Label) ? (item.Kind == "work_item" ? "Untitled work item" : "Untitled plan") : Fold(item.Label);

        return new($"{label} — {result}", item.Result == "carried", item.Result is not ("carried" or "skipped"));
    }

    const int MaxLabel = 120;

    /// A server-supplied label on one line, capped so one long title cannot take over the card.
    internal static string Fold(string text) {
        var line = Whitespace().Replace(text, " ").Trim();
        return line.Length <= MaxLabel ? line : $"{line[..(MaxLabel - 1)].TrimEnd()}…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
