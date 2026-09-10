using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;
using AcpInteractionOption = Capacitor.Remote.Models.AcpInteractionOption;

namespace Capacitor.App.Tests.Unit;

public class PermissionServiceTests {
    static PermissionPendingDto Dto(string id = "r1", string agent = "a1", string? serverRequestId = null) =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, "2026-08-28T10:00:00.0000000+00:00", null, serverRequestId);

    static PendingPermissionRequest ServerPermission(string id = "srv-1", string session = "s1", IReadOnlyList<AcpInteractionOption>? options = null) =>
        PendingPermissionRequest.FromServer(new ServerPermissionRequest(session, id, "Bash", null, options), "claude", DateTimeOffset.UtcNow);

    sealed class FakePermissionStream {
        readonly Channel<PermissionStreamEvent?> _channel = Channel.CreateUnbounded<PermissionStreamEvent?>();
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<PermissionStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref _attempts);
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                if (evt is null) yield break;
                yield return evt;
            }
        }

        public void EmitSubscribed() => _channel.Writer.TryWrite(new PermissionStreamEvent.Subscribed());
        public void EmitPending(PermissionPendingDto dto) => _channel.Writer.TryWrite(new PermissionStreamEvent.Pending(dto));
        public void EmitResolved(string id, string source) => _channel.Writer.TryWrite(new PermissionStreamEvent.Resolved(new PermissionResolvedDto(id, "allow", source)));
    }

    sealed class Harness : IDisposable {
        public readonly FakeDaemonClientService Daemon = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakePermissionStream Stream = new();
        public readonly List<(string SessionId, string RequestId, PermissionResponsePayload Payload)> Responses = [];
        public ServerRespondOutcome NextRespond = new(ServerRespondKind.Applied);
        public readonly BehaviorSubject<IReadOnlyDictionary<string, string>> SessionAgents = new(new Dictionary<string, string>());
        public readonly PermissionService Service;
        public readonly IObservableCache<PendingPermissionRequest, string> View;
        public IReadOnlySet<string> Agents = new HashSet<string>();
        public int Count;

        public Harness() {
            Service = new PermissionService(
                Daemon, Ops, Stream.RunAsync, new FakeTimeProvider(), CancellationToken.None,
                (sid, rid, payload, _) => { Responses.Add((sid, rid, payload)); return Task.FromResult(NextRespond); },
                SessionAgents);
            View = Service.Pending.AsObservableCache();
            Service.AgentsWithPending.Subscribe(s => Agents = s);
            Service.PendingCount.Subscribe(c => Count = c);
        }

        public void Connect(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));

        public async Task StartAsync() {
            Connect("consent/1", "permission/1");
            await WaitUntilAsync(() => Stream.Attempts == 1, what: "the subscribe attempt");
            Stream.EmitSubscribed();
        }

        /// Drops the daemon and reconnects it, which is the deterministic way to reach a second
        /// subscribe attempt: the loop's own retry gap is armed on a FakeTimeProvider nothing here
        /// advances.
        public async Task ResubscribeAsync() {
            var attempts = Stream.Attempts;
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
            Connect("consent/1", "permission/1");
            await WaitUntilAsync(() => Stream.Attempts == attempts + 1, what: "resubscribe");
        }

        public async Task<PendingPermissionRequest> EmitAsync(PermissionPendingDto dto) {
            var key = PendingPermissionRequest.KeyFor(PermissionLane.Local, dto.RequestId);
            Stream.EmitPending(dto);
            await WaitUntilAsync(() => View.Lookup(key).HasValue, what: $"pending {dto.RequestId} cached");
            return View.Lookup(key).Value;
        }

        public void Dispose() { Service.Dispose(); View.Dispose(); SessionAgents.Dispose(); }
    }

    [Test]
    public async Task Subscribes_only_with_the_permission_capability_and_clears_on_a_down_level_daemon() {
        using var h = new Harness();
        h.Connect("consent/1");
        await Task.Delay(50);
        await Assert.That(h.Stream.Attempts).IsEqualTo(0);

        await h.StartAsync();
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("consent/1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared on a down-level daemon");
    }

    [Test]
    public async Task Resolved_push_from_the_server_clears_entry_agent_set_and_count_together() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1", "a1"));
        await WaitUntilAsync(() => h.Agents.Contains("a1") && h.Count == 1, what: "derivations lit");

        h.Stream.EmitResolved("r1", "server");
        await WaitUntilAsync(() => h.View.Count == 0 && !h.Agents.Contains("a1") && h.Count == 0, what: "every derivation cleared");
    }

    [Test]
    public async Task A_replayed_ghost_of_a_resolved_request_is_dropped() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Stream.EmitResolved("r1", "app");
        await WaitUntilAsync(() => h.View.Count == 0, what: "removed");
        h.Stream.EmitPending(Dto("r1"));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_outcomes_and_the_always_allow_payload() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("r1"));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.ResolveAsync(entry, PermissionAnswer.AllowAlways, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        await Assert.That(h.Ops.PermissionResolvePayloads[0].Decision).IsEqualTo("allow");
        await Assert.That(h.Ops.PermissionResolvePayloads[0].ApplyPermissions!.Value.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto("r2"));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.ResolveAsync(second, PermissionAnswer.Deny, CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto("r3"));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.ResolveAsync(third, PermissionAnswer.Allow, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(failed.Error).IsEqualTo("daemon_unreachable");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Subscribed_clears_at_the_boundary_and_disconnect_retains() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("permission/1");
        await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "resubscribe");
        await Assert.That(h.View.Count).IsEqualTo(1);
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared at Subscribed");
    }

    static PermissionPendingDto PendingDto(string id, string agent, string vendor, string toolName, string? toolInputJson, bool omitted = false) {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PermissionPendingDto(id, agent, "s1", vendor, toolName, input, null, omitted, false, "2026-08-28T10:00:00.0000000+00:00");
    }

    const string QuestionInput = """{"questions":[{"question":"Pick","options":[{"label":"A"},{"label":"B"}]}]}""";

    [Test]
    public async Task Classification_requires_claude_the_tool_name_present_input_and_a_parse() {
        using var h = new Harness();
        await h.StartAsync();
        var yes = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Assert.That(yes.Questions).IsNotNull();
        var codex = await h.EmitAsync(PendingDto("q2", "a1", "codex", ClaudeElicitation.ToolName, QuestionInput));
        var wrongTool = await h.EmitAsync(PendingDto("q3", "a1", "claude", "Bash", QuestionInput));
        var omitted = await h.EmitAsync(PendingDto("q4", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput, omitted: true));
        var nullInput = await h.EmitAsync(PendingDto("q5", "a1", "claude", ClaudeElicitation.ToolName, null));
        var unparseable = await h.EmitAsync(PendingDto("q6", "a1", "claude", ClaudeElicitation.ToolName, """{"questions":[]}"""));
        foreach (var entry in new[] { codex, wrongTool, omitted, nullInput, unparseable })
            await Assert.That(entry.Questions).IsNull();
    }

    [Test]
    public async Task Answer_sends_allow_with_updated_input_and_concludes_on_either_ack() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.AnswerAsync(entry, [new ElicitationAnswer("Pick", ["B"], null)], CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        var payload = h.Ops.PermissionResolvePayloads[0];
        await Assert.That(payload.Decision).IsEqualTo("allow");
        await Assert.That(payload.ApplyPermissions).IsNull();
        await Assert.That(payload.UpdatedInput!.Value.Prop("answers")!.Value.Str("Pick")).IsEqualTo("B");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(PendingDto("q2", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.AnswerAsync(second, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(PendingDto("q3", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.AnswerAsync(third, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(h.View.Count).IsEqualTo(1);

        // A Resolved push after the failed send clears the survivor; a ghost replay stays dropped.
        h.Stream.EmitResolved("q3", "server");
        await WaitUntilAsync(() => h.View.Count == 0, what: "push cleared the survivor");
    }

    /// A withdraw carries no answer, and an older daemon that rejects the decision still gets the
    /// entry concluded here: the tool already ran, so the card is stale whatever the ack says.
    [Test]
    public async Task Withdraw_sends_withdraw_with_no_payload_and_concludes_on_either_ack() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("r1"));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.WithdrawAsync(entry, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        var payload = h.Ops.PermissionResolvePayloads[0];
        await Assert.That(payload.Decision).IsEqualTo("withdraw");
        await Assert.That(payload.ApplyPermissions).IsNull();
        await Assert.That(payload.UpdatedInput).IsNull();
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto("r2"));
        h.Ops.QueuePermissionResolve(false, "invalid resolve payload (decision must be allow|deny)");
        var rejected = await h.Service.WithdrawAsync(second, CancellationToken.None);
        await Assert.That(rejected.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto("r3"));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.WithdrawAsync(third, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Answer_rejects_an_unclassified_target_and_a_bad_answer_set_without_sending() {
        using var h = new Harness();
        await h.StartAsync();
        var plain = await h.EmitAsync(PendingDto("p1", "a1", "claude", "Bash", """{"command":"ls"}"""));
        await Assert.That(async () => await h.Service.AnswerAsync(plain, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None))
            .Throws<ArgumentException>();

        var entry = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Assert.That(async () => await h.Service.AnswerAsync(entry, [], CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(h.Ops.PermissionResolveCalls).IsEqualTo(0);
        await Assert.That(h.View.Count).IsEqualTo(2);
    }

    [Test]
    public async Task A_resolved_push_landing_before_the_ack_ends_in_the_same_state() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));

        var gate = h.Ops.ArmPermissionResolve();
        var run = h.Service.AnswerAsync(entry, [new ElicitationAnswer("Pick", ["A"], null)], CancellationToken.None);
        h.Stream.EmitResolved("q1", "server");
        await WaitUntilAsync(() => h.View.Count == 0, what: "push evicted while the ack is in flight");
        gate.SetResult(new PermissionAckDto(false, "no pending permission request with that id"));
        var outcome = await run;
        await Assert.That(outcome.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        // The tombstoned id stays dead against a ghost replay.
        h.Stream.EmitPending(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Summary_seeds_and_stays_a_consistent_pair() {
        using var h = new Harness();
        var summaries = new List<PendingSummary>();
        using var sub = h.Service.Summary.Subscribe(summaries.Add);
        await Assert.That(summaries[0]).IsEqualTo(default(PendingSummary));

        await h.StartAsync();
        await h.EmitAsync(PendingDto("p1", "a1", "claude", "Bash", """{"command":"ls"}"""));
        await h.EmitAsync(PendingDto("q1", "a1", "claude", ClaudeElicitation.ToolName, QuestionInput));
        await WaitUntilAsync(() => summaries[^1] == new PendingSummary(1, 1, 2, 0), what: "one of each");

        h.Stream.EmitResolved("q1", "server");
        await WaitUntilAsync(() => summaries[^1] == new PendingSummary(1, 0, 1, 0), what: "question settled");
        foreach (var s in summaries) {
            await Assert.That(s.Permissions).IsGreaterThanOrEqualTo(0);
            await Assert.That(s.Questions).IsGreaterThanOrEqualTo(0);
        }
    }

    [Test]
    public async Task Local_subscribed_replaces_local_items_only() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("l1"));
        h.Service.UpsertServer(ServerPermission("srv-9"));
        await Assert.That(h.View.Count).IsEqualTo(2);

        await h.ResubscribeAsync();
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 1, what: "local item dropped on resubscribe");
        await Assert.That(h.View.Lookup("server:srv-9").HasValue).IsTrue();
    }

    [Test]
    public async Task A_correlated_local_item_keeps_its_instance_and_shadows_the_server_twin() {
        using var h = new Harness();
        await h.StartAsync();
        var local = await h.EmitAsync(Dto("l1"));
        h.Service.UpsertServer(ServerPermission("srv-1"));
        await Assert.That(h.View.Count).IsEqualTo(2);

        h.Stream.EmitPending(Dto("l1", serverRequestId: "srv-1"));
        await WaitUntilAsync(() => h.View.Count == 1, what: "twin shadowed");
        await Assert.That(ReferenceEquals(h.View.Lookup("local:l1").Value, local)).IsTrue();
        await Assert.That(local.ServerRequestId).IsEqualTo("srv-1");

        // A later server push for the claimed id stays shadowed.
        h.Service.UpsertServer(ServerPermission("srv-1"));
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Subscription_loss_resurfaces_the_shadowed_twin_with_its_own_handle() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("l1", serverRequestId: "srv-1"));
        h.Service.UpsertServer(ServerPermission("srv-1"));
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["consent/1"]));
        await WaitUntilAsync(() => h.View.Lookup("server:srv-1").HasValue, what: "twin resurfaced");
        await Assert.That(h.View.Lookup("local:l1").HasValue).IsFalse();
        var twin = h.View.Lookup("server:srv-1").Value;
        await Assert.That(twin.Lane).IsEqualTo(PermissionLane.Server);
        await Assert.That(twin.RequestId).IsEqualTo("srv-1");
    }

    [Test]
    public async Task Server_settlement_by_id_clears_the_twin_and_the_claiming_local_item() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("l1", serverRequestId: "srv-1"));
        h.Service.SettleServer("s1", "srv-1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "local claimant concluded");

        h.Stream.EmitPending(Dto("l1", serverRequestId: "srv-1"));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_session_wide_clear_retires_server_items_only_even_after_a_late_correlation() {
        using var h = new Harness();
        await h.StartAsync();
        h.Service.UpsertServer(ServerPermission("srv-1"));
        h.Service.UpsertServer(ServerPermission("srv-2", session: "s2"));
        var local = await h.EmitAsync(Dto("l1"));
        var generation = h.Service.SessionGeneration("s1");

        h.Service.SettleServer("s1", null);
        await Assert.That(h.View.Lookup("server:srv-1").HasValue).IsFalse();
        await Assert.That(h.View.Lookup("server:srv-2").HasValue).IsTrue();
        await Assert.That(h.Service.SessionGeneration("s1")).IsNotEqualTo(generation);

        h.Stream.EmitPending(Dto("l1", serverRequestId: "srv-1"));
        await WaitUntilAsync(() => local.ServerRequestId == "srv-1", what: "late correlation applied");
        await Assert.That(h.View.Lookup("local:l1").HasValue).IsTrue();
    }

    [Test]
    public async Task Reconciliation_results_apply_only_under_the_generation_they_started_with() {
        using var h = new Harness();
        await h.StartAsync();
        var generation = h.Service.SessionGeneration("s1");
        h.Service.SettleServer("s1", null);

        h.Service.ReplaceServerForSession("s1", [ServerPermission("srv-1")], generation);
        await Assert.That(h.View.Count).IsEqualTo(0);
        h.Service.ReplaceServerForSession("s1", [ServerPermission("srv-1")], h.Service.SessionGeneration("s1"));
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Server_items_answer_over_http_with_their_own_id_and_a_404_drops_the_card() {
        using var h = new Harness();
        await h.StartAsync();
        h.Service.UpsertServer(ServerPermission("srv-1"));
        var item = h.View.Lookup("server:srv-1").Value;

        var applied = await h.Service.ResolveAsync(item, PermissionAnswer.AllowAlways, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        await Assert.That(h.Responses.Single().RequestId).IsEqualTo("srv-1");
        await Assert.That(h.Responses.Single().Payload.Behavior).IsEqualTo("allow");
        await Assert.That(h.Responses.Single().Payload.ApplyPermissions).IsNotNull();
        await Assert.That(h.Ops.PermissionResolveCalls).IsEqualTo(0);
        await Assert.That(h.View.Count).IsEqualTo(0);

        h.Service.UpsertServer(ServerPermission("srv-2"));
        h.NextRespond = new(ServerRespondKind.NotPending);
        var gone = await h.Service.ResolveAsync(h.View.Lookup("server:srv-2").Value, PermissionAnswer.Deny, CancellationToken.None);
        await Assert.That(gone.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        h.Service.UpsertServer(ServerPermission("srv-3"));
        h.NextRespond = new(ServerRespondKind.Unreachable, "boom");
        var failed = await h.Service.ResolveAsync(h.View.Lookup("server:srv-3").Value, PermissionAnswer.Allow, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task An_acp_question_answers_with_option_ids_and_an_acp_permission_with_the_picked_option() {
        using var h = new Harness();
        await h.StartAsync();
        var options = new AcpInteractionOption[] {
            new() { OptionId = "a", Label = "Same", MinSelections = 1, MaxSelections = 2 },
            new() { OptionId = "b", Label = "Same", MinSelections = 1, MaxSelections = 2 },
        };
        h.Service.UpsertServer(PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", options, true), DateTimeOffset.UtcNow));
        var question = h.View.Lookup("server:q1").Value;
        await Assert.That(question.IsQuestion).IsTrue();

        await h.Service.AnswerAcpAsync(question, new AcpAnswer(["a", "b"], null), CancellationToken.None);
        var payload = h.Responses.Single().Payload;
        await Assert.That(payload.Behavior).IsEqualTo("answered");
        await Assert.That(payload.SelectedOptionIds).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(payload.SelectedOptionLabels).IsEquivalentTo(new[] { "Same", "Same" });

        h.Responses.Clear();
        h.Service.UpsertServer(ServerPermission("p1", options: [new() { OptionId = "reject", Label = "Reject", Kind = "reject_once" }]));
        await h.Service.PickOptionAsync(h.View.Lookup("server:p1").Value, "reject", CancellationToken.None);
        await Assert.That(h.Responses.Single().Payload.Behavior).IsEqualTo("deny");
        await Assert.That(h.Responses.Single().Payload.SelectedOptionId).IsEqualTo("reject");
    }

    [Test]
    public async Task Server_items_resolve_their_agent_id_from_the_session_map() {
        using var h = new Harness();
        await h.StartAsync();
        h.Service.UpsertServer(ServerPermission("srv-1", session: "s1"));
        await Assert.That(h.Agents.Count).IsEqualTo(0);

        h.SessionAgents.OnNext(new Dictionary<string, string> { ["s1"] = "agent-1" });
        await WaitUntilAsync(() => h.Agents.Contains("agent-1"), what: "agent resolved");
        await Assert.That(h.View.Lookup("server:srv-1").Value.AgentId).IsEqualTo("agent-1");
    }

    [Test]
    public async Task Local_settlement_retires_the_claimed_twin() {
        using var h = new Harness();
        await h.StartAsync();
        var local = await h.EmitAsync(Dto("l1", serverRequestId: "srv-1"));
        h.Service.UpsertServer(ServerPermission("srv-1"));

        h.Ops.QueuePermissionResolve(true);
        await h.Service.ResolveAsync(local, PermissionAnswer.Allow, CancellationToken.None);
        await Assert.That(h.View.Count).IsEqualTo(0);

        h.Service.UpsertServer(ServerPermission("srv-1"));
        await Assert.That(h.View.Count).IsEqualTo(0);
    }
}
