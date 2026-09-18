using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Microsoft.Extensions.Time.Testing;
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
        public SessionSubagents Subagents { get; }
        public WorkContextViewModel Vm { get; }
        public Window Window { get; }

        public Host() {
            Subagents = new SessionSubagents(Time);
            Vm = new WorkContextViewModel(Presence, Source, Time, new RecordingOpener(), Subagents);
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
    static WorkContextRead KeyOnlyRead(string issueKey = "WK-2198") {
        var row = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "WK-2198", Source = "mcp", Confidence = 1, IsPrimary = true };
        var item = new WorkItemDto {
            WorkItemId = "w1",
            Title = "WK-2198",
            Key = new WorkItemKeyDto { ShortKey = "WK-2198", Provider = "linear", Kind = "issue", Value = "WK-2198" },
            State = new WorkItemStateDto { Kind = "in_flight" },
            Links = [new WorkItemLinkDto {
                Kind = "issue", Provider = "linear", Value = issueKey, ShortKey = issueKey,
                Url = $"https://linear.app/x/issue/{issueKey}", LinkClass = "link", IsSeed = true,
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
            await Assert.That(host.Find<TextBlock>("WorkContextTitle").IsEffectivelyVisible).IsFalse();

            var issueCard = host.Find<ContentControl>("IssueCard");
            await Assert.That(host.Find<Button>("OpenWorkItemButton").IsEffectivelyVisible).IsTrue();
            await Assert.That(issueCard.IsEffectivelyVisible).IsEqualTo(!inline);
            if (!inline) {
                var linkKey = issueCard.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "LinkKey");
                var linkTitle = issueCard.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "LinkTitle");
                await Assert.That(linkKey.Text).IsEqualTo(issueKey);
                await Assert.That(linkKey.IsEffectivelyVisible).IsTrue();
                await Assert.That(linkTitle.IsEffectivelyVisible).IsFalse();
            }
        });
    }

    /// The card is its own chrome: the wrapping button must paint nothing on hover, or the theme's
    /// hover fill shows at the button's smaller corner radius behind the card's rounded border.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hovering_a_link_card_paints_no_chrome_outside_its_rounded_border() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead("WK-2199"));

            var button = host.Find<ContentControl>("IssueCard").GetVisualDescendants().OfType<Button>().First();
            var card = button.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("card"));
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
            host.Window.MouseMove(centre);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(button.Classes.Contains(":pointerover")).IsTrue().Because("the hover must register for the assertion to mean anything");

            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            await Assert.That(Alpha(presenter.Background)).IsEqualTo((byte)0);
            await Assert.That(Alpha(presenter.BorderBrush)).IsEqualTo((byte)0);
            await Assert.That(presenter.CornerRadius).IsEqualTo(card.CornerRadius);
            await Assert.That(ReferenceEquals(card.BorderBrush, host.Window.FindResource("KcapFaintBrush"))).IsTrue()
                .Because("the hover cue is the card's own border, inside its rounded outline");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_who_row_counts_people_before_sessions() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var count = host.Find<TextBlock>("WhoCountText");
            await Assert.That(count.Text).IsEqualTo("1 person · 2 sessions");
            await Assert.That(count.IsEffectivelyVisible).IsTrue();
        });
    }

    /// Each row carries its state in words as well as in the dot, and the failed word is painted
    /// danger; the section hides whole when the session spawned none and folds on its toggle.
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
            await Assert.That(host.Find<TextBlock>("SubagentsHeaderText").Text).IsEqualTo("1 running · 2 total");
            var texts = section.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            await Assert.That(texts).Contains("Explore");
            await Assert.That(texts).Contains("background");
            await Assert.That(texts).Contains("running · 18s");
            await Assert.That(texts).Contains("Map desktop chat UI surfaces");
            await Assert.That(texts).Contains("Reviewer");
            await Assert.That(texts).Contains("failed · 48s");
            await Assert.That(texts.Count(t => t == "background")).IsEqualTo(1);

            var failed = section.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "failed · 48s");
            var danger = (ISolidColorBrush)Avalonia.Application.Current!.FindResource("KcapDangerBrush")!;
            await Assert.That(((ISolidColorBrush)failed.Foreground!).Color).IsEqualTo(danger.Color);
            await Assert.That(section.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("toolRunning") && b.IsEffectivelyVisible)).IsEqualTo(1);

            await host.Vm.ToggleSubagentsCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            await Assert.That(host.Find<ItemsControl>("SubagentList").IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Find<TextBlock>("SubagentsHeaderText").IsEffectivelyVisible).IsTrue();
        });
    }

    /// A horizontal StackPanel measures its children unbounded, so the name's ellipsis only
    /// engages once the tag and the state sit in their own columns.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_long_background_subagent_name_stays_clear_of_the_tag_and_the_state() {
        await RunOnUiAsync(async () => {
            await using var host = new Host();
            await host.ShowAsync(KeyOnlyRead());

            var now = host.Time.GetUtcNow();
            var longName = new string('x', 80);
            host.Subagents.Apply(new ChatProjectionResult([], [], [
                new SubagentSignal.Started("c1", longName, "", now.AddSeconds(-18)),
                new SubagentSignal.Detached("c1", "a1"),
            ]));
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();

            var section = host.Find<StackPanel>("SubagentsSection");
            var texts = section.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
            var name = texts.Single(t => t.Text == longName);
            var tag = texts.Single(t => t.Text == "background");
            var state = texts.Single(t => t.Text == "running · 18s");

            await Assert.That(name.Bounds.Right).IsLessThanOrEqualTo(tag.Bounds.Left);
            await Assert.That(name.Bounds.Right).IsLessThanOrEqualTo(state.Bounds.Left);
            await Assert.That(state.Bounds.Right).IsLessThanOrEqualTo(host.Window.Bounds.Width);
        });
    }
}
