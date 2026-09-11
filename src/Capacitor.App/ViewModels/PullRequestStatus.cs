namespace Capacitor.App.ViewModels;

public sealed record PullRequestStatus(string Text, string Kind = "neutral", string? Detail = null) {
    public bool IsSuccess => Kind is "success" or "open";
    public bool IsWarning => Kind == "pending";
    public bool IsDanger => Kind is "failure" or "closed";
    public bool IsPurple => Kind == "merged";
    public string IconData => Kind switch {
        "open" => "M4,5 A2,2 0 1 0 4,1 A2,2 0 1 0 4,5 M4,5 V13 M12,11 A2,2 0 1 0 12,15 A2,2 0 1 0 12,11 M12,11 V6 Q12,3 8,3 M10,1 L8,3 L10,5",
        "merged" => "M4,5 A2,2 0 1 0 4,1 A2,2 0 1 0 4,5 M4,5 V11 M4,11 A2,2 0 1 0 4,15 A2,2 0 1 0 4,11 M4,6 Q4,10 11,10 M11,10 A2,2 0 1 0 15,10 A2,2 0 1 0 11,10",
        "commented" => "M3,2 H13 Q14,2 14,3 V10 Q14,11 13,11 H6 L2,14 V3 Q2,2 3,2 Z",
        "success" => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M4.5,8 L7,10.5 L11.5,5.5",
        "failure" or "closed" => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M5.5,5.5 L10.5,10.5 M10.5,5.5 L5.5,10.5",
        "pending" => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M8,4 V8 H11",
        _ => "M8,1 A7,7 0 1 0 8,15 A7,7 0 1 0 8,1 M5,8 H11"
    };
}
