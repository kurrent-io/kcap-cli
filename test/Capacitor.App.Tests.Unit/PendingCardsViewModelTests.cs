using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// PendingCardsViewModel's own pipeline in isolation: the agent filter, the three-way card
/// transform, and HasPendingCards, exercised without a hosting ChatTabViewModel.
[NotInParallel(nameof(AvaloniaSession))]
public class PendingCardsViewModelTests {
    [Test]
    public async Task Cards_follow_this_agents_entries_across_both_lanes_and_pick_the_card_by_kind() {
        await RunOnUiAsync(async () => {
            var permissions = new FakePermissionService();
            using var cards = new PendingCardsViewModel("a1", permissions, Observable.Return<string?>(null));

            permissions.Add(PermissionEntries.Entry("l1", "a1"));
            permissions.Add(PermissionEntries.Question("l2", "a1"));
            var acp = PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", [], false), DateTimeOffset.UtcNow);
            acp.AgentId = "a1";
            permissions.Add(acp);
            permissions.Add(PermissionEntries.Entry("other", "a2"));

            await WaitUntilAsync(() => cards.PendingCards.Count == 3, what: "the three cards for a1");
            await Assert.That(cards.PendingCards.OfType<AcpQuestionCardViewModel>().Count()).IsEqualTo(1);
            await Assert.That(cards.HasPendingCards).IsTrue();

            permissions.Remove("l1");
            permissions.Remove("l2");
            permissions.Cache.Remove(acp.Key);

            await WaitUntilAsync(() => !cards.HasPendingCards, what: "cards cleared");
        });
    }

    [Test]
    public async Task A_refresh_keeps_the_card_instance() {
        await RunOnUiAsync(async () => {
            var permissions = new FakePermissionService();
            using var cards = new PendingCardsViewModel("a1", permissions, Observable.Return<string?>(null));
            var entry = PermissionEntries.Entry("l1", "a1");
            permissions.Add(entry);
            await WaitUntilAsync(() => cards.PendingCards.Count == 1, what: "the initial card");

            var before = cards.PendingCards.Single();
            entry.ServerRequestId = "srv-1";
            permissions.Cache.Refresh(entry);

            await Assert.That(ReferenceEquals(cards.PendingCards.Single(), before)).IsTrue();
        });
    }
}
