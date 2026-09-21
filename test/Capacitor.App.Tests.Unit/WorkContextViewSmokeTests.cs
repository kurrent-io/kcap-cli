using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the work-context pane on its own: the view is hosted in a
/// plain Window with its view model set directly, as WorkspaceViewSmokeTests hosts the workspace.
public class WorkContextViewSmokeTests {
    const string SessionA = "0123456789abcdef0123456789abcdef";

    /// Disposal closes the window and tears the view model down, so a failed assertion leaves
    /// neither behind in the shared headless session.
    sealed class Host : IAsyncDisposable {
        public BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        public FakeWorkContextSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public SessionSubagents Subagents { get; }
        public WorkContextViewModel Vm { get; }
        public Window Window { get; }

        public Host() {
            Subagents = new SessionSubagents(Time);
            Vm = new WorkContextViewModel(Presence, Source, Time, Opener, Subagents);
            Window = new Window { Content = new WorkContextView { DataContext = Vm }, Width = 320, Height = 900 };
        }

        public async Task ShowAsync(WorkContextRead read) {
            Source.Enqueue(read);
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Presence.OnNext(WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: SessionA));
            await (Vm.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public T Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        public async ValueTask DisposeAsync() {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
            await Vm.TeardownAsync();
        }
    }

    /// A key-titled item with no tracker title, its seed issue untitled too, one contributor
    /// holding two sessions: the shape the server serves for a fresh key-only declaration.
    static WorkContextRead KeyOnlyRead(string issueKey = "WK-2198", string? issueTitle = null) {
        var row = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "WK-2198", Source = "mcp", Confidence = 1, IsPrimary = true };
        var item = new WorkItemDto {
            WorkItemId = "w1",
            Title = "WK-2198",
            Key = new WorkItemKeyDto { ShortKey = "WK-2198", Provider = "linear", Kind = "issue", Value = "WK-2198" },
            State = new WorkItemStateDto { Kind = "in_flight" },
            Links = [new WorkItemLinkDto {
                Kind = "issue", Provider = "linear", Value = issueKey, ShortKey = issueKey,
                Url = $"https://linear.app/x/issue/{issueKey}", Title = issueTitle, LinkClass = "link", IsSeed = true,
            }],
            Contributors = [new WorkItemContributorDto { UserId = "u1", DisplayName = "Ada" }],
            SessionCount = 2,
        };
        return new WorkContextRead(WorkContextReadKind.Ready, [row], row, item, null, new SessionSummaryDto { SessionId = SessionA }, false, false, false, null);
    }

    static byte Alpha(IBrush? brush) => brush switch {
        null               => 0,
        ISolidColorBrush s => s.Color.A,
        _                  => 255,
    };

    /// Before the daemon reports the agent the pane knows no harness, checkout or requester, so the
    /// sections that would hold only dashes stay out and the waiting note stands alone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Session_and_who_sections_wait_for_the_agent() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            host.Window.Show();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await Assert.That(host.Vm.HasAgent).IsFalse();
            await Assert.That(host.Find<TextBlock>("PhaseNoteText").IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Find<Control>("SessionSection").IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Find<Control>("WhoSection").IsEffectivelyVisible).IsFalse();

            host.Presence.OnNext(WorkspaceFixtures.Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj"));
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await Assert.That(host.Vm.HasAgent).IsTrue();
            await Assert.That(host.Find<Control>("SessionSection").IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Find<Control>("WhoSection").IsEffectivelyVisible).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("WK-2198", true)]
    [Arguments("WK-2199", false)]
    public async Task Key_only_items_and_untitled_issues_keep_each_distinct_key_visible(string issueKey, bool inline) {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead(issueKey));

            var key = host.Find<TextBlock>("WorkContextKey");
            await Assert.That(key.Text).IsEqualTo("WK-2198");
            await Assert.That(key.IsEffectivelyVisible).IsTrue();
            await Assert.That(ReferenceEquals(key.Foreground, host.Window.FindResource("KcapTextBrush"))).IsTrue()
                .Because("the key is identity, not a status colour");
            await Assert.That(host.Find<TextBlock>("WorkContextTitle").IsEffectivelyVisible).IsFalse();

            var issueSection = host.Find<StackPanel>("IssueSection");
            var open = host.Find<Button>("OpenWorkItemButton");
            await Assert.That(open.IsEffectivelyVisible).IsTrue();
            await Assert.That(open.Parent).IsSameReferenceAs(host.Find<Button>("RefreshButton").Parent);
            await Assert.That(issueSection.IsEffectivelyVisible).IsEqualTo(!inline);
            if (!inline) {
                var linkKey = host.Find<TextBlock>("IssueKey");
                var linkTitle = ((WorkContextView)host.Window.Content!).FindControl<TextBlock>("IssueTitle")!;
                await Assert.That(linkKey.Text).IsEqualTo(issueKey);
                await Assert.That(linkKey.IsEffectivelyVisible).IsTrue();
                await Assert.That(linkTitle.IsEffectivelyVisible).IsFalse();

                var issueHeader = host.Find<Button>("IssueHeader");
                await Assert.That(host.Vm.SeparateIssue!.CanOpen).IsTrue();
                await Assert.That(issueHeader.Command).IsSameReferenceAs(host.Vm.ToggleIssuesCommand);
                await host.Vm.ToggleIssuesCommand.Execute();
                await Assert.That(host.Opener.Opened).IsEquivalentTo(new[] { $"https://linear.app/x/issue/{issueKey}" });
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hovering_the_issue_title_paints_no_button_chrome() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead("WK-2199", "A linked issue"));

            var button = host.Find<Button>("IssueTitleButton");
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(centre);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(button.Classes.Contains(":pointerover")).IsTrue().Because("the hover must register for the assertion to mean anything");

            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            await Assert.That(Alpha(presenter.Background)).IsEqualTo((byte)0);
            await Assert.That(Alpha(presenter.BorderBrush)).IsEqualTo((byte)0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Issue_title_wraps_instead_of_sharing_a_horizontal_row_with_the_key() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead("WK-2199", new string('x', 160)));

            var key = host.Find<TextBlock>("IssueKey");
            var title = host.Find<TextBlock>("IssueTitle");
            await Assert.That(title.TextWrapping).IsEqualTo(TextWrapping.Wrap);
            var row = title.Parent as StackPanel;
            await Assert.That(row is null || row.Orientation != Orientation.Horizontal || !row.Children.Contains(key)).IsTrue();
            await Assert.That(title.Bounds.Width).IsLessThanOrEqualTo(host.Find<ScrollViewer>("PaneScroll").Bounds.Width);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_who_row_shows_sessions_when_every_contributor_is_listed() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var count = host.Find<TextBlock>("WhoCountText");
            await Assert.That(count.Text).IsEqualTo("2 sessions");
            await Assert.That(count.IsEffectivelyVisible).IsTrue();

            var list = host.Find<ItemsControl>("ContributorList");
            await Assert.That(list.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Vm.PeopleExpanded).IsFalse();
            var name = list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Ada");
            await Assert.That(name.IsEffectivelyVisible).IsTrue();
        });
    }

    /// Who's-on-it initials are identity, not a live/settled status, so they must not paint
    /// success green. Border ignores alignment on a direct child; the letter lives in a Panel
    /// so Horizontal/VerticalAlignment actually centre it in the disc.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Who_avatar_is_neutral_and_centres_the_initial() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());
            host.Window.UpdateLayout();

            var avatar = host.Find<ItemsControl>("ContributorList")
                .GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("avatar"));
            var letter = avatar.GetVisualDescendants().OfType<TextBlock>().Single();
            var raised = host.Window.FindResource("KcapSurfaceRaisedBrush");
            var muted = host.Window.FindResource("KcapMutedBrush");

            await Assert.That(ReferenceEquals(avatar.Background, raised)).IsTrue();
            await Assert.That(ReferenceEquals(letter.Foreground, muted)).IsTrue();
            await Assert.That(letter.Parent).IsTypeOf<Panel>();
            await Assert.That(letter.HorizontalAlignment).IsEqualTo(HorizontalAlignment.Center);
            await Assert.That(letter.VerticalAlignment).IsEqualTo(VerticalAlignment.Center);
            await Assert.That(letter.TextAlignment).IsEqualTo(TextAlignment.Center);

            var letterMid = letter.TranslatePoint(
                new Point(letter.Bounds.Width / 2, letter.Bounds.Height / 2), avatar)!.Value;
            await Assert.That(Math.Abs(letterMid.X - avatar.Bounds.Width / 2)).IsLessThan(1.5);
            await Assert.That(Math.Abs(letterMid.Y - avatar.Bounds.Height / 2)).IsLessThan(1.5);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task This_session_part_mark_is_purple_and_settled_stays_green() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            var primary = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "WK-2198", Source = "mcp", Confidence = 1, IsPrimary = true };
            var here = new SessionWorkItemAssignmentDto { WorkItemId = "p1", Label = "part", Source = "mcp", Confidence = 1, IsPrimary = false };
            var item = new WorkItemDto {
                WorkItemId = "w1",
                Title = "WK-2198",
                Key = new WorkItemKeyDto { ShortKey = "WK-2198", Provider = "linear", Kind = "issue", Value = "WK-2198" },
                State = new WorkItemStateDto { Kind = "in_flight" },
                Parts = [
                    new WorkItemPartDto { WorkItemId = "p1", Title = "Here", Ordinal = 0 },
                    new WorkItemPartDto { WorkItemId = "p2", Title = "Done", Ordinal = 1, IsSettled = true },
                ],
            };
            await host.ShowAsync(new WorkContextRead(WorkContextReadKind.Ready, [primary, here], primary, item, null,
                new SessionSummaryDto { SessionId = SessionA }, false, false, false, null));

            var purple = host.Window.FindResource("KcapPurpleBrush");
            var green = host.Window.FindResource("KcapSuccessBrush");
            var marks = MarksBeside(host.Find<ItemsControl>("PartsList"), "Here");
            await Assert.That(marks.Any(e => ReferenceEquals(e.Stroke, purple) || ReferenceEquals(e.Fill, purple))).IsTrue();
            await Assert.That(marks.Any(e => ReferenceEquals(e.Stroke, green) || ReferenceEquals(e.Fill, green))).IsFalse();

            marks = MarksBeside(host.Find<ItemsControl>("PartsList"), "Done");
            await Assert.That(marks.Any(e => ReferenceEquals(e.Fill, green))).IsTrue();
            await Assert.That(marks.Any(e => ReferenceEquals(e.Stroke, purple) || ReferenceEquals(e.Fill, purple))).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Signed_out_sign_in_uses_primary_not_status_green() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(WorkContextRead.Of(WorkContextReadKind.SignedOut));

            var signIn = host.Find<Button>("SignInButton");
            await Assert.That(signIn.IsEffectivelyVisible).IsTrue();
            await Assert.That(signIn.Classes.Contains("kcapPrimary")).IsTrue();
            await Assert.That(ReferenceEquals(signIn.Background, host.Window.FindResource("KcapPrimaryBrush"))).IsTrue();
            await Assert.That(ReferenceEquals(signIn.Background, host.Window.FindResource("KcapSuccessBrush"))).IsFalse();
        });
    }

    static Ellipse[] MarksBeside(ItemsControl list, string title) {
        var row = list.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == title).Parent as StackPanel;
        return row!.Children.OfType<Panel>().First().Children.OfType<Ellipse>().Where(e => e.IsEffectivelyVisible).ToArray();
    }

    static WorkContextRead CrowdedWhoRead() {
        var read = KeyOnlyRead();
        var item = read.Item! with {
            Contributors = [
                new WorkItemContributorDto { UserId = "u1", DisplayName = "Ada" },
                new WorkItemContributorDto { UserId = "u2", DisplayName = "Bob" },
                new WorkItemContributorDto { UserId = "u3", DisplayName = "Cyd" },
                new WorkItemContributorDto { UserId = "u4", DisplayName = "Dee" },
                new WorkItemContributorDto { UserId = "u5", DisplayName = "Eve" },
            ],
        };
        return read with { Item = item };
    }

    static ContentPresenter WhoPresenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Who_header_has_no_hover_fill_when_the_list_does_not_overflow() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var button = host.Find<Button>("WhoToggle");
            await Assert.That(button.Classes.Contains("expandable")).IsFalse();

            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(centre);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(button.Classes.Contains(":pointerover")).IsTrue()
                .Because("the hover must register for the assertion to mean anything");
            await Assert.That(Alpha(WhoPresenter(button).Background)).IsEqualTo((byte)0);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Who_header_does_not_dim_on_press_when_the_list_does_not_overflow() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var button = host.Find<Button>("WhoToggle");
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(centre);
            host.Window.MouseDown(centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(button.Classes.Contains(":pressed")).IsTrue()
                .Because("the press must register for the assertion to mean anything");
            await Assert.That(WhoPresenter(button).Opacity).IsEqualTo(1);
            await Assert.That(Alpha(WhoPresenter(button).Background)).IsEqualTo((byte)0);
            host.Window.MouseUp(centre, MouseButton.Left);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Who_header_paints_hover_when_the_list_overflows() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(CrowdedWhoRead());

            var button = host.Find<Button>("WhoToggle");
            await Assert.That(host.Vm.PeopleOverflows).IsTrue();
            await Assert.That(button.Classes.Contains("expandable")).IsTrue();

            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(centre);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(button.Classes.Contains(":pointerover")).IsTrue()
                .Because("the hover must register for the assertion to mean anything");
            await Assert.That(ReferenceEquals(WhoPresenter(button).Background, host.Window.FindResource("KcapSurfaceRaisedBrush"))).IsTrue();
        });
    }

    /// Each row carries its state in words as well as in the dot, and the failed word is painted
    /// danger; the section hides whole when the session spawned none, starts folded and opens on
    /// its toggle.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_subagents_section_lists_rows_by_state_and_hides_when_the_session_spawned_none() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());
            var section = host.Find<StackPanel>("SubagentsSection");
            await Assert.That(section.IsEffectivelyVisible).IsFalse();

            var now = host.Time.GetUtcNow();
            host.Subagents.Apply(new ChatProjectionResult([], [], [
                new SubagentSignal.Started("c1", "Explore", "Map desktop chat UI surfaces", now.AddSeconds(-18)),
                new SubagentSignal.Started("c2", "Reviewer", "Check the plan", now.AddMinutes(-3)),
                new SubagentSignal.Detached("c1", "a1"),
                new SubagentSignal.Finished("c2", null, SubagentOutcome.Failed, now.AddSeconds(-132)),
            ]));
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            await Assert.That(section.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Find<ItemsControl>("SubagentList").IsEffectivelyVisible).IsFalse();

            await host.Vm.ToggleSubagentsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Assert.That(host.Find<ItemsControl>("SubagentsSummary").IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Find<TextBlock>("SubagentsHeaderText").Text).IsEqualTo("1 of 2 running");
            var texts = section.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            await Assert.That(texts).Contains("Explore");
            await Assert.That(texts).Contains("running in background · 18s");
            await Assert.That(texts).Contains("Map desktop chat UI surfaces");
            await Assert.That(texts).Contains("Reviewer");
            await Assert.That(texts).Contains("failed · 48s");

            var failed = section.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "failed · 48s");
            var danger = (ISolidColorBrush)Avalonia.Application.Current!.FindResource("KcapDangerBrush")!;
            await Assert.That(((ISolidColorBrush)failed.Foreground!).Color).IsEqualTo(danger.Color);
            await Assert.That(section.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsEqualTo(1);

            await host.Vm.ToggleSubagentsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Assert.That(host.Find<ItemsControl>("SubagentList").IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Find<ItemsControl>("SubagentsSummary").IsEffectivelyVisible).IsTrue();
        });
    }

    /// Collapsed, the header carries one count per state beside the rows' own mark, and a state
    /// nothing is in shows neither mark nor number.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_collapsed_subagents_header_shows_a_marked_count_per_state_and_omits_empty_states() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());
            var now = host.Time.GetUtcNow();
            host.Subagents.Apply(new ChatProjectionResult([], [], [
                new SubagentSignal.Started("c1", "Explore", "", now.AddSeconds(-18)),
                new SubagentSignal.Started("c2", "Reviewer", "", now.AddMinutes(-3)),
                new SubagentSignal.Started("c3", "Planner", "", now.AddMinutes(-4)),
                new SubagentSignal.Finished("c2", null, SubagentOutcome.Failed, now.AddSeconds(-132)),
                new SubagentSignal.Finished("c3", null, SubagentOutcome.Failed, now.AddSeconds(-140)),
            ]));
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            var summary = host.Find<ItemsControl>("SubagentsSummary");
            await Assert.That(summary.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Find<TextBlock>("SubagentsHeaderText").IsEffectivelyVisible).IsFalse();
            var numbers = summary.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            await Assert.That(numbers).IsEquivalentTo(new[] { "1", "2" }, CollectionOrdering.Matching);

            var marks = summary.GetVisualDescendants().OfType<Ellipse>().Where(e => e.IsEffectivelyVisible).ToList();
            var warning = ((ISolidColorBrush)Avalonia.Application.Current!.FindResource("KcapWarningBrush")!).Color;
            var danger = ((ISolidColorBrush)Avalonia.Application.Current!.FindResource("KcapDangerBrush")!).Color;
            await Assert.That(marks.Select(e => ((ISolidColorBrush)(e.Fill ?? e.Stroke)!).Color))
                .IsEquivalentTo(new[] { warning, danger }, CollectionOrdering.Matching);
            await Assert.That(summary.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsEqualTo(1);
        });
    }

    /// The name trims to the pane and the state line sits beneath it, so a long name can push
    /// neither off the pane.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_long_subagent_name_trims_and_keeps_the_state_line_beneath_it() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var now = host.Time.GetUtcNow();
            var longName = new string('x', 80);
            host.Subagents.Apply(new ChatProjectionResult([], [], [
                new SubagentSignal.Started("c1", longName, "", now.AddSeconds(-18)),
                new SubagentSignal.Detached("c1", "a1"),
            ]));
            await host.Vm.ToggleSubagentsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            var section = host.Find<StackPanel>("SubagentsSection");
            var texts = section.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
            var name = texts.Single(t => t.Text == longName);
            var state = texts.Single(t => t.Text == "running in background · 18s");

            var nameRight = name.TranslatePoint(new Point(name.Bounds.Width, 0), host.Window)!.Value.X;
            var nameBottom = name.TranslatePoint(new Point(0, name.Bounds.Height), host.Window)!.Value.Y;
            var stateTop = state.TranslatePoint(new Point(0, 0), host.Window)!.Value.Y;
            var stateRight = state.TranslatePoint(new Point(state.Bounds.Width, 0), host.Window)!.Value.X;
            await Assert.That(nameRight).IsLessThanOrEqualTo(host.Window.Bounds.Width);
            await Assert.That(stateRight).IsLessThanOrEqualTo(host.Window.Bounds.Width);
            await Assert.That(stateTop).IsGreaterThanOrEqualTo(nameBottom);
        });
    }

    /// "Copied" is set as a local tip; leaving the button has to put the full id back, or the
    /// flash sticks as the tip.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Copying_the_session_id_keeps_the_full_id_on_the_next_hover() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());
            host.Vm.ToggleSessionCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var button = host.Find<Button>("SessionIdButton");
            await Assert.That(button.IsEffectivelyVisible).IsTrue();
            var origin = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(origin);
            host.Window.MouseDown(origin, MouseButton.Left);
            host.Window.MouseUp(origin, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            var whileCopied = ToolTip.GetTip(button) as string;
            await Assert.That(whileCopied).IsEqualTo("Copied");

            host.Window.MouseMove(new Point(1, 1));
            Dispatcher.UIThread.RunJobs();
            await Assert.That(ToolTip.GetTip(button) as string).IsEqualTo(SessionA);
        });
    }

    /// Who's on it is work-item data: it sits under the work item, before pull request.
    /// Session stays last (after subagents).
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Section_order_is_work_item_then_people_then_pr_then_issue_then_subagents_then_session() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var body = host.Find<ScrollViewer>("PaneScroll").Content as StackPanel
                ?? throw new InvalidOperationException("pane stack");
            int At(Control c) {
                Control? walk = c;
                while (walk is not null && !ReferenceEquals(walk.Parent, body))
                    walk = walk.Parent as Control;
                return body.Children.IndexOf(walk!);
            }

            var who = host.Find<StackPanel>("WhoSection");
            var pr = host.Find<StackPanel>("PullRequestSection");
            var issue = host.Find<StackPanel>("IssueSection");
            var subagents = host.Find<StackPanel>("SubagentsSection");
            var session = host.Find<Button>("SessionToggle");

            await Assert.That(At(who)).IsLessThan(At(pr));
            await Assert.That(At(pr)).IsLessThan(At(issue));
            await Assert.That(At(issue)).IsLessThan(At(session));
            if (subagents.IsEffectivelyVisible)
                await Assert.That(At(subagents)).IsLessThan(At(session));
            else
                await Assert.That(At(issue)).IsLessThan(At(subagents));
        });
    }

    /// The card is the pane's live PR surface: its picker switches between the linked PRs and its
    /// checks and review rows read without opening the reader tab. Once the list settles empty
    /// the card yields to the pane's own empty copy rather than standing as a bare frame.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pull_request_card_shows_its_picker_and_status_rows_in_the_pane() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            var source = new FakePullRequestSource(host.Time);
            var pullRequests = new PullRequestContextViewModel(host.Presence, source, host.Time, new RecordingOpener(), () => { });
            host.Vm.PullRequests = pullRequests;
            pullRequests.SetForeground(true);
            try {
                await host.ShowAsync(KeyOnlyRead());
                await WorkspaceFixtures.WaitUntilAsync(() => pullRequests.CanReveal && !pullRequests.IsReading, what: "linked PRs loaded");
                Dispatcher.UIThread.RunJobs();
                host.Window.UpdateLayout();

                var card = host.Find<PullRequestCard>("PullRequestCard");
                await Assert.That(card.IsEffectivelyVisible).IsTrue();
                await Assert.That(card.DataContext).IsSameReferenceAs(pullRequests);
                var header = host.Find<Button>("PullRequestHeader");
                await Assert.That(header.IsEffectivelyVisible).IsTrue();
                var meta = host.Find<TextBlock>("PullRequestNumberMeta");
                await Assert.That(meta.Text).IsEqualTo(pullRequests.SectionMeta);
                await Assert.That(meta.Text).IsEqualTo("2");
                await Assert.That(pullRequests.SectionEyebrow).IsEqualTo("PULL REQUESTS");
                await Assert.That(host.Find<TextBlock>("LifecycleNumber").Text).IsEqualTo(pullRequests.NumberLabel);
                await Assert.That(card.Content).IsTypeOf<StackPanel>();
                var picker = host.Find<ComboBox>("PullRequestSelector");
                await Assert.That(picker.IsEffectivelyVisible).IsTrue();
                await Assert.That(((IEnumerable<PullRequestChoice>)picker.ItemsSource!).Count()).IsEqualTo(2);
                await Assert.That(picker.Classes.Contains("kcapField")).IsTrue();
                await Assert.That(host.Find<Button>("SidebarChecksButton").IsEffectivelyVisible).IsTrue();
                await Assert.That(host.Find<Button>("SidebarReviewsButton").IsEffectivelyVisible).IsTrue();
                await Assert.That(host.Find<TextBlock>("PullRequestEmptyText").IsEffectivelyVisible).IsFalse();

                source.Links = [];
                host.Time.Advance(TimeSpan.FromSeconds(16));
                await pullRequests.RefreshCommand.Execute();
                await WorkspaceFixtures.WaitUntilAsync(() => !pullRequests.HasPullRequest && !pullRequests.IsReading, what: "PRs unlinked");
                Dispatcher.UIThread.RunJobs();
                host.Window.UpdateLayout();
                await Assert.That(card.IsEffectivelyVisible).IsFalse();
                await Assert.That(host.Find<TextBlock>("PullRequestEmptyText").IsEffectivelyVisible).IsTrue();
            } finally {
                await pullRequests.TeardownAsync();
            }
        });
    }
}
