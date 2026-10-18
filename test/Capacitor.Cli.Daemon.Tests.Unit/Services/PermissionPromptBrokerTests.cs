using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionPromptBrokerTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static PermissionPendingDto Dto(string id = "r1", string agent = "a1", string? toolUseId = null) =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, DateTimeOffset.UtcNow.ToString("O"), toolUseId);

    /// A prompt as Claude's permission hook raises it: no tool-use id, the tool named and its input carried.
    static PermissionPendingDto Prompt(string id, string toolName, string? inputJson, string? requestedAt = null, bool omitted = false) =>
        new(id, "a1", "s1", "claude", toolName, inputJson is null ? null : Input(inputJson), null, omitted, false,
            requestedAt ?? DateTimeOffset.UtcNow.ToString("O"));

    static JsonElement Input(string json) {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

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

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1")).IsTrue();

        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.RequestId).IsEqualTo("r1");
        await Assert.That(resolved.Outcome).IsEqualTo("withdrawn");
        await Assert.That(resolved.Source).IsEqualTo("tool_settled");
        await Assert.That((await WaitBounded(s1, "withdrawn")).Decision.Behavior).IsEqualTo("deny");
        await Assert.That(s2.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r2");
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1")).IsFalse();
    }

    /// The shared token admits every hosted agent, so a tool id is only honoured for the agent
    /// whose prompt carries it.
    [Test]
    public async Task A_settled_tool_of_another_agent_withdraws_nothing() {
        var broker = new PermissionPromptBroker();
        var s1 = broker.Register(Dto("r1", "a1", toolUseId: "toolu_1"));

        await Assert.That(broker.TryWithdrawTool("a2", "s1", "toolu_1")).IsFalse();

        await Assert.That(s1.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
    }

    /// A notice names the session its hook ran in, and a prompt registered under another session
    /// is not its to retire, whichever agent the caller named.
    [Test]
    public async Task A_notice_for_another_session_withdraws_nothing() {
        var broker = new PermissionPromptBroker();
        var s1 = broker.Register(Dto("r1", "a1", toolUseId: "toolu_1"));

        await Assert.That(broker.TryWithdrawTool("a1", "s2", "toolu_1")).IsFalse();
        await Assert.That(broker.WithdrawTurn("a1", "s2", subagentId: null)).IsEqualTo(0);

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

        await Assert.That(broker.WithdrawTurn("a1", "s1", subagentId: null)).IsEqualTo(1);

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

        await Assert.That(broker.WithdrawTurn("a1", "s1", subagentId: "sub-1")).IsEqualTo(1);

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
        await Assert.That(broker.WithdrawTurn("a1", "s1", subagentId: null)).IsEqualTo(1);

        var next = broker.Register(Dto("r2", "a1"));

        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r2");
    }

    /// Claude's permission hook names the tool and carries its input but no tool-use id, so the
    /// settled tool's name and input are what identify the prompt it answered. Key order is not
    /// identity; a different input or a different tool with the same input is another call.
    [Test]
    public async Task A_settled_tool_withdraws_the_prompt_with_its_name_and_input_when_the_prompt_has_no_id() {
        var broker = new PermissionPromptBroker();
        var ls    = broker.Register(Prompt("r1", "Bash", """{"command":"ls","description":"list"}"""));
        var rm    = broker.Register(Prompt("r2", "Bash", """{"command":"rm -rf x"}"""));
        var read  = broker.Register(Prompt("r3", "Read", """{"command":"ls","description":"list"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"description":"list","command":"ls"}"""))).IsTrue();

        var settled = await WaitBounded(ls, "the matching prompt withdrawn");
        await Assert.That(settled.Outcome).IsEqualTo("withdrawn");
        await Assert.That(settled.Source).IsEqualTo("tool_settled");
        await Assert.That(settled.Decision.Behavior).IsEqualTo("deny");
        await Assert.That(rm.IsCompleted).IsFalse();
        await Assert.That(read.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Select(p => p.RequestId).Order()).IsEquivalentTo(["r2", "r3"]);
    }

    /// Two identical pending calls are answered in the order they were raised, so each settled
    /// tool retires the oldest survivor.
    [Test]
    public async Task Identical_prompts_are_withdrawn_oldest_first() {
        var broker = new PermissionPromptBroker();
        var newer = broker.Register(Prompt("r-new", "Bash", """{"command":"ls"}""", requestedAt: "2026-10-18T10:00:05.0000000+00:00"));
        var older = broker.Register(Prompt("r-old", "Bash", """{"command":"ls"}""", requestedAt: "2026-10-18T10:00:00.0000000+00:00"));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(older, "the older prompt first")).Outcome).IsEqualTo("withdrawn");
        await Assert.That(newer.IsCompleted).IsFalse();

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(newer, "the newer prompt second")).Outcome).IsEqualTo("withdrawn");
        await Assert.That(broker.PendingSnapshot()).IsEmpty();
    }

    /// A prompt that does carry a tool-use id is matched by that id alone: the same name and input
    /// under a different id is another call.
    [Test]
    public async Task A_prompt_with_its_own_id_is_not_matched_by_name_and_input() {
        var broker = new PermissionPromptBroker();
        var dto = Prompt("r1", "Bash", """{"command":"ls"}""") with { ToolUseId = "toolu_other" };
        var s1 = broker.Register(dto);

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsFalse();

        await Assert.That(s1.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(1);
    }

    /// An input the wire dropped as oversize cannot prove a match, and a notice without an input
    /// has only its id to offer; both leave the prompt to the turn's end.
    [Test]
    public async Task A_prompt_without_a_comparable_input_is_not_matched() {
        var broker = new PermissionPromptBroker();
        var omitted = broker.Register(Prompt("r1", "Bash", null, omitted: true));
        var plain   = broker.Register(Prompt("r2", "Bash", """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(plain, "the comparable prompt")).Outcome).IsEqualTo("withdrawn");
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", "Bash", Input("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_3")).IsFalse();

        await Assert.That(omitted.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r1");
    }

    /// A subagent's hook runs with the parent's environment, so its finished tool is scoped by
    /// its id: the parent's identical pending prompt is not the one it answered, and the reverse
    /// holds for the parent's own completion.
    [Test]
    public async Task A_settled_tool_matched_by_name_and_input_stays_within_its_subagent_scope() {
        var broker = new PermissionPromptBroker();
        var parent = broker.Register(Prompt("r-parent", "Bash", """{"command":"ls"}"""));
        var sub    = broker.Register(Prompt("r-sub", "Bash", """{"command":"ls"}"""), subagentId: "sub-1");

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""), subagentId: "sub-1")).IsTrue();
        await Assert.That((await WaitBounded(sub, "the subagent's own prompt")).Outcome).IsEqualTo("withdrawn");
        await Assert.That(parent.IsCompleted).IsFalse();

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", "Bash", Input("""{"command":"ls"}"""), subagentId: "sub-2")).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_3", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(parent, "the parent's prompt")).Outcome).IsEqualTo("withdrawn");
    }

    /// A prompt allowed here is still owed its tool's completion notice, and that notice must not
    /// be read as the answer to a newer identical prompt nobody has answered: hooks run
    /// asynchronously, so the completion can land after the next identical call is raised.
    [Test]
    public async Task An_allowed_prompts_completion_leaves_a_newer_identical_prompt_pending() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Prompt("r-allowed", "Bash", """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r-allowed", Allow, "allow", "app")).IsTrue();
        var next = broker.Register(Prompt("r-next", "Bash", """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(next.IsCompleted).IsFalse();

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(next, "the second completion is the terminal answer")).Outcome).IsEqualTo("withdrawn");
    }

    /// A denied call never runs, so it owes no completion; the next identical call's completion is
    /// a terminal answer to its own prompt.
    [Test]
    public async Task A_denied_prompt_is_owed_no_completion() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Prompt("r-denied", "Bash", """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r-denied", PermissionSettlements.DenyDecision, "deny", "app")).IsTrue();
        var next = broker.Register(Prompt("r-next", "Bash", """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(next, "the pending prompt")).Outcome).IsEqualTo("withdrawn");
    }

    /// A call the launch policy allowed raises no prompt but still runs and still reports, so the
    /// bridge registers the completion it is owed the same way an allowed prompt does.
    [Test]
    public async Task A_policy_allowed_call_is_owed_a_completion() {
        var broker = new PermissionPromptBroker();
        broker.ExpectCompletion("a1", "s1", subagentId: null, "Bash", Input("""{"command":"ls"}"""));
        var next = broker.Register(Prompt("r-next", "Bash", """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
    }

    /// A turn's end settles its accounts: a completion the turn never reported must not absorb the
    /// next turn's terminal answer.
    [Test]
    public async Task A_turn_end_forgets_the_completions_that_turn_was_owed() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Prompt("r-allowed", "Bash", """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r-allowed", Allow, "allow", "app")).IsTrue();
        await Assert.That(broker.WithdrawTurn("a1", "s1", subagentId: null)).IsEqualTo(0);
        var next = broker.Register(Prompt("r-next", "Bash", """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", "Bash", Input("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(next, "the next turn's prompt")).Outcome).IsEqualTo("withdrawn");
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
