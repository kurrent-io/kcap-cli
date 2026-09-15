using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

public static class PullRequestTones {
    /// A closed or merged PR is past its checks; among live ones a failure outranks a conflict,
    /// which outranks a run in progress, which outranks the draft marker.
    public static PullRequestTone From(PullRequestOverviewDto overview) {
        switch (overview.Lifecycle) {
            case "merged": return PullRequestTone.Merged;
            case "closed": return PullRequestTone.Closed;
            case "open" or "draft": break;
            default: return PullRequestTone.None;
        }
        var rollup = overview.Checks?.Availability.Status == "ready" ? overview.Checks.Rollup : null;
        if (rollup == "failure") return PullRequestTone.ChecksFailed;
        if (overview.Mergeable == false) return PullRequestTone.Conflict;
        if (rollup == "pending") return PullRequestTone.ChecksRunning;
        if (overview.Lifecycle == "draft" || overview.IsDraft == true) return PullRequestTone.Draft;
        return PullRequestTone.Ready;
    }

    /// The tone of a set of PRs is the one most in need of attention, which the enum's order encodes.
    public static PullRequestTone Strongest(IEnumerable<PullRequestTone> tones) {
        var strongest = PullRequestTone.None;
        foreach (var tone in tones) if (tone > strongest) strongest = tone;
        return strongest;
    }

    public static string Label(PullRequestTone tone) => tone switch {
        PullRequestTone.Ready => "Ready to merge",
        PullRequestTone.Draft => "Draft",
        PullRequestTone.ChecksRunning => "Checks running",
        PullRequestTone.ChecksFailed => "Checks failed",
        PullRequestTone.Conflict => "Merge conflicts",
        PullRequestTone.Merged => "Merged",
        PullRequestTone.Closed => "Closed",
        _ => "",
    };

    /// The card's lifecycle label: the lifecycle word, except that an open PR with conflicts says so.
    public static PullRequestStatus LifecycleStatus(PullRequestOverviewDto? overview) => overview is null ? new("Unknown") : overview.Lifecycle switch {
        "open" when overview.Mergeable == false => new("Merge conflicts", "conflict"),
        "draft" => new("Draft", "draft"),
        "open" when overview.IsDraft == true => new("Draft", "draft"),
        "open" => new("Open", "open"),
        "merged" => new("Merged", "merged"),
        "closed" => new("Closed", "closed"),
        _ => new("Unknown"),
    };
}
