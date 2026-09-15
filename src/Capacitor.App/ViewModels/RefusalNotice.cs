namespace Capacitor.App.ViewModels;

/// The one line an intake's refusals render to, shared by every composer that shows it.
public static class RefusalNotice {
    /// The per-prompt cap is stated once with the names it dropped, and a failure that names no file
    /// (the clipboard, a source that never opened) drops the name rather than backticking it.
    public static string? Render(IReadOnlyList<IntakeRefusal> refused) {
        if (refused.Count == 0) return null;
        var parts = new List<string>();
        var overCap = new List<string>();
        foreach (var refusal in refused) {
            if (refusal.Reason == AttachmentTray.CapReason) overCap.Add($"`{refusal.Name}`");
            else parts.Add(refusal.Reason.StartsWith("the ", StringComparison.Ordinal) ? refusal.Reason : $"`{refusal.Name}` {refusal.Reason}");
        }
        if (overCap.Count > 0) parts.Add($"{AttachmentTray.CapReason} — {string.Join(", ", overCap)} not added");
        return string.Join("; ", parts);
    }
}
