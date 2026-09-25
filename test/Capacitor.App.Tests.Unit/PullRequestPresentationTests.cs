using System.Globalization;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
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
        await Assert.That(checksTab.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().IsEffectivelyVisible).IsTrue();

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

    [Test]
    [Arguments(700)]
    [Arguments(360)]
    public Task Reviewer_rows_are_compact_and_long_names_fit_the_reader(double width) => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost(width);
        h.Source.PageItem = section => section == "reviewers" ? new PullRequestReviewerDto {
            Id = "reviewer", Availability = "available", ReviewState = "commented",
            Actor = new() { Id = "bot", Kind = "bot", Login = new string('r', 100) }
        } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("reviewers");
        await h.SettleAsync();
        var list = h.Reader.FindControl<ItemsControl>("ReviewerRows")!;
        var row = list.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("prRow"));
        await Assert.That(row.Bounds.Height).IsLessThan(70);
        await Assert.That(row.Bounds.Width).IsLessThanOrEqualTo(width);
        var status = row.GetVisualDescendants().OfType<PullRequestStatusLabel>().Single();
        await Assert.That(status.FindControl<TextBlock>("StatusText")!.Text).IsEqualTo("Commented");
        await Assert.That(status.GetVisualDescendants().OfType<Path>().Single(path => path.IsEffectivelyVisible).Data).IsNotNull();
        await Assert.That(h.Model.Rows.Single().IsBot).IsTrue();
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == new string('r', 100));
        await Assert.That(title.Bounds.Width).IsGreaterThan(0);
        await Assert.That(title.Bounds.Right).IsLessThanOrEqualTo(row.Bounds.Width);
    });

    /// A requested reviewer who has not reviewed carries no state; that is a wait, not an unknown,
    /// and the label stays on one line beside the name at the narrow width.
    [Test]
    public Task A_requested_reviewer_without_a_review_reads_as_awaiting_on_one_line() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost(360);
        h.Source.PageItem = section => section == "reviewers" ? new PullRequestReviewerDto {
            Id = "reviewer", Availability = "available", Requested = true, Actor = new() { Id = "octocat", Kind = "user", Login = "octocat" }
        } : null;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("reviewers");
        await h.SettleAsync();
        var row = h.Reader.FindControl<ItemsControl>("ReviewerRows")!.GetVisualDescendants().OfType<Border>().Single(border => border.Classes.Contains("prRow"));
        var text = row.GetVisualDescendants().OfType<PullRequestStatusLabel>().Single().FindControl<TextBlock>("StatusText")!;
        await Assert.That(text.Text).IsEqualTo("Awaiting review");
        await Assert.That(text.Bounds.Height).IsLessThanOrEqualTo(text.LineHeight + 1);
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

    /// Threads alone carries the resolved toggle; the content must start at the same height on every review sub-tab.
    [Test]
    public Task Review_sub_tabs_keep_the_content_at_one_height() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        var scroll = h.Reader.FindControl<ScrollViewer>("ReaderScroll")!;
        var tops = new List<double>();
        foreach (var section in new[] { "reviewers", "reviews", "threads" }) {
            await h.Model.ShowSectionCommand.Execute(section);
            await h.SettleAsync();
            tops.Add(scroll.TranslatePoint(new Point(0, 0), h.Reader)!.Value.Y);
        }
        await Assert.That(h.Reader.FindControl<ToggleSwitch>("ShowResolvedToggle")!.IsEffectivelyVisible).IsTrue();
        await Assert.That(tops.Distinct().Count()).IsEqualTo(1);
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

    /// A click inside the poll's gap used to wait it out with nothing on screen.
    [Test]
    public Task A_refresh_click_shows_progress_at_once_and_runs_within_seconds() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        var lists = h.Source.Lists;
        h.Model.Refresh();
        await Assert.That(h.Model.IsReading).IsTrue();
        h.Time.Advance(TimeSpan.FromSeconds(4));
        await WorkspaceFixtures.WaitUntilAsync(() => h.Source.Lists > lists && !h.Model.IsReading, what: "manual refresh ran");
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
    public Task Sidebar_status_rows_keep_a_hand_cursor_without_a_hover_wash() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        await h.ShowAsync();
        var checks = h.Card.FindControl<Button>("SidebarChecksButton")!;
        var presenter = checks.GetVisualDescendants().OfType<ContentPresenter>().Single(item => item.Name == "PART_ContentPresenter" && item.TemplatedParent == checks);
        await Assert.That(checks.Cursor?.ToString()).Contains("Hand");
        h.Window.MouseMove(checks.TranslatePoint(new Point(checks.Bounds.Width / 2, checks.Bounds.Height / 2), h.Window)!.Value);
        Dispatcher.UIThread.RunJobs();
        await Assert.That(((ISolidColorBrush)presenter.Background!).Color.A).IsEqualTo((byte)0);

        h.Source.Failure = "transient";
        h.Time.Advance(TimeSpan.FromSeconds(21));
        await h.Model.RefreshCommand.Execute();
        await WorkspaceFixtures.WaitUntilAsync(() => !h.Model.IsReading, what: "access refresh fails");
        await Assert.That(checks.IsEnabled).IsFalse();
        await Assert.That(h.Model.CanDisplay).IsTrue();
        await Assert.That(((ISolidColorBrush)presenter.Background!).Color.A).IsEqualTo((byte)0);
        await Assert.That(presenter.Opacity).IsEqualTo(0.45);
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

    /// A rule separates two comments; the last comment has nothing to separate from.
    [Test]
    public Task Comments_are_ruled_apart_but_the_last_carries_no_rule() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.TotalPages = 3;
        await h.ShowAsync();
        await h.Model.ShowSectionCommand.Execute("conversation");
        await h.SettleAsync();
        await h.Model.LoadMoreCommand.Execute();
        await h.SettleAsync();
        var list = h.Reader.FindControl<ItemsControl>("DiscussionRows")!;
        var rows = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("prDiscussion")).ToList();
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[0].BorderThickness.Bottom).IsEqualTo(1);
        await Assert.That(rows[1].BorderThickness.Bottom).IsEqualTo(0);
        await h.Model.LoadMoreCommand.Execute();
        await h.SettleAsync();
        rows = list.GetVisualDescendants().OfType<Border>().Where(border => border.Classes.Contains("prDiscussion")).ToList();
        await Assert.That(rows.Count).IsEqualTo(3);
        await Assert.That(rows[1].BorderThickness.Bottom).IsEqualTo(1);
        await Assert.That(rows[2].BorderThickness.Bottom).IsEqualTo(0);
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
