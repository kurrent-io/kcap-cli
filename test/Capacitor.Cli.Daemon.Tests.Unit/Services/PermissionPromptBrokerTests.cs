using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionPromptBrokerTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static PermissionPendingDto Dto(string id = "r1", string agent = "a1", string? toolUseId = null) =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, DateTimeOffset.UtcNow.ToString("O"), toolUseId);

    static PermissionDecision Allow => new("allow", null, null);

    static async Task<T> WaitBounded<T>(Task<T> task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        return await task;
    }

    [Test]
    public async Task Register_broadcasts_pending_and_settle_broadcasts_resolved_and_completes_the_task() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var settlement = broker.Register(Dto());

        var first = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(((PermissionStreamItem.Pending)first).Dto.RequestId).IsEqualTo("r1");

        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        var second = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        var resolved = ((PermissionStreamItem.Resolved)second).Dto;
        await Assert.That(resolved.Outcome).IsEqualTo("allow");
        await Assert.That(resolved.Source).IsEqualTo("app");

        var s = await WaitBounded(settlement, "the claim completes the registration");
        await Assert.That(s.Decision.Behavior).IsEqualTo("allow");
        await Assert.That(s.Source).IsEqualTo("app");
    }

    [Test]
    public async Task Second_claim_loses_and_the_task_carries_the_first() {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto());
        await Assert.That(broker.TrySettle("r1", new("deny", null, null), "deny", "server")).IsTrue();
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsFalse();
        var s = await WaitBounded(settlement, "first claim");
        await Assert.That(s.Source).IsEqualTo("server");
        await Assert.That(s.Decision.Behavior).IsEqualTo("deny");
    }

    [Test]
    public async Task Subscribe_replays_each_pending_exactly_once() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1"));
        _ = broker.Register(Dto("r2"));
        var (_, reader) = broker.Subscribe();
        var a = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        var b = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(new[] { ((PermissionStreamItem.Pending)a).Dto.RequestId, ((PermissionStreamItem.Pending)b).Dto.RequestId })
            .IsEquivalentTo(new[] { "r1", "r2" });
        await Assert.That(reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task Withdraw_settles_the_agents_entries_and_a_later_register_for_it_settles_at_once_without_broadcast() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var s1 = broker.Register(Dto("r1", "a1"));
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the Pending

        broker.WithdrawForAgent("a1");
        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.Outcome).IsEqualTo("withdrawn");
        await Assert.That(resolved.Source).IsEqualTo("agent_gone");
        await Assert.That((await WaitBounded(s1, "withdrawn")).Decision.Behavior).IsEqualTo("deny");

        var s2 = broker.Register(Dto("r2", "a1"));
        await Assert.That(s2.IsCompletedSuccessfully).IsTrue();
        await Assert.That(s2.Result.Source).IsEqualTo("agent_gone");
        await Assert.That(reader.TryRead(out _)).IsFalse(); // nothing broadcast for r2
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Settle_if_no_subscriber_is_refused_while_a_subscriber_is_registered() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto());
        var (id, _) = broker.Subscribe();
        await Assert.That(broker.TrySettleIfNoSubscriber("r1", new("deny", null, null), "deny", "no_ui")).IsFalse();
        broker.Unsubscribe(id);
        await Assert.That(broker.TrySettleIfNoSubscriber("r1", new("deny", null, null), "deny", "no_ui")).IsTrue();
    }

    /// The gate invariant: a subscriber that dials during a settlement sees either nothing or
    /// Pending then Resolved — never Pending alone. Driven from many interleavings.
    [Test]
    public async Task Subscribe_racing_settle_never_yields_pending_alone() {
        for (var round = 0; round < 200; round++) {
            var broker = new PermissionPromptBroker();
            _ = broker.Register(Dto());
            var subscribe = Task.Run(() => broker.Subscribe());
            var settle    = Task.Run(() => broker.TrySettle("r1", Allow, "allow", "app"));
            var (id, reader) = await subscribe;
            await settle;
            broker.Unsubscribe(id);

            var items = new List<PermissionStreamItem>();
            while (reader.TryRead(out var item)) items.Add(item);
            var pendings  = items.Count(i => i is PermissionStreamItem.Pending);
            var resolveds = items.Count(i => i is PermissionStreamItem.Resolved);
            await Assert.That(pendings == 0 || resolveds == 1).IsTrue().Because($"round {round}: {pendings} pending, {resolveds} resolved");
        }
    }

    [Test]
    public async Task Registering_the_same_request_id_twice_throws_and_leaves_the_first_pending() {
        var broker = new PermissionPromptBroker();
        var first = broker.Register(Dto("r1"));
        await Assert.That(() => broker.Register(Dto("r1"))).Throws<InvalidOperationException>();
        await Assert.That(first.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        await Assert.That((await WaitBounded(first, "first still settles")).Source).IsEqualTo("app");
    }

    [Test]
    public async Task Unsubscribe_completes_the_channel() {
        var broker = new PermissionPromptBroker();
        var (id, reader) = broker.Subscribe();
        broker.Unsubscribe(id);
        await reader.Completion.WaitAsync(Bounded);
        await Assert.That(broker.HasSubscriber).IsFalse();
    }

    [Test]
    public async Task Correlate_rebroadcasts_the_pending_with_the_server_id_and_replays_it_to_late_subscribers() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        _ = broker.Register(Dto());
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the first Pending

        await Assert.That(broker.TryCorrelate("r1", "srv-1")).IsTrue();
        var update = ((PermissionStreamItem.Pending)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(update.RequestId).IsEqualTo("r1");
        await Assert.That(update.ServerRequestId).IsEqualTo("srv-1");

        var (_, late) = broker.Subscribe();
        var replayed = ((PermissionStreamItem.Pending)await late.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(replayed.ServerRequestId).IsEqualTo("srv-1");
        await Assert.That(broker.PendingSnapshot().Single().ServerRequestId).IsEqualTo("srv-1");
    }

    [Test]
    public async Task A_settled_tool_withdraws_only_the_request_carrying_its_id() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var s1 = broker.Register(Dto("r1", "a1", toolUseId: "toolu_1"));
        var s2 = broker.Register(Dto("r2", "a1", toolUseId: "toolu_2"));
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token);

        await Assert.That(broker.TryWithdrawTool("a1", "toolu_1")).IsTrue();

        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.RequestId).IsEqualTo("r1");
        await Assert.That(resolved.Outcome).IsEqualTo("withdrawn");
        await Assert.That(resolved.Source).IsEqualTo("tool_settled");
        await Assert.That((await WaitBounded(s1, "withdrawn")).Decision.Behavior).IsEqualTo("deny");
        await Assert.That(s2.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r2");
        await Assert.That(broker.TryWithdrawTool("a1", "toolu_1")).IsFalse();
    }

    /// The shared token admits every hosted agent, so a tool id is only honoured for the agent
    /// whose prompt carries it.
    [Test]
    public async Task A_settled_tool_of_another_agent_withdraws_nothing() {
        var broker = new PermissionPromptBroker();
        var s1 = broker.Register(Dto("r1", "a1", toolUseId: "toolu_1"));

        await Assert.That(broker.TryWithdrawTool("a2", "toolu_1")).IsFalse();

        await Assert.That(s1.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
    }

    /// A background subagent's prompt outlives the parent's turn, so the turn-end backstop must
    /// not answer it on the user's behalf.
    [Test]
    public async Task A_turn_end_withdraws_the_main_turns_requests_and_spares_a_subagents() {
        var broker = new PermissionPromptBroker();
        var main = broker.Register(Dto("r1", "a1"));
        var sub  = broker.Register(Dto("r2", "a1"), subagentId: "sub-1");

        await Assert.That(broker.WithdrawTurn("a1", subagentId: null)).IsEqualTo(1);

        var settled = await WaitBounded(main, "main turn withdrawn");
        await Assert.That(settled.Outcome).IsEqualTo("withdrawn");
        await Assert.That(settled.Source).IsEqualTo("tool_settled");
        await Assert.That(sub.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r2");
    }

    [Test]
    public async Task A_subagent_stop_withdraws_only_that_subagents_requests() {
        var broker = new PermissionPromptBroker();
        var main  = broker.Register(Dto("r1", "a1"));
        var sub1  = broker.Register(Dto("r2", "a1"), subagentId: "sub-1");
        var sub2  = broker.Register(Dto("r3", "a1"), subagentId: "sub-2");
        var other = broker.Register(Dto("r4", "a2"), subagentId: "sub-1");

        await Assert.That(broker.WithdrawTurn("a1", subagentId: "sub-1")).IsEqualTo(1);

        await Assert.That((await WaitBounded(sub1, "subagent withdrawn")).Outcome).IsEqualTo("withdrawn");
        await Assert.That(main.IsCompleted).IsFalse();
        await Assert.That(sub2.IsCompleted).IsFalse();
        await Assert.That(other.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(3);
    }

    /// Unlike an agent's exit, a turn's end says nothing about the next turn.
    [Test]
    public async Task A_turn_end_leaves_the_agents_next_request_pending() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", "a1"));
        await Assert.That(broker.WithdrawTurn("a1", subagentId: null)).IsEqualTo(1);

        var next = broker.Register(Dto("r2", "a1"));

        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r2");
    }

    [Test]
    public async Task Correlate_after_settlement_is_a_no_op_and_broadcasts_nothing() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        _ = broker.Register(Dto());
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the Resolved

        await Assert.That(broker.TryCorrelate("r1", "srv-1")).IsFalse();
        await Assert.That(reader.TryRead(out _)).IsFalse();
    }
}
