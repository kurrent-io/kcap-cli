using System.Collections.ObjectModel;
using System.Globalization;
using Capacitor.Cli.Core.PullRequests;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed partial class PullRequestContextViewModel {
    public string Title => CanDisplay && _overview?.Title is { } title ? title
        : _selected is { IsAvailable: true } choice ? choice.Link.Title ?? choice.Label : _selected?.Label ?? "";
    public string Branches => CanDisplay && (_overview?.HeadRef is not null || _overview?.BaseRef is not null)
        ? (_overview.HeadRef ?? "?") + " → " + (_overview.BaseRef ?? "?") : "";
    public bool HasBranches => Branches.Length > 0;
    public string FetchedLabel => CanDisplay && _overviewRead?.FetchedAt is { } at ? "Updated " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "";
    public string AccessLabel => CanDisplay ? (_grace ? "Access refresh paused" : "Access checked for " + (_overview?.AccessCheckedFor ?? "linked GitHub account")) : "";
    public string? Description => CanDisplayReader && _section == "overview" ? _overview?.Description : null;
    public bool DescriptionTruncated => CanDisplayReader && _overview?.DescriptionTruncated == true;
    /// The selected pull request has not arrived yet. Distinct from a section's own first load,
    /// which runs only once that overview can be shown.
    public bool IsSelectionLoading => IsReading && !CanDisplay && _selected is { IsAvailable: true };
    public bool ShowsLoading => IsSelectionLoading || IsSectionLoading;
    public string DescriptionNote => IsSelectionLoading ? "" : !CanDisplayReader ? "Refresh access to open PR content." : _overview?.Description is null ? "Description unavailable." : _overview.Description.Length == 0 ? "No description." : "";
    public bool IsOverview => _section == "overview";
    public bool IsThreads => _section == "threads";
    public bool IsThreadComments => _section == "thread_comments";
    public bool HasNotice => _notice.Length > 0;
    public bool ShowsSignIn => _notice.StartsWith("Sign in", StringComparison.Ordinal);
    public bool ShowsLinkGitHub => _notice.StartsWith("Link GitHub", StringComparison.Ordinal);
    public bool ShowReaderContent => CanDisplayReader;
    readonly ObservableCollection<PullRequestRow> _visibleRows = [];
    public IReadOnlyList<PullRequestRow> Rows => _visibleRows;
    public bool HasMore => CanReveal && CurrentSection is { Stopped: false, Next: not null };
    public bool CanReloadEarlier => CanReveal && CurrentSection is { Stopped: false, Evicted: not null };
    public string PageNote {
        get {
            if (_section == "overview") return "";
            if (!CanDisplayReader) return IsSelectionLoading ? "" : "Refresh access to open PR content.";
            if (CurrentSection is not { } state) return _pageRequests.Contains(SectionKey) ? "" : "This section has not loaded yet.";
            if (state.Error is { } error) return error;
            if (state.Coverage != "complete") return "Limited snapshot: an ordered subset. More may be available on GitHub.";
            return "";
        }
    }
    public string EmptyNote {
        get {
            if (_section == "overview" || !CanDisplayReader || CurrentSection is not { Error: null } state) return "";
            if (state.Pages.Count == 0 || state.Pages.Sum(page => page.Rows.Length) > 0) return "";
            return _section switch {
                "threads" when state.Excluded is { Kind: "exact", Value: > 0 } => $"No unresolved threads ({state.Excluded.Value} resolved).",
                "threads" => IncludeResolved ? "No threads." : "No unresolved threads.", "checks" => "No checks reported.", "reviewers" => "No reviewers yet.",
                "reviews" => "No submitted reviews.", _ => "No comments."
            };
        }
    }
    public bool HasEmptyNote => EmptyNote.Length > 0;
    /// A first load only: a reload keeps the rows it is replacing on screen.
    public bool IsSectionLoading => _section != "overview" && CanDisplayReader && CurrentSection is null && _pageRequests.Contains(SectionKey);
    public string LoadingNote => "Loading " + (IsSelectionLoading ? "pull request" : _section switch {
        "checks" => "checks", "reviewers" => "reviewers", "reviews" => "reviews", "threads" => "threads", "thread_comments" => "replies", _ => "comments"
    }) + "…";
    public string SnapshotLabel => CanDisplayReader && CurrentSection?.Completed is { } at
        ? "Updated " + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : "";
    /// The footer names one thing, the time; which commit the checks ran on rides its hover.
    public string FreshnessDetail => !IsOverview && CanDisplayReader && CurrentSection?.Head is { Length: >= 7 } head
        ? "Checks ran on commit " + head[..7] : "When GitHub last sent what this tab shows";

    static readonly string[] NotifiedProperties = [
        nameof(Notice), nameof(IsReading), nameof(HasChoice), nameof(HasPullRequest), nameof(HasListed), nameof(IsLegacy), nameof(CanOpenReader), nameof(Section), nameof(CanReveal), nameof(CanDisplay),
        nameof(Title), nameof(Branches), nameof(HasBranches), nameof(EmptyNote), nameof(HasEmptyNote), nameof(IsSectionLoading), nameof(LoadingNote), nameof(ShowsRefreshing), nameof(FetchedLabel), nameof(AccessLabel),
        nameof(Description), nameof(DescriptionTruncated), nameof(DescriptionNote), nameof(IsOverview), nameof(IsThreads), nameof(IsThreadComments), nameof(IncludeResolved), nameof(IsSelectionLoading), nameof(ShowsLoading),
        nameof(HasNotice), nameof(ShowsSignIn), nameof(ShowsLinkGitHub), nameof(ShowReaderContent), nameof(Rows), nameof(HasMore),
        nameof(CanReloadEarlier), nameof(PageNote), nameof(SnapshotLabel), nameof(FreshnessDetail),
        nameof(ReaderNote), nameof(HasReaderNote), nameof(ShowsInstallTool), nameof(InstallToolLabel),
    ];

    void Notify() {
        if (!IsReading) _userRefresh = false;
        _readerNote = _readers is null ? null
            : _selected?.Subject is { } subject ? _readers.NoteFor(subject.Provider, subject.Host)
            : _primaryRepo?.Invoke() is { } repository ? _readers.NoteFor(repository.Provider, repository.Host) : null;
        var rows = CanDisplayReader ? CurrentSection?.Pages.SelectMany(page => page.Rows).ToArray() ?? [] : [];
        ReconcileRows(rows);
        if (!_disposed && _hasPullRequest.Value != HasPullRequest) _hasPullRequest.OnNext(HasPullRequest);
        foreach (var property in NotifiedProperties) this.RaisePropertyChanged(property);
        NotifyPresentation();
    }

    /// Edits the visible list in place rather than replacing it: a row's control holds state its
    /// data does not — which sections a reader opened, a loaded image, the scroll extent it
    /// contributes — and replacing the list would discard that state on every page load or refresh.
    void ReconcileRows(PullRequestRow[] rows) {
        for (var i = 0; i < rows.Length && i < _visibleRows.Count; i++) if (_visibleRows[i] != rows[i]) _visibleRows[i] = rows[i];
        while (_visibleRows.Count > rows.Length) _visibleRows.RemoveAt(_visibleRows.Count - 1);
        for (var i = _visibleRows.Count; i < rows.Length; i++) _visibleRows.Add(rows[i]);
    }
    static string Reason<T>(PullRequestRead<T> read) where T : class => read.Kind switch {
        PullRequestReadKind.SignedOut => "Sign in to see pull requests.",
        PullRequestReadKind.SubjectUnavailable => "This pull request is no longer linked or visible.",
        PullRequestReadKind.InvalidProtocol => "The server returned an invalid PR response. Retry after updating the server and app.",
        PullRequestReadKind.Ready or PullRequestReadKind.Stale => "Refreshing access before opening new content…",
        _ => read.Reason switch {
            "github_not_linked" => "Link GitHub in your account settings to read this pull request.",
            "github_access_denied" => "Your linked GitHub account cannot read this repository.",
            "disabled" => "PR reading is disabled for this workspace.",
            "not_configured" => "PR reading is not configured for this workspace.",
            "integration_capability_unavailable" => "The GitHub integration cannot read this PR. An operator can check its permissions.",
            "rate_limited" or "budget_exhausted" => "GitHub reads are paused temporarily. Retry after the cooldown.",
            "identity_changed" or "integration_changed" => "Access changed. Refresh to reload this pull request.",
            "no_reader" => "No reader is available for this pull request's host.",
            "not_found" => "This pull request could not be found.",
            "tool_signed_out" => "The local CLI is not signed in for this host. Sign in and refresh.",
            "tool_denied" => "Your account cannot read this pull request.",
            "tool_failed" => "The local CLI could not read this pull request. Refresh to try again.",
            _ => "Couldn't load pull request context. Retry when the server is reachable."
        }
    };
    static string Author(PullRequestActorDto? actor) => actor is null ? "Unknown author" : actor.Kind == "team"
        ? actor.Name ?? actor.Login ?? "Team" : actor.Login ?? actor.Name ?? "Unknown author";
    static string Joined(params string?[] parts) => string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part)));
    /// GitHub serves a user's avatar at /{login}.png on github.com and Enterprise hosts alike; teams and bots keep their glyph.
    static string? AvatarUrl(PullRequestActorDto? actor, PullRequestSubjectDto subject) =>
        subject.Provider == "github" && actor is { Kind: "user", Login: { Length: > 0 } login } && Uri.CheckHostName(subject.Host) == UriHostNameType.Dns
            ? $"https://{subject.Host}/{Uri.EscapeDataString(login)}.png?size=64" : null;
    static string Dated(DateTime? at) => at?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "";
    static string Outcome(string? value) => value switch {
        "success" => "Passed", "failure" => "Failed", "pending" => "Pending", "neutral" => "Neutral", "skipped" => "Skipped",
        "cancelled" => "Cancelled", "timed_out" => "Timed out", "action_required" => "Action required", "stale" => "Stale", _ => "Unknown"
    };
    static string ReviewState(string? value) => value switch { "approved" => "Approved", "changes_requested" => "Changes requested",
        "commented" => "Commented", "dismissed" => "Dismissed", _ => "Unknown review state" };
    static PullRequestRow Unavailable(string id, string title, string? url, string? availability) => new(id, title, "", null, null, url,
        availability == "redacted" ? "redacted" : "unavailable");
    PullRequestRow ToRow(PullRequestCommentDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, Author(item.Author), Dated(item.CreatedAt), item.Body, null, PrLink(item.Url, subject), "available", item.BodyTruncated, ActorKind: item.Author?.Kind)
        : Unavailable(item.Id, "Comment", PrLink(item.Url, subject), item.Availability);
    PullRequestRow ToRow(PullRequestReviewDto item, PullRequestSubjectDto subject) => item.Availability == "available" && item.State is not ("pending" or "PENDING")
        ? new(item.Id, Author(item.Author), Joined(ReviewState(item.State), Dated(item.SubmittedAt)), item.Body, null, PrLink(item.Url, subject), "available", item.BodyTruncated)
        : Unavailable(item.Id, "Review", PrLink(item.Url, subject), item.Availability);
    PullRequestRow ToRow(PullRequestReviewerDto item, PullRequestSubjectDto subject) {
        if (item.Availability != "available") return Unavailable(item.Id, "Reviewer", null, item.Availability) with { IsReviewer = true };
        var status = ReviewRowStatus(item.ReviewState, item.Requested == true);
        return new(item.Id, Author(item.Actor), (item.Requested == true ? "Review requested · " : "") + status.Text,
            null, null, PrLink(item.Url, subject), "available", IsReviewer: true, ActorKind: item.Actor?.Kind, AvatarUrl: AvatarUrl(item.Actor, subject),
            Status: status, ReRequested: item.Requested == true && item.ReviewState is not null);
    }
    PullRequestRow ToRow(PullRequestCheckDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, item.Name ?? "Unnamed check",
            Joined(item.AppName ?? item.Source, PullRequestWire.CheckLink(item.Url) is { } url ? new Uri(url).Host : null),
            null, null, PullRequestWire.CheckLink(item.Url), "available", IsCheck: true, Outcome: item.Outcome ?? "unknown",
            Status: new(Outcome(item.Outcome), item.Outcome switch {
                "success" => "success", "failure" or "timed_out" or "action_required" => "failure", "pending" => "pending", _ => "neutral"
            }))
        : Unavailable(item.Id, "Check", PullRequestWire.CheckLink(item.Url), item.Availability) with { IsCheck = true, Outcome = "unknown" };
    static PullRequestStatus ReviewRowStatus(string? state, bool requested) => state switch {
        "approved" => new("Approved", "success"), "changes_requested" => new("Changes requested", "failure"),
        "commented" => new("Commented", "commented"), "dismissed" => new("Dismissed"),
        null when requested => new("Awaiting review", Detail: "Review requested; this reviewer has not submitted one yet."),
        _ => new("Unknown", Detail: "GitHub did not report this reviewer's state."),
    };
    PullRequestRow ToRow(PullRequestThreadDto item, PullRequestSubjectDto subject) => item.Availability == "available"
        ? new(item.Id, (item.Path ?? "Unknown file") + (item.Line is { } line ? ":" + line.ToString(CultureInfo.InvariantCulture) : ""),
            Joined(item.IsResolved switch { true => "Resolved", false => "Unresolved", _ => null }, item.IsOutdated == true ? "Outdated" : null,
                item.RootComment is { Availability: "available", Author: { } author } ? Author(author) : null),
            item.RootComment?.Availability == "available" ? item.RootComment.Body : null, item.DiffHunk, PrLink(item.Url, subject),
            "available", item.HunkTruncated || item.RootComment?.BodyTruncated == true, IsThread: true)
        : Unavailable(item.Id, "Thread", PrLink(item.Url, subject), item.Availability);
}
