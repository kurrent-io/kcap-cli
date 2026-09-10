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
        public WorkContextViewModel Vm { get; }
        public Window Window { get; }

        public Host() {
            Vm = new WorkContextViewModel(Presence, Source, new FakeTimeProvider(), new RecordingOpener());
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
            await Assert.That(host.Find<Button>("InlineIssueButton").IsEffectivelyVisible).IsEqualTo(inline);
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
}
