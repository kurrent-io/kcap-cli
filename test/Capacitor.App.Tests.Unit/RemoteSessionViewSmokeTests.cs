using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the remote host: the card templates it shares with the chat
/// pane resolve here too, so a card raised on another machine is answerable in this window.
/// These run under the real Avalonia scheduler (AvaloniaSession.DispatchAsync, not the immediate
/// pin): SessionAccessService publishes from a thread-pool continuation, and only the real
/// scheduler marshals that onto the UI thread before it reaches a binding.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteSessionViewSmokeTests {
    sealed class Host : IDisposable {
        readonly SessionAccessService _access;
        readonly FakeAgentDirectory _directory = new();

        public readonly FakeServerLane Lane = new();
        public readonly FakePermissionService Permissions = new();
        public readonly RemoteSessionViewModel Vm;
        public readonly RemoteSessionView View;
        public readonly Window Window;

        public Host() {
            _access = new SessionAccessService(Lane, new FakeTimeProvider());
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            var row = AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac",
                Vendor = "gemini", OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            });
            _directory.Rows.AddOrUpdate(row);
            Vm = new RemoteSessionViewModel(row, _directory, _access, Permissions, NewActions());
            View = new RemoteSessionView { DataContext = Vm };
            Window = new Window { Content = View, Width = 900, Height = 700 };
            Window.Show();
            Settle();
        }

        public void Settle() {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SettleUntilAsync(Func<bool> condition, string what) {
            await WaitUntilAsync(() => { Settle(); return condition(); }, what: what);
            Settle();
        }

        public void Dispose() {
            Window.Close();
            _access.Dispose();
            Permissions.Dispose();
            _directory.Dispose();
        }
    }

    [Test]
    public async Task An_acp_question_renders_through_the_shared_card_template() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");

            var empty = host.View.FindControl<TextBlock>("NoCardsNote")!;
            await Assert.That(empty.IsVisible).IsTrue();

            var card = PendingPermissionRequest.FromServer(
                new ServerElicitationRequest("s1", "q1", "Pick a branch",
                    [new() { OptionId = "a", Label = "main" }, new() { OptionId = "b", Label = "next" }], false),
                DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            host.Permissions.Add(card);
            await host.SettleUntilAsync(() => host.Vm.Cards.HasPendingCards, "the card");

            await Assert.That(host.View.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "AcpQuestionCard")).IsTrue();
            var labels = host.View.GetVisualDescendants().OfType<Button>()
                .Where(b => b is not ToggleButton && b.Classes.Contains("acpOption"))
                .Select(b => b.Content as string ?? "")
                .ToList();
            await Assert.That(labels).IsEquivalentTo(new[] { "main", "next" });
            await Assert.That(empty.IsVisible).IsFalse();

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    /// Denied is a stated verdict, not an empty pane: the cards go away and the banner says why.
    [Test]
    public async Task A_denied_session_replaces_the_cards_with_the_access_note() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");

            var banner = host.View.FindControl<Border>("AccessBanner")!;
            var cards = host.View.FindControl<Border>("NeedsYouPane")!;
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(cards.IsVisible).IsTrue();

            host.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            host.Lane.SessionAccessChangedSubject.OnNext("s1");
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Denied, "denied");

            await Assert.That(cards.IsVisible).IsFalse();
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(host.View.FindControl<TextBlock>("AccessNoteText")!.Text)
                .IsEqualTo("You no longer have access to this session");

            await host.Vm.TeardownAsync();
            return true;
        });
    }
}
