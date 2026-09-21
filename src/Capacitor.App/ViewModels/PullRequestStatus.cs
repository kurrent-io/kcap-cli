namespace Capacitor.App.ViewModels;

public sealed record PullRequestStatus(string Text, string Kind = "neutral", string? Detail = null) {
    public bool IsSuccess => Kind is "success" or "open";
    public bool IsWarning => Kind is "conflict" or "warning";
    public bool IsDanger => Kind is "failure" or "closed";
    /// A running check, a draft, and a merge share the muted colour; only the check pulses.
    public bool IsMuted => Kind is "pending" or "draft" or "merged";
    public bool IsPulsing => Kind == "pending";
    /// Git lifecycle marks stay stroke glyphs; checks/reviews use the pane's filled discs.
    public bool UsesGlyphIcon => Kind is "open" or "draft" or "merged" or "conflict" or "commented";
    public bool UsesDiscIcon => !UsesGlyphIcon;
    public bool IsNeutralDisc => UsesDiscIcon && !IsSuccess && !IsWarning && !IsDanger && !IsPulsing;
    public string IconData => Kind switch {
        "open" or "draft" => "M4,5 A2,2 0 1 0 4,1 A2,2 0 1 0 4,5 M4,5 V13 M12,11 A2,2 0 1 0 12,15 A2,2 0 1 0 12,11 M12,11 V6 Q12,3 8,3 M10,1 L8,3 L10,5",
        "conflict" => "M8,2 L14.5,13.5 H1.5 Z M8,6.5 V9.5 M8,11.3 V11.7",
        "merged" => "M4,5 A2,2 0 1 0 4,1 A2,2 0 1 0 4,5 M4,5 V11 M4,11 A2,2 0 1 0 4,15 A2,2 0 1 0 4,11 M4,6 Q4,10 11,10 M11,10 A2,2 0 1 0 15,10 A2,2 0 1 0 11,10",
        "commented" => "M3,2 H13 Q14,2 14,3 V10 Q14,11 13,11 H6 L2,14 V3 Q2,2 3,2 Z",
        _ => ""
    };
}
