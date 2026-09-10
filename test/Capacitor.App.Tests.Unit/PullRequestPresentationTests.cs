using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Path = Avalonia.Controls.Shapes.Path;
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
        await Assert.That(reviews.SelectedIndex).IsEqualTo(2);
        await Assert.That(h.Model.Rows.Single().Body).IsEqualTo("Private comment");

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
        await Assert.That(status.GetVisualDescendants().OfType<Path>().Single().Data).IsNotNull();
        await Assert.That(h.Model.Rows.Single().IsBot).IsTrue();
        var title = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == new string('r', 100));
        await Assert.That(title.Bounds.Width).IsGreaterThan(0);
        await Assert.That(title.Bounds.Right).IsLessThanOrEqualTo(row.Bounds.Width);
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
    public Task An_incomplete_check_page_keeps_the_advisory_summary_and_its_provenance() => RunOnUiAsync(async () => {
        await using var h = new PullRequestViewTestHost();
        h.Source.TotalPages = 2;
        await h.ShowAsync();
        h.Model.SelectedTabIndex = 1;
        await h.SettleAsync();
        await Assert.That(h.Model.ChecksStatus.Text).IsEqualTo("Checks passing");
        await Assert.That(h.Model.ChecksStatus.Detail).IsEqualTo("GitHub summary: successful");
        await Assert.That(h.Model.CheckRows.Single().Status!.IsDanger).IsTrue();
        await h.Model.LoadMoreCommand.Execute();
        await h.SettleAsync();
        await Assert.That(h.Model.ChecksStatus.Text).IsEqualTo("2 failed");
        await Assert.That(h.Model.ChecksStatus.IsDanger).IsTrue();
    });
}
