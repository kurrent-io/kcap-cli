using System.Globalization;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using Capacitor.Cli.Core.PullRequests;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class PullRequestPresentationTests {
    [Test]
    public Task Sidebar_status_actions_open_the_matching_tabs_and_review_subsections_keep_native_content() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        var tabs = h.Reader.FindControl<TabStrip>("ReaderTabs")!;
        var reviews = h.Reader.FindControl<TabStrip>("ReviewTabs")!;
        var checks = h.Card.FindControl<Button>("SidebarChecksButton")!;
        checks.Command!.Execute(checks.CommandParameter);
        await h.SettleAsync();
        await Assert.That(tabs.SelectedIndex).IsEqualTo(1);
        await Assert.That(h.Model.Rows.Single().IsCheck).IsTrue();
        await Assert.That(h.Model.ChecksStatus.IsDanger).IsTrue();
        await Assert.That(h.Model.ChecksStatus.Detail).Contains("1 failed");
        await Assert.That(AutomationProperties.GetName(checks)).IsEqualTo("Open checks: 1 failed");
        var checksTab = h.Reader.FindControl<TabStripItem>("ChecksTab")!;
        await Assert.That(AutomationProperties.GetName(checksTab)).IsEqualTo("Checks: 1 failed");

        var review = h.Card.FindControl<Button>("SidebarReviewsButton")!;
        review.Command!.Execute(review.CommandParameter);
        await h.SettleAsync();
        await Assert.That(h.Opened).IsGreaterThanOrEqualTo(2);
        await Assert.That(tabs.SelectedIndex).IsEqualTo(2);
        await Assert.That(reviews.IsEffectivelyVisible).IsTrue();
        await Assert.That(h.Model.Rows.Single().IsReviewer).IsTrue();
        await Assert.That(h.Model.CheckRows).IsEmpty();
        await Assert.That(h.Model.DiscussionRows).IsEmpty();

        reviews.SelectedIndex = 1;
        await h.SettleAsync();
        await Assert.That(h.Model.Rows.Single().Body).IsEqualTo("Private review");
        reviews.SelectedIndex = 2;
        await h.SettleAsync();
        await Assert.That(h.Model.Rows.Single().Hunk).IsEqualTo("+private code");
        await h.Model.ExpandThreadCommand.Execute(h.Model.Rows.Single());
        await h.SettleAsync();
        await Assert.That(h.Model.Section).IsEqualTo("thread_comments");
        await Assert.That(tabs.SelectedIndex).IsEqualTo(2);
        await Assert.That(reviews.SelectedIndex).IsEqualTo(3);
        await Assert.That(h.Model.Rows.Single().Body).IsEqualTo("Private comment");
        var replies = reviews.GetVisualDescendants().OfType<TabStripItem>().Single(tab => Equals(tab.Content, "Replies"));
        replies.Focus();
        h.Window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.None);
        await h.SettleAsync();
        await Assert.That(h.Model.Section).IsEqualTo("threads");
        await Assert.That(reviews.SelectedIndex).IsEqualTo(2);
        await Assert.That(replies.IsEffectivelyVisible).IsFalse();

        tabs.SelectedIndex = 3;
        await h.SettleAsync();
        await Assert.That(h.Model.Section).IsEqualTo("conversation");
        await Assert.That(h.Model.Rows.Single().Body).IsEqualTo("Private comment");
        tabs.SelectedIndex = 0;
        await h.SettleAsync();
        await Assert.That(h.Model.Description).IsEqualTo("Private description");
    });

    [Test]
    [Arguments(1, false)]
    [Arguments(2, true)]
    public Task The_picker_is_visible_only_when_multiple_PRs_can_be_selected(int count, bool visible) => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.Links = h.Source.Links.Take(count).ToArray();
        await h.ShowAsync();
        var selector = h.Card.FindControl<ComboBox>("PullRequestSelector")!;
        await Assert.That(selector.IsEffectivelyVisible).IsEqualTo(visible);
        await Assert.That(h.Model.SectionEyebrow).IsEqualTo("PULL REQUESTS");
        await Assert.That(h.Model.SectionMeta).IsEqualTo(count.ToString(CultureInfo.InvariantCulture));
        await Assert.That(h.Model.RepositoryLabel).IsEqualTo("example/repo");
        await Assert.That(h.Model.NumberLabel).IsEqualTo("#1");
        if (visible) {
            selector.SelectedIndex = 1;
            await h.SettleAsync();
            await Assert.That(h.Model.Selected!.Subject.Number).IsEqualTo(2);
            await Assert.That(h.Model.NumberLabel).IsEqualTo("#2");
        }
    });

    /// A requested reviewer who has not reviewed carries no state; that is a wait, not an unknown.
    [Test]
    public Task A_requested_reviewer_without_a_review_reads_as_awaiting() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.PageItem = section => section == "reviewers" ? new PullRequestReviewerDto {
            Id = "reviewer", Availability = "available", Requested = true, Actor = new() { Id = "octocat", Kind = "user", Login = "octocat" }
        } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("reviewers");
        await h.SettleAsync();
        var row = h.Reader.FindControl<ItemsControl>("ReviewerRows")!.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("prRow"));
        var text = row.GetVisualDescendants().OfType<PullRequestStatusLabel>().Single().FindControl<TextBlock>("StatusText")!;
        await Assert.That(text.Text).IsEqualTo("Awaiting review");
        await Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "Review re-requested").IsEffectivelyVisible).IsFalse();
        await Assert.That(h.Model.Rows.Single().AvatarUrl).IsEqualTo("https://github.com/octocat.png?size=64");
    });

    /// A re-request after a review is the only case the subline adds anything the status does not say.
    [Test]
    public Task A_reviewer_asked_again_after_reviewing_shows_the_re_request_and_bots_get_no_avatar() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.PageItem = section => section == "reviewers" ? new PullRequestReviewerDto {
            Id = "reviewer", Availability = "available", Requested = true, ReviewState = "changes_requested",
            Actor = new() { Id = "bot", Kind = "bot", Login = "review-bot" }
        } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("reviewers");
        await h.SettleAsync();
        var row = h.Reader.FindControl<ItemsControl>("ReviewerRows")!.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("prRow"));
        await Assert.That(row.GetVisualDescendants().OfType<TextBlock>().Single(block => block.Text == "Review re-requested").IsEffectivelyVisible).IsTrue();
        await Assert.That(h.Model.Rows.Single().AvatarUrl).IsNull();
    });

    /// Every pending marker reads one clock, so the sidebar and reader never fade out of step, and
    /// only the marker fades — the status text beside it stays readable.
    [Test]
    public Task Pending_markers_share_one_phase_and_leave_the_text_steady() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.PageItem = section => section == "checks"
            ? new PullRequestCheckDto { Id = "check", Availability = "available", Name = "build", Outcome = "pending", HeadSha = new string('a', 40) } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("checks");
        await h.SettleAsync();
        await Assert.That(h.Model.ChecksStatus.IsPulsing).IsTrue();
        var markers = new Control[] { h.Card, h.Reader }.SelectMany(root => root.GetVisualDescendants().OfType<Visual>())
            .Where(visual => PulseClock.GetIsActive(visual) && visual.IsEffectivelyVisible).ToArray();
        await Assert.That(markers.Length).IsGreaterThanOrEqualTo(3);
        await WorkspaceFixtures.WaitUntilAsync(() => markers.Select(marker => marker.Opacity).Distinct().Count() == 1, what: "one shared pulse phase");
        var texts = h.Card.GetVisualDescendants().OfType<PullRequestStatusLabel>()
            .Select(label => label.FindControl<TextBlock>("StatusText")!).Where(text => text.Text == h.Model.ChecksStatus.Text);
        foreach (var text in texts) await Assert.That(text.GetSelfAndVisualAncestors().OfType<Visual>().TakeWhile(v => v != h.Card).All(v => v.Opacity == 1)).IsTrue();
    });

    /// Check rows are a snapshot; the summary follows the overview. While a check runs the next
    /// poll must reload the rows, or the summary turns green beside rows still saying pending.
    [Test]
    public Task Pending_check_rows_reload_on_the_next_poll() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        var outcome = "pending";
        h.Source.PageItem = section => section == "checks"
            ? new PullRequestCheckDto { Id = "check", Availability = "available", Name = "build", Outcome = outcome, HeadSha = new string('a', 40) } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("checks");
        await h.SettleAsync();
        await Assert.That(h.Model.Rows.Single().Outcome).IsEqualTo("pending");
        outcome = "success";
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Model.Rows.SingleOrDefault()?.Outcome == "success", what: "check rows reloaded");
        await Assert.That(h.Model.ChecksStatus.Text).IsEqualTo("Checks passing");
    });

    /// The poll reloads only the open tab, so checks left pending behind another tab go stale while
    /// the summary moves on; opening them again must reload rather than show the stale rows.
    [Test]
    public Task Returning_to_stale_checks_reloads_them() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        var outcome = "pending";
        h.Source.PageItem = section => section == "checks"
            ? new PullRequestCheckDto { Id = "check", Availability = "available", Name = "build", Outcome = outcome, HeadSha = new string('a', 40) } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("checks");
        await h.SettleAsync();
        await h.Model.ShowSectionCommand.Execute("overview");
        outcome = "success";
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Model.ChecksStatus.Text == "Checks passing" && !h.Model.IsReading, what: "summary moved on");
        await h.Model.ShowSectionCommand.Execute("checks");
        await WorkspaceFixtures.WaitUntilAsync(() => h.Model.Rows.SingleOrDefault()?.Outcome == "success", what: "stale checks reloaded");
    });

    /// Checks belong to a commit, and a new head is something the reader learns on its own.
    [Test]
    public Task A_new_head_reloads_the_checks_without_asking() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("checks");
        await h.SettleAsync();
        h.Source.HeadSha = new string('b', 40);
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Model.FreshnessDetail == "Checks ran on commit bbbbbbb" && !h.Model.IsReading, what: "checks for the new head");
        await Assert.That(h.Model.PageNote).IsEmpty();
        await Assert.That(h.Model.Rows.Count).IsEqualTo(1);
    });

    /// A page read can be the first to hear of a new head; it recovers through a fresh overview.
    [Test]
    public Task A_head_changed_page_restart_recovers_by_itself() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        h.Source.RestartNextPage = "head_changed";
        await h.Model.ShowSectionCommand.Execute("checks");
        await WorkspaceFixtures.WaitUntilAsync(() => h.Model.Rows.Count == 1 && !h.Model.IsReading, what: "checks recovered");
        await Assert.That(h.Model.PageNote).IsEmpty();
    });

    /// A click inside the poll's gap used to wait it out with nothing on screen.
    [Test]
    public Task A_refresh_click_shows_progress_at_once_and_runs_within_seconds() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        var lists = h.Source.Lists;
        h.Model.Refresh();
        await Assert.That(h.Model.ShowsRefreshing).IsTrue();
        h.Time.Advance(TimeSpan.FromSeconds(4));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Source.Lists > lists && !h.Model.IsReading, what: "manual refresh ran");
        await Assert.That(h.Model.ShowsRefreshing).IsFalse();
    });

    /// The open tab follows the poll, and the poll never shows the progress a click does.
    [Test]
    public Task The_poll_reloads_the_open_tab_without_showing_progress() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        var body = "First review";
        h.Source.PageItem = section => section == "reviews"
            ? new PullRequestReviewDto { Id = "review", Availability = "available", Body = body, State = "commented" } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("reviews");
        await h.SettleAsync();
        body = "Edited review";
        var sawProgress = false;
        h.Model.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(h.Model.ShowsRefreshing) && h.Model.ShowsRefreshing) sawProgress = true; };
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Model.Rows.SingleOrDefault()?.Body == "Edited review", what: "open tab reloaded by the poll");
        await Assert.That(sawProgress).IsFalse();
    });

    /// A reader who paged on keeps their rows: a reload would restart at page one.
    [Test]
    public Task The_poll_leaves_a_tab_alone_once_more_pages_are_loaded() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.TotalPages = 3;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("conversation");
        await h.SettleAsync();
        await h.Model.LoadMoreCommand.Execute();
        await h.SettleAsync();
        var pages = h.Source.Pages;
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Source.Overviews >= 2 && !h.Model.IsReading, what: "poll ran");
        await Assert.That(h.Source.Pages).IsEqualTo(pages);
        await Assert.That(h.Model.Rows.Count).IsEqualTo(2);
    });

    /// Every tab's footer reads the same way; the commit the checks ran on is hover detail only.
    [Test]
    public Task The_footer_says_updated_on_every_tab() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        foreach (var section in new[] { "overview", "checks", "reviewers", "conversation" }) {
            await h.Model.ShowSectionCommand.Execute(section);
            await h.SettleAsync();
            await Assert.That(h.Model.FreshnessLabel).Matches(@"^Updated \d\d:\d\d:\d\d$");
        }
        await h.Model.ShowSectionCommand.Execute("checks");
        await h.SettleAsync();
        await Assert.That(h.Model.FreshnessDetail).IsEqualTo("Checks ran on commit aaaaaaa");
    });

    [Test]
    public Task Row_details_leave_out_what_the_source_did_not_report() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("checks");
        await h.SettleAsync();
        await Assert.That(h.Model.Rows.Single().Detail).IsEmpty();
        await h.Model.ShowSectionCommand.Execute("threads");
        await h.SettleAsync();
        await Assert.That(h.Model.Rows.Single().Detail).IsEmpty();
    });

    /// A first load and the empty result it may turn into occupy one slot, one at a time.
    [Test]
    public Task A_first_load_shows_the_loading_state_in_the_empty_states_place() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.EmptyPages = true;
        await h.ShowAsync();
        h.Source.PageGate = new();
        await h.Model.ShowSectionCommand.Execute("reviews");
        Dispatcher.UIThread.RunJobs();
        var loading = h.Reader.FindControl<StackPanel>("LoadingState")!;
        var empty = h.Reader.FindControl<StackPanel>("EmptyState")!;
        await Assert.That(h.Model.LoadingNote).IsEqualTo("Loading reviews…");
        await Assert.That(loading.IsEffectivelyVisible).IsTrue();
        await Assert.That(empty.IsEffectivelyVisible).IsFalse();
        await Assert.That(h.Model.PageNote).IsEmpty();
        h.Source.PageGate.SetResult();
        await h.SettleAsync();
        await Assert.That(loading.IsEffectivelyVisible).IsFalse();
        await Assert.That(empty.IsEffectivelyVisible).IsTrue();
    });

    [Test]
    public Task An_empty_section_shows_the_empty_state_instead_of_a_page_note() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.EmptyPages = true;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("reviews");
        await h.SettleAsync();
        await Assert.That(h.Reader.FindControl<StackPanel>("EmptyState")!.IsEffectivelyVisible).IsTrue();
        await Assert.That(h.Model.EmptyNote).IsEqualTo("No submitted reviews.");
        await Assert.That(h.Model.PageNote).IsEmpty();
    });

    [Test]
    public Task Tabs_can_be_selected_with_the_keyboard() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        var tabs = h.Reader.FindControl<TabStrip>("ReaderTabs")!;
        var overview = tabs.GetVisualDescendants().OfType<TabStripItem>().First();
        overview.Focus();
        h.Window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await h.SettleAsync();
        await Assert.That(tabs.SelectedIndex).IsEqualTo(1);
        await Assert.That(h.Model.Section).IsEqualTo("checks");
    });

    [Test]
    public Task A_lost_access_lease_disables_navigation_and_masks_all_content_lists() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        h.Model.SelectedTabIndex = 2;
        await h.SettleAsync();
        h.Model.SetForeground(false);
        Dispatcher.UIThread.RunJobs();
        await Assert.That(h.Reader.FindControl<TabStrip>("ReaderTabs")!.IsEnabled).IsFalse();
        await Assert.That(h.Card.FindControl<Button>("SidebarChecksButton")!.IsEnabled).IsFalse();
        h.Model.SelectedTabIndex = 3;
        await Assert.That(h.Model.Section).IsEqualTo("reviewers");
        await Assert.That(h.Model.ReviewerRows).IsEmpty();
        await Assert.That(h.Model.CheckRows).IsEmpty();
        await Assert.That(h.Model.DiscussionRows).IsEmpty();
        await Assert.That(h.Model.LifecycleStatus.Text).IsEmpty();
        await Assert.That(h.Model.ChecksStatus.Text).IsEmpty();
    });

    [Test]
    public Task A_ready_page_cannot_hide_the_age_of_a_stale_overview() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(h.Source.Overview(subject) with {
            Kind = PullRequestReadKind.Stale, FetchedAt = h.Time.GetUtcNow().UtcDateTime.AddMinutes(-5),
        }));
        await h.ShowAsync();
        h.Model.SelectedTabIndex = 2;
        await h.SettleAsync();
        await Assert.That(h.Model.HasNotice).IsFalse();
        var freshness = h.Card.FindControl<TextBlock>("OverviewFreshness")!;
        await Assert.That(freshness.IsEffectivelyVisible).IsTrue();
        await Assert.That(freshness.Text).Contains("Earlier snapshot");
        await Assert.That(freshness.Text).Contains(h.Model.FetchedLabel);
    });

    [Test]
    public Task An_incomplete_check_page_keeps_the_advisory_summary_and_its_provenance() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.TotalPages = 2;
        await h.ShowAsync();
        h.Model.SelectedTabIndex = 1;
        await h.SettleAsync();
        await Assert.That(h.Model.ChecksStatus.Text).IsEqualTo("Checks passing");
        await Assert.That(h.Model.ChecksStatus.Detail).IsEqualTo("All checks have passed.");
        await Assert.That(h.Model.ChecksStatus.Tip).IsEqualTo("All checks have passed.");
        await Assert.That(h.Model.CheckRows.Single().Status!.IsDanger).IsTrue();
        await h.Model.LoadMoreCommand.Execute();
        await h.SettleAsync();
        await Assert.That(h.Model.ChecksStatus.Text).IsEqualTo("2 failed");
        await Assert.That(h.Model.ChecksStatus.IsDanger).IsTrue();
    });

    /// A press focuses the comment's viewer, and a viewer taller than the reader brought into view
    /// on focus scrolls its top to the top — under the pointer, so the release misses the header.
    /// The middle comment is the one toggled: collapsing the last would shrink the extent under
    /// the offset, which is a clamp, not a jump.
    [Test]
    public Task A_press_on_a_section_header_toggles_it_without_scrolling_the_reader() => RunOnUiAsync(async () => {
        await using var h = await ShowConversationAsync(pages: 3);
        await Assert.That(SectionHeaders(h.Reader).Count).IsEqualTo(3);
        var scroll = h.Reader.FindControl<ScrollViewer>("ReaderScroll")!;
        var header = SectionHeaders(h.Reader)[1];
        scroll.Offset = new Vector(0, header.TranslatePoint(new Point(0, 0), scroll)!.Value.Y - 100);
        h.Window.UpdateLayout();
        var offset = scroll.Offset.Y;
        await Assert.That(header.TranslatePoint(new Point(0, 0), scroll)!.Value.Y).IsEqualTo(100).Within(1);

        Click(h.Window, header);
        header = SectionHeaders(h.Reader)[1];
        await Assert.That(header.IsChecked).IsFalse();
        await Assert.That(scroll.Offset.Y).IsEqualTo(offset).Within(1);
        await Assert.That(header.TranslatePoint(new Point(0, 0), scroll)!.Value.Y).IsEqualTo(100).Within(1);
    });

    /// Rows already shown keep their controls, and with them what the reader did to them.
    [Test]
    public Task Loading_more_comments_keeps_the_rows_already_shown() => RunOnUiAsync(async () => {
        await using var h = await ShowConversationAsync(pages: 3, load: 2);
        var list = h.Reader.FindControl<ItemsControl>("DiscussionRows")!;
        Click(h.Window, SectionHeaders(h.Reader)[0]);
        await Assert.That(SectionHeaders(h.Reader)[0].IsChecked).IsFalse();
        var views = list.GetVisualDescendants().OfType<MarkdownView>().ToList();
        await Assert.That(views.Count).IsEqualTo(2);

        await h.Model.LoadMoreCommand.Execute();
        await h.SettleAsync();
        var after = list.GetVisualDescendants().OfType<MarkdownView>().ToList();
        await Assert.That(after.Count).IsEqualTo(3);
        await Assert.That(ReferenceEquals(after[0], views[0])).IsTrue();
        await Assert.That(ReferenceEquals(after[1], views[1])).IsTrue();
        await Assert.That(SectionHeaders(h.Reader)[0].IsChecked).IsFalse();
    });

    static async Task<PullRequestViewTestHost> ShowConversationAsync(int pages, int? load = null) {
        var h = new PullRequestViewTestHost();
        var body = string.Concat(Enumerable.Repeat("line\n\n", 30));
        var n = 0;
        h.Source.TotalPages = pages;
        h.Source.PageItem = section => section == "conversation"
            ? new PullRequestCommentDto { Id = "c" + n++, Availability = "available", Body = $"<details open>\n<summary>S</summary>\n\n{body}</details>\n\nafter" } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("conversation");
        await h.SettleAsync();
        for (var i = 1; i < (load ?? pages); i++) { await h.Model.LoadMoreCommand.Execute(); await h.SettleAsync(); }
        return h;
    }

    static List<ToggleButton> SectionHeaders(Visual root) =>
        root.GetVisualDescendants().OfType<ToggleButton>().Where(b => b.Classes.Contains("markdown-details-summary")).ToList();

    static void Click(Window window, Control control) {
        var point = control.TranslatePoint(new Point(10, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
