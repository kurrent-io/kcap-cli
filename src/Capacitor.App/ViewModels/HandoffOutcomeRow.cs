using Capacitor.Cli.Core.Handoff;

namespace Capacitor.App.ViewModels;

/// One carried-over item. Carried reads as success; refused, failed and unknown are the ones that
/// need a look; skipped is neutral.
public sealed record HandoffOutcomeRow(string Text, bool IsCarried, bool NeedsAttention) {
    public bool IsNeutral => !IsCarried && !NeedsAttention;

    public static HandoffOutcomeRow From(HandoffOutcomeItemDto item) {
        var result = item.Result switch {
            "carried" => "carried over",
            "refused" => "refused",
            "skipped" => "skipped",
            "failed"  => "failed",
            _         => "couldn't be checked",
        };

        var label = string.IsNullOrWhiteSpace(item.Label) ? (item.Kind == "work_item" ? "Untitled work item" : "Untitled plan") : item.Label;

        return new($"{label} — {result}", item.Result == "carried", item.Result is not ("carried" or "skipped"));
    }
}
