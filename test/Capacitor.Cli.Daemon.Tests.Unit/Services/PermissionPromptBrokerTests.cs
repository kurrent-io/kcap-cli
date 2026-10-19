using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionPromptBrokerTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static PermissionPendingDto Dto(string id = "r1", string agent = "a1", string? toolUseId = null, string? input = null) =>
        new(id, agent, "s1", "claude", "Bash", input is null ? null : Json(input), null, false, false, DateTimeOffset.UtcNow.ToString("O"), toolUseId);

    static JsonElement Json(string json) {
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
            var subscribe = Task.Run(broker.Subscribe);
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

    /// Claude's prompt carries no tool id, so a finished call matches it by tool name and input,
    /// whatever order the input's properties arrive in. One call retires one prompt, the oldest.
    [Test]
    public async Task A_finished_call_retires_the_oldest_id_less_prompt_with_its_name_and_input() {
        var broker = new PermissionPromptBroker();
        var first  = broker.Register(Dto("r1", input: """{"command":"ls","description":"list"}"""));
        var second = broker.Register(Dto("r2", input: """{"command":"ls","description":"list"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_9", toolName: "Bash",
            toolInput: Json("""{"description":"list","command":"ls"}"""))).IsTrue();

        await Assert.That((await WaitBounded(first, "oldest withdrawn")).Source).IsEqualTo("tool_settled");
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_10", toolName: "Bash",
            toolInput: Json("""{"command":"ls","description":"list"}"""))).IsTrue();
        await Assert.That(second.IsCompleted).IsTrue();
    }

    /// A prompt allowed in the app before its call finished is owed that call's notice, so the
    /// notice does not retire an identical prompt that is still waiting; the next call does.
    [Test]
    public async Task A_call_owed_to_an_allowed_prompt_retires_nothing() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        var waiting = broker.Register(Dto("r2", input: """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(waiting.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
        await Assert.That(waiting.IsCompleted).IsTrue();
    }

    /// An allowed prompt that carried its tool id is owed that call too, so the call's notice cannot
    /// fall through to an identical id-less prompt still waiting.
    [Test]
    public async Task A_call_owed_by_id_does_not_fall_through_to_an_id_less_prompt() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", toolUseId: "toolu_1", input: """{"command":"ls"}"""));
        var waiting = broker.Register(Dto("r2", input: """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(waiting.IsCompleted).IsFalse();
    }

    /// Only a vendor whose hook reports a finished call with its name and input is owed one, so
    /// another vendor's allowed prompt is owed nothing and a later identical prompt is still retired.
    [Test]
    public async Task A_vendor_reporting_no_completions_owes_no_call() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}""") with { Vendor = "cursor" });
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}""") with { Vendor = "cursor" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
        await Assert.That(later.IsCompleted).IsTrue();
    }

    /// Codex's hook reports its finished calls as Claude's does, so a Codex prompt allowed in the
    /// app is owed its call, and that call's notice leaves an identical prompt still waiting.
    [Test]
    public async Task A_codex_allowed_prompt_owes_its_call() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}""") with { Vendor = "codex" });
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}""") with { Vendor = "codex" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "exec-1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
        await Assert.That(broker.OwedCountForTesting).IsEqualTo(0);
    }

    /// Codex's permission prompt carries the escalation's justification as a top-level
    /// <c>description</c> its finished call does not, so the two match without it.
    [Test]
    public async Task A_codex_prompt_matches_its_finished_call_without_the_description() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", input: """{"command":"ls","description":"List?"}""") with { Vendor = "codex" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "exec-1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
        await Assert.That((await WaitBounded(held, "the matching call withdrew it")).Source).IsEqualTo(PermissionSettlements.SourceToolSettled);
    }

    /// A notice whose name and input were too large to send still names its call by id, so a call
    /// owed by that id is paid rather than held until the turn ends.
    [Test]
    public async Task A_notice_without_name_or_input_still_pays_the_call_owed_by_its_id() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", toolUseId: "toolu_1", input: """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1")).IsFalse();
        await Assert.That(broker.OwedCountForTesting).IsEqualTo(0);
    }

    /// Even an id match honours the notice's stamp: a prompt registered after the hook sent it is
    /// not its call's, and a withdrawal would deny it.
    [Test]
    public async Task An_id_match_retires_only_a_prompt_registered_before_the_notice_was_sent() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", toolUseId: "toolu_1"));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", sentAt: DateTimeOffset.UtcNow.AddMinutes(-1))).IsFalse();
        await Assert.That(held.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", sentAt: DateTimeOffset.UtcNow.AddMinutes(1))).IsTrue();
    }

    /// A turn's end that reaches the daemon late can find the next turn's prompt already waiting,
    /// so it retires only what was registered before the hook sent it.
    [Test]
    public async Task A_turn_end_withdraws_only_prompts_registered_before_it_was_sent() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", input: """{"command":"ls"}"""));

        await Assert.That(broker.WithdrawTurn("a1", "s1", null, DateTimeOffset.UtcNow.AddMinutes(-1))).IsEqualTo(0);
        await Assert.That(held.IsCompleted).IsFalse();
        await Assert.That(broker.WithdrawTurn("a1", "s1", null, DateTimeOffset.UtcNow.AddMinutes(1))).IsEqualTo(1);
    }

    /// A call the next turn is owed outlives a late turn end, or its notice would retire an
    /// identical prompt still waiting.
    [Test]
    public async Task A_turn_end_keeps_the_calls_owed_after_it_was_sent() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        broker.WithdrawTurn("a1", "s1", null, DateTimeOffset.UtcNow.AddMinutes(-1));
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
    }

    /// A prompt whose input was over the cap is held without it, so its call cannot be told from an
    /// identical prompt's; matching by name and input stops for the scope until the turn ends.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_prompt_held_without_its_input_stops_name_and_input_matching_until_the_turn_ends(bool allowed) {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1") with { Vendor = "codex" });
        if (allowed) await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        var other = broker.Register(Dto("r2", input: """{"command":"ls"}""") with { Vendor = "codex" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "exec-1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(other.IsCompleted).IsFalse();

        broker.WithdrawTurn("a1", "s1", null, DateTimeOffset.UtcNow.AddMinutes(1));
        var next = broker.Register(Dto("r3", input: """{"command":"ls"}""") with { Vendor = "codex" });
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "exec-2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""),
            sentAt: DateTimeOffset.UtcNow.AddMinutes(2))).IsTrue();
        await Assert.That(next.IsCompleted).IsTrue();
    }

    /// Codex's hook answer has no updated input, so an allow that carries one still runs the
    /// prompt's own call, and that is the call owed.
    [Test]
    public async Task A_codex_allow_with_updated_input_owes_the_prompts_own_call() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}""") with { Vendor = "codex" });
        await Assert.That(broker.TrySettle("r1", new PermissionDecision("allow", null, Json("""{"command":"rm -rf x"}""")), "allow", "app")).IsTrue();
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}""") with { Vendor = "codex" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "exec-1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
    }

    /// Only Codex's shell prompt adds the justification; another tool's <c>description</c> is its
    /// own argument, so a call that differs in it is another call.
    [Test]
    public async Task A_codex_tools_own_description_still_tells_its_calls_apart() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", input: """{"title":"t","description":"first"}""") with { Vendor = "codex", ToolName = "mcp__notes__add" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "call_1", toolName: "mcp__notes__add",
            toolInput: Json("""{"title":"t","description":"second"}"""))).IsFalse();
        await Assert.That(held.IsCompleted).IsFalse();
    }

    /// Only the description is set aside: a Codex call whose other input differs is another call.
    [Test]
    public async Task A_codex_prompt_is_not_matched_by_a_call_with_other_input() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", input: """{"command":"ls","description":"List?"}""") with { Vendor = "codex" });

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "exec-1", toolName: "Bash", toolInput: Json("""{"command":"ls -a"}"""))).IsFalse();
        await Assert.That(held.IsCompleted).IsFalse();
    }

    /// Claude's finished call carries the description its prompt did, so a Claude prompt still
    /// needs the whole input.
    [Test]
    public async Task A_claude_prompt_still_needs_its_description_to_match() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", input: """{"command":"ls","description":"List"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(held.IsCompleted).IsFalse();
    }

    /// A prompt registered after the hook sent its notice is a later call's, whatever its name and
    /// input, so a notice that arrives late cannot retire it.
    [Test]
    public async Task A_notice_retires_only_a_prompt_registered_before_it_was_sent() {
        var broker = new PermissionPromptBroker();
        var held = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        var input = Json("""{"command":"ls"}""");

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: input,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(-1))).IsFalse();
        await Assert.That(held.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: input,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(1))).IsTrue();
    }

    /// A call the agent's policy allowed raises no prompt, but is owed its notice all the same, so
    /// that notice does not retire an identical prompt still waiting for the user.
    [Test]
    public async Task A_policy_allowed_call_is_owed_its_notice() {
        var broker = new PermissionPromptBroker();
        var waiting = broker.Register(Dto("waiting", input: """{"command":"ls"}"""));
        broker.OweCall(Dto("policy", input: """{"command":"ls"}"""), subagentId: null);

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_p", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(waiting.IsCompleted).IsFalse();
    }

    /// An agent that has exited is owed nothing: its id is never reused, so a record for it could
    /// only sit there.
    [Test]
    public async Task An_exited_agent_is_owed_nothing() {
        var broker = new PermissionPromptBroker();
        broker.WithdrawForAgent("a2");
        broker.OweCall(Dto("late", agent: "a2", input: """{"command":"ls"}"""), subagentId: null);
        var other = broker.Register(Dto("other", agent: "a1", input: """{"command":"ls"}"""));

        await Assert.That(broker.OwedCountForTesting).IsEqualTo(0);
        await Assert.That(other.IsCompleted).IsFalse();
    }

    /// A late notice cannot spend a call owed to a prompt registered after it was sent, so that
    /// prompt's own notice is still the one that consumes it.
    [Test]
    public async Task A_late_notice_does_not_spend_a_later_prompts_owed_call() {
        var broker = new PermissionPromptBroker();
        var input = Json("""{"command":"ls"}""");
        _ = broker.Register(Dto("allowed", input: """{"command":"ls"}"""));
        broker.TrySettle("allowed", Allow, "allow", "app");
        var waiting = broker.Register(Dto("waiting", input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_old", toolName: "Bash", toolInput: input,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(-1))).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_own", toolName: "Bash", toolInput: input,
            sentAt: DateTimeOffset.UtcNow.AddMinutes(1))).IsFalse();
        await Assert.That(waiting.IsCompleted).IsFalse();
    }

    /// A turn that outgrows the bound on what it is owed stops matching by name and input until it
    /// ends, rather than forgetting an owed call and retiring the wrong prompt.
    [Test]
    public async Task A_turn_past_its_bound_matches_nothing_by_name_and_input() {
        var broker = new PermissionPromptBroker();
        for (var i = 0; i <= 64; i++) {
            _ = broker.Register(Dto($"r{i}", input: $$"""{"command":"c{{i}}"}"""));
            broker.TrySettle($"r{i}", Allow, "allow", "app");
        }
        var waiting = broker.Register(Dto("w", input: """{"command":"c64"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_x", toolName: "Bash", toolInput: Json("""{"command":"c64"}"""))).IsFalse();
        await Assert.That(waiting.IsCompleted).IsFalse();
    }

    /// A finished turn has run all of its own calls, and a prompt it retires was either denied in
    /// the terminal or had its call reported before the next turn's prompts exist, so the turn's
    /// end leaves nothing owed that would keep a later prompt from being retired.
    [Test]
    public async Task A_finished_turn_owes_no_call() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        broker.TrySettle("r1", Allow, "allow", "app");
        _ = broker.Register(Dto("r-pending", input: """{"command":"ls"}"""));
        broker.WithdrawTurn("a1", "s1", subagentId: null);
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}"""));

        await Assert.That(broker.OwedCountForTesting).IsEqualTo(0);
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
        await Assert.That(later.IsCompleted).IsTrue();
    }

    /// A deny settled here does not prove the call never ran: the terminal can have allowed the
    /// same prompt first, with the card still showing. So the call is owed its notice like an
    /// allowed prompt's, or that notice would retire an identical prompt still waiting. The cost
    /// of a notice that never comes is one card left until the turn's end.
    [Test]
    public async Task A_prompt_denied_here_is_still_owed_the_call_the_terminal_may_have_started() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        broker.TrySettle("r1", PermissionSettlements.DenyDecision, "deny", "app");
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
        await Assert.That(later.IsCompleted).IsTrue();
    }

    /// The app withdraws a prompt as soon as the transcript shows its call's result, which can be
    /// before the call's own hook has reported; that report is still owed.
    [Test]
    public async Task A_withdrawal_on_transcript_evidence_is_still_owed_the_call() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        broker.TrySettle("r1", PermissionSettlements.DenyDecision, "withdrawn", "tool_settled");
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
    }

    /// An input the answer rewrote applies only when that answer is the one that ran the call: a
    /// call the terminal allowed ran with the prompt's own input, which is what its notice carries,
    /// whatever a deny settled here attached.
    [Test]
    public async Task A_denied_prompt_is_owed_the_call_under_the_prompts_own_input() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));
        broker.TrySettle("r1", new PermissionDecision("deny", null, Json("""{"command":"rm -rf x"}""")), "deny", "app");
        var later = broker.Register(Dto("r2", input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsFalse();
        await Assert.That(later.IsCompleted).IsFalse();
        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_2", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();
    }

    /// Only the call's own report settles without owing one: the report is the call's completion.
    [Test]
    public async Task A_withdrawal_by_the_calls_own_report_owes_nothing() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1", input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_1", toolName: "Bash", toolInput: Json("""{"command":"ls"}"""))).IsTrue();

        await Assert.That(broker.OwedCountForTesting).IsEqualTo(0);
    }

    /// A name-and-input match never reaches past what makes the call the prompt's own: another
    /// input, another tool, another subagent, or a prompt that carries a tool id of its own.
    [Test]
    [Arguments("Bash", """{"command":"rm"}""", null,    null)]
    [Arguments("Read", """{"command":"ls"}""", null,    null)]
    [Arguments("Bash", """{"command":"ls"}""", "sub-1", null)]
    [Arguments("Bash", """{"command":"ls"}""", null,    "toolu_1")]
    public async Task A_name_and_input_match_spares_a_prompt_that_is_not_the_calls(
            string toolName, string input, string? subagentId, string? promptToolUseId) {
        var broker = new PermissionPromptBroker();
        var held   = broker.Register(Dto("r1", toolUseId: promptToolUseId, input: """{"command":"ls"}"""));

        await Assert.That(broker.TryWithdrawTool("a1", "s1", "toolu_9", subagentId, toolName, Json(input))).IsFalse();
        await Assert.That(held.IsCompleted).IsFalse();
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
