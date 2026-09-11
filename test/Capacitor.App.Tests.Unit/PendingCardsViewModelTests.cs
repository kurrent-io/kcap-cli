using System.Collections.Specialized;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using DynamicData;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// PendingCardsViewModel's own pipeline in isolation: the lane-and-identity filter, the three-way
/// card transform, and HasPendingCards, exercised without a hosting ChatTabViewModel.
[NotInParallel(nameof(AvaloniaSession))]
public class PendingCardsViewModelTests {
    [Test]
    public async Task Cards_follow_this_agents_entries_across_both_lanes_and_pick_the_card_by_kind() {
        await RunOnUiAsync(async () => {
            var permissions = new FakePermissionService();
            using var cards = new PendingCardsViewModel("a1", AgentOrigin.Local, Observable.Return<string?>("s1"), permissions, Observable.Return<string?>(null));

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

    /// A server item is this workspace's by session, not by agent id, and the id only resolves
    /// once the row carries it.
    [Test]
    public async Task A_server_item_belongs_to_the_workspace_holding_its_session() {
        await RunOnUiAsync(async () => {
            using var permissions = new FakePermissionService();
            var sessions = new BehaviorSubject<string?>(null);
            using var cards = new PendingCardsViewModel("a1", AgentOrigin.Remote, sessions, permissions, Observable.Return<string?>(null));

            var mine = PermissionEntries.ServerEntry("srv-mine", sessionId: "s1");
            mine.AgentId = "a1";
            var elsewhere = PermissionEntries.ServerEntry("srv-other", sessionId: "s2");
            elsewhere.AgentId = "a1";
            permissions.Add(mine);
            permissions.Add(elsewhere);

            // No session id yet, so neither item is provably this workspace's.
            await Assert.That(cards.PendingCards.Count).IsEqualTo(0);

            sessions.OnNext("s1");

            await WaitUntilAsync(() => cards.PendingCards.Count == 1, what: "the card for this session");
            await Assert.That(cards.PendingCards.Single().RequestId).IsEqualTo("srv-mine");
        });
    }

    /// The dedup fails open, so a local and a remote row can share an agent id while holding two
    /// unrelated agents. The local lane's card is answerable only through the local workspace.
    [Test]
    public async Task A_local_item_renders_only_in_the_local_workspace() {
        await RunOnUiAsync(async () => {
            using var permissions = new FakePermissionService();
            using var local = new PendingCardsViewModel("a1", AgentOrigin.Local, Observable.Return<string?>("s1"), permissions, Observable.Return<string?>(null));
            using var remote = new PendingCardsViewModel("a1", AgentOrigin.Remote, Observable.Return<string?>("s9"), permissions, Observable.Return<string?>(null));

            permissions.Add(PermissionEntries.Entry("l1", "a1"));

            await WaitUntilAsync(() => local.PendingCards.Count == 1, what: "the local card");
            await Assert.That(remote.PendingCards.Count).IsEqualTo(0);
        });
    }

    /// A session id is unique only within one server. While the local daemon is on a different
    /// one, a server-lane item carrying this id is another server's session: it belongs to the
    /// remote workspace that does hold it, and never to the local one, whose process it would
    /// answer over HTTP.
    [Test]
    public async Task A_local_workspace_admits_no_server_item_while_its_daemon_is_on_another_server() {
        await RunOnUiAsync(async () => {
            using var permissions = new FakePermissionService();
            using var onAppServer = new BehaviorSubject<bool>(false);
            using var local = new PendingCardsViewModel(
                "a1", AgentOrigin.Local, Observable.Return<string?>("s1"), permissions, Observable.Return<string?>(null), onAppServer);
            using var remote = new PendingCardsViewModel(
                "a1", AgentOrigin.Remote, Observable.Return<string?>("s1"), permissions, Observable.Return<string?>(null), onAppServer);

            var item = PermissionEntries.ServerEntry("srv-1", sessionId: "s1");
            item.AgentId = "a1";
            permissions.Add(item);

            await WaitUntilAsync(() => remote.PendingCards.Count == 1, what: "the card in the remote workspace");
            await Assert.That(local.PendingCards.Count).IsEqualTo(0);

            onAppServer.OnNext(true);

            await WaitUntilAsync(() => local.PendingCards.Count == 1, what: "the card once the daemon is on the app's server");
        });
    }

    /// Thread identity, so deliberately not under WithImmediateRxScheduler: an immediate scheduler
    /// delivers the flip on the thread that raised it and would prove nothing. The flag is a filter
    /// input, so flipping it adds and removes rows of a collection the UI is bound to.
    [Test]
    public async Task A_scope_flip_off_the_ui_thread_changes_the_cards_on_the_ui_thread() {
        var offUiThread = await DispatchAsync(async () => {
            using var permissions = new FakePermissionService();
            using var onAppServer = new BehaviorSubject<bool>(true);
            using var local = new PendingCardsViewModel(
                "a1", AgentOrigin.Local, Observable.Return<string?>("s1"), permissions,
                Observable.Return<string?>(null), onAppServer);

            var off = 0;
            ((INotifyCollectionChanged)local.PendingCards).CollectionChanged +=
                (_, _) => { if (!Dispatcher.UIThread.CheckAccess()) Interlocked.Increment(ref off); };

            var item = PermissionEntries.ServerEntry("srv-1", sessionId: "s1");
            item.AgentId = "a1";
            permissions.Add(item);
            await WaitUntilAsync(() => local.PendingCards.Count == 1, what: "the card on the app's own server");

            await Task.Run(() => onAppServer.OnNext(false));
            await WaitUntilAsync(() => local.PendingCards.Count == 0, what: "the card gone once the daemon is elsewhere");

            await Task.Run(() => onAppServer.OnNext(true));
            await WaitUntilAsync(() => local.PendingCards.Count == 1, what: "the card back");

            return Volatile.Read(ref off);
        });

        await Assert.That(offUiThread).IsEqualTo(0);
    }

    [Test]
    public async Task A_refresh_keeps_the_card_instance() {
        await RunOnUiAsync(async () => {
            var permissions = new FakePermissionService();
            using var cards = new PendingCardsViewModel("a1", AgentOrigin.Local, Observable.Return<string?>("s1"), permissions, Observable.Return<string?>(null));
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
