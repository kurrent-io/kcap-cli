using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Harness.Muse;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Muse;

/// <summary>
/// Drives <see cref="MuseMspHostedAgentRuntime"/> against a scripted MSP host. Time is a
/// <see cref="FakeTimeProvider"/>, so no deadline or grace period elapses unless a test advances it;
/// "the pump has consumed line N" is proven with <see cref="MuseScriptedHost.SyncAsync"/>, never a sleep.
/// </summary>
public class MuseMspHostedAgentRuntimeTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    const string MintedId = "01a136a0-0000-7000-8000-000000000001";
    const string ApprovalId = "01a13695-6ad8-7313-9d2b-57f03aebbb24";
    const string SearchTool = "mcp__kcap_sessions__search_sessions";
    const string ShellApprovalId = "01a13696-a953-7bd0-bedc-c5bf1ef3b309";

    static MuseLaunchPlan Plan(
            bool        isReviewer = false,
            JsonObject? mcpServers = null,
            string?     resume     = null,
            IReadOnlySet<string>? admitted = null) =>
        new(isReviewer,
            isReviewer ? ":read-only" : ":ask-me",
            "onRequest",
            mcpServers,
            resume,
            admitted ?? FrozenSet<string>.Empty,
            MintedId);

    sealed class Rig : IAsyncDisposable {
        public required MuseScriptedHost Host { get; init; }
        public required MuseMspHostedAgentRuntime Runtime { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public FakeMuseMspProcess Process => Host.Process;

        public ValueTask DisposeAsync() => Runtime.DisposeAsync();
    }

    static Rig NewRig(
            MuseLaunchPlan? plan = null,
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>? interact = null,
            Action<MuseScriptedHost>? configure = null,
            TimeSpan? readyDeadline = null,
            string? requestedModel = "muse-spark-1.3",
            ILogger? logger = null) {
        var host = new MuseScriptedHost();
        configure?.Invoke(host);

        var time = new FakeTimeProvider();
        var rt = new MuseMspHostedAgentRuntime(
            host.Process,
            logger ?? NullLogger.Instance,
            "agent-1",
            "/workspace/project",
            requestedModel,
            time,
            interact ?? ((_, _) => throw new InvalidOperationException("no interaction expected")),
            plan ?? Plan(),
            "9.9.9",
            readyDeadline);

        return new Rig { Host = host, Runtime = rt, Time = time };
    }

    static async Task<Rig> ReadyRig(
            MuseLaunchPlan? plan = null,
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>? interact = null,
            Action<MuseScriptedHost>? configure = null,
            ILogger? logger = null) {
        var rig = NewRig(plan, interact, configure, logger: logger);
        await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        return rig;
    }

    static JsonObject Params(JsonObject request) => request["params"]!.AsObject();

    // ---- Handshake ----

    [Test]
    public async Task Handshake_refuses_schema_version_other_than_one() {
        await using var rig = NewRig(configure: h =>
            h.Process.AutoInitializeResult = MuseMspWire.InitializeResult.Replace("\"version\":1", "\"version\":2"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("muse_schema_version_unsupported");
        await Assert.That(ex.Message).Contains("host schema version 2");
        await Assert.That(rig.Host.WritesOf("session/start")).IsEmpty();
    }

    [Test]
    public async Task Handshake_records_the_host_and_sends_initialized_before_the_session() {
        await using var rig = await ReadyRig();

        var handshake = rig.Runtime.Handshake!;
        await Assert.That(handshake.SchemaVersion).IsEqualTo(1);
        await Assert.That(handshake.Fingerprint).StartsWith("sha256:6de32db0");
        await Assert.That(handshake.GrantedCapabilities.Contains("sessionMcp")).IsTrue();

        var methods = rig.Process.Writes.Select(w => (string)JsonNode.Parse(w)!["method"]!).ToList();
        await Assert.That(methods.Take(3)).IsEquivalentTo(["initialize", "initialized", "session/start"], TUnit.Assertions.Enums.CollectionOrdering.Matching);

        var init = Params(rig.Host.WritesOf("initialize")[0]);
        await Assert.That((string?)init["clientInfo"]!["version"]).IsEqualTo("9.9.9");
    }

    [Test]
    public async Task Ready_barrier_faults_at_the_deadline() {
        await using var rig = NewRig(configure: h => h.Process.AutoInitializeResult = null, readyDeadline: TimeSpan.FromSeconds(5));

        await Assert.That(rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).IsCompleted).IsFalse();

        rig.Time.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));
    }

    [Test]
    public async Task A_ready_deadline_applies_when_the_caller_omits_one() =>
        await Assert.That(MuseMspHostedAgentRuntime.DefaultReadyDeadline).IsEqualTo(TimeSpan.FromSeconds(30));

    [Test]
    public async Task Ready_barrier_faults_when_the_host_exits_mid_handshake() {
        await using var rig = NewRig(configure: h => h.Silent.Add("session/start"));

        await rig.Host.WaitForWriteAsync("session/start");
        rig.Process.EndOfStream(1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));
    }

    [Test]
    public async Task Ready_barrier_faults_on_an_error_response_to_session_start() {
        await using var rig = NewRig(configure: h => h.Override = r =>
            (string?)r["method"] == "session/start" ? MuseScriptedHost.ErrorResponse((long)r["id"]!, "rejected") : null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));
        await Assert.That(rig.Runtime.Session).IsNull();
    }

    [Test]
    public async Task Session_start_carries_the_minted_id_and_lane_posture() {
        var servers = new JsonObject { ["kcap-flow-result"] = new JsonObject { ["command"] = "kcap", ["mode"] = "required" } };

        await using (var rig = await ReadyRig(Plan(isReviewer: true, mcpServers: servers))) {
            var p = Params(rig.Host.WritesOf("session/start")[0]);

            await Assert.That((string?)p["sessionId"]).IsEqualTo(MintedId);
            await Assert.That((string?)p["workspaceRoot"]).IsEqualTo("/workspace/project");
            await Assert.That((string?)p["modelId"]).IsEqualTo("muse-spark-1.3");
            await Assert.That((string?)p["providerId"]).IsEqualTo("meta");
            await Assert.That((string?)p["permissionProfile"]).IsEqualTo(":read-only");
            await Assert.That((string?)p["approvalMode"]).IsEqualTo("onRequest");
            await Assert.That((string?)p["commandId"]).IsNotNull();
            await Assert.That((string?)p["config"]!["mcpServers"]!["kcap-flow-result"]!["mode"]).IsEqualTo("required");

            var session = rig.Runtime.Session!;
            await Assert.That(session.SessionId).IsEqualTo(MintedId);
            await Assert.That(session.LogPath).IsEqualTo($"/muse-home/sessions/2026/10/13/{MintedId}/session.jsonl");
            await Assert.That(session.WorkspaceRoot).IsEqualTo("/workspace/project");
            await Assert.That(rig.Runtime.ResolvedModel).IsEqualTo("muse-spark-1.3");
        }

        await using (var rig = await ReadyRig(Plan())) {
            var p = Params(rig.Host.WritesOf("session/start")[0]);

            await Assert.That((string?)p["permissionProfile"]).IsEqualTo(":ask-me");
            await Assert.That((string?)p["approvalMode"]).IsEqualTo("onRequest");
            await Assert.That(p.ContainsKey("config")).IsFalse();
        }
    }

    static Action<MuseScriptedHost> StartAnswersWith(string sessionId) => h => h.Override = r =>
        (string?)r["method"] == "session/start"
            ? """{"jsonrpc":"2.0","id":@0@,"result":@1@}"""
                .Replace("@0@", ((long)r["id"]!).ToString(CultureInfo.InvariantCulture))
                .Replace("@1@", MuseMspWire.SessionStartResult.Replace(MuseMspWire.WireSessionId, sessionId))
            : null;

    [Test]
    public async Task Session_start_answering_another_id_faults_the_barrier() {
        await using var rig = NewRig(configure: StartAnswersWith("01a136a0-0000-7000-8000-0000000000ff"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).StartsWith("muse_session_id_mismatch: ");
        await Assert.That(rig.Runtime.Session).IsNull();
    }

    [Test]
    public async Task Session_start_answering_the_minted_id_without_dashes_is_the_same_session() {
        await using var rig = NewRig(configure: StartAnswersWith(MintedId.Replace("-", "", StringComparison.Ordinal)));

        await rig.Runtime.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(Guid.Parse(rig.Runtime.Session!.SessionId)).IsEqualTo(Guid.Parse(MintedId));
    }

    [Test]
    public async Task Resume_sends_session_resume_not_start() {
        const string resumeId = "01a13695-0736-75d0-8f4e-f121585b8453";
        await using var rig = await ReadyRig(Plan(resume: resumeId));

        await Assert.That(rig.Host.WritesOf("session/start")).IsEmpty();

        var p = Params(rig.Host.WritesOf("session/resume")[0]);
        await Assert.That((string?)p["sessionId"]).IsEqualTo(resumeId);
        await Assert.That((string?)p["commandId"]).IsNotNull();
        await Assert.That(rig.Runtime.Session!.SessionId).IsEqualTo(resumeId);
    }

    /// <summary>The server refuses a registration whose transport is not the one it expects, and it
    /// expects <c>pty</c> for every vendor but Codex, so a Muse agent reports <c>pty</c>.</summary>
    [Test]
    public async Task Runtime_identity_is_the_msp_control_plane() {
        await using var rig = await ReadyRig();

        await Assert.That(rig.Runtime.Vendor).IsEqualTo("muse");
        await Assert.That(rig.Runtime.RuntimeTransport).IsEqualTo("pty");
        await Assert.That(rig.Runtime.EmitsTerminalOutput).IsFalse();
        await Assert.That(rig.Runtime.Pid).IsEqualTo(4242);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await rig.Runtime.SendRawInputAsync([1]));
    }

    // ---- Input ----

    [Test]
    public async Task Input_when_idle_is_turn_start_and_mid_turn_is_turn_steer() {
        await using var rig = await ReadyRig();

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("first").WaitAsync(HangGuard);

        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("second").WaitAsync(HangGuard);

        var start = Params(rig.Host.WritesOf("turn/start").Single());
        await Assert.That((string?)start["sessionId"]).IsEqualTo(MintedId);
        await Assert.That((string?)start["input"]![0]!["text"]).IsEqualTo("first");

        var steer = Params(rig.Host.WritesOf("turn/steer").Single());
        await Assert.That((string?)steer["expectedTurnId"]).IsEqualTo(MuseMspWire.WireTurnId);
        await Assert.That((string?)steer["input"]![0]!["text"]).IsEqualTo("second");
    }

    [Test]
    public async Task Stale_steer_falls_back_to_a_fresh_turn() {
        await using var rig = await ReadyRig(configure: h => h.Override = r =>
            (string?)r["method"] == "turn/steer" ? MuseScriptedHost.ErrorResponse((long)r["id"]!, "expected turn is not running") : null);

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("first").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();

        var second = rig.Runtime.SendUserInputAndWaitForWriteAsync("again");

        await rig.Host.WaitForWriteAsync("turn/steer");
        await rig.Host.SyncAsync();
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(rig.Host.WritesOf("turn/start")).Count().IsEqualTo(1);

        rig.Process.Push(MuseMspWire.TurnCompleted);
        await second.WaitAsync(HangGuard);

        var starts = rig.Host.WritesOf("turn/start");
        await Assert.That(starts).Count().IsEqualTo(2);
        await Assert.That((string?)Params(starts[1])["input"]![0]!["text"]).IsEqualTo("again");
        await Assert.That(rig.Host.WritesOf("turn/steer")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Input_after_terminal_is_not_admitted() {
        await using var rig = await ReadyRig();
        rig.Process.EndOfStream(0);

        await DrainAsync(rig.Runtime).WaitAsync(HangGuard);

        await Assert.ThrowsAsync<InputNotAdmittedException>(async () => await rig.Runtime.SendUserInputAsync("late"));
        await Assert.ThrowsAsync<InputNotAdmittedException>(async () => await rig.Runtime.SendUserInputAndWaitForWriteAsync("late"));
    }

    [Test]
    public async Task Escape_cancels_the_running_turn_and_is_a_no_op_when_idle() {
        await using var rig = await ReadyRig();

        await rig.Runtime.SendSpecialKeyAsync("Escape");
        await rig.Runtime.SendUserInputAndWaitForWriteAsync("go").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();

        await rig.Runtime.SendSpecialKeyAsync("Escape");

        var cancel = Params(await rig.Host.WaitForWriteAsync("turn/cancel"));
        await Assert.That((string?)cancel["turnId"]).IsEqualTo(MuseMspWire.WireTurnId);
        await Assert.That(rig.Host.WritesOf("turn/cancel")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Turn_idle_waits_for_the_running_turn_to_complete() {
        await using var rig = await ReadyRig();

        await rig.Runtime.WaitForTurnIdleAsync(CancellationToken.None).WaitAsync(HangGuard);

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("go").WaitAsync(HangGuard);
        var idle = rig.Runtime.WaitForTurnIdleAsync(CancellationToken.None);

        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();
        await Assert.That(idle.IsCompleted).IsFalse();

        rig.Process.Push(MuseMspWire.TurnCompleted);
        await idle.WaitAsync(HangGuard);
    }

    /// <summary>The host acknowledges <c>turn/start</c> before it sends <c>turn/started</c>, so an idle wait
    /// issued right after the acknowledged send must already see the turn as running.</summary>
    [Test]
    public async Task Idle_wait_right_after_an_acknowledged_send_waits_for_the_turn() {
        await using var rig = await ReadyRig();

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("go").WaitAsync(HangGuard);
        var idle = rig.Runtime.WaitForTurnIdleAsync(CancellationToken.None);

        await rig.Host.SyncAsync();
        await Assert.That(idle.IsCompleted).IsFalse();

        rig.Process.Push(MuseMspWire.StatusIdle);
        await idle.WaitAsync(HangGuard);
    }

    /// <summary>Nothing clears a turn after terminal, so a late <c>turn/started</c> must not mark one running
    /// and park the next idle waiter forever.</summary>
    [Test]
    public async Task A_turn_started_after_the_session_closed_leaves_the_runtime_idle() {
        await using var rig = await ReadyRig();

        rig.Process.Push(MuseMspWire.SessionClosed);
        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();

        await Assert.That(rig.Runtime.WaitForTurnIdleAsync(CancellationToken.None).IsCompleted).IsTrue();
    }

    // ---- Approvals ----

    [Test]
    public async Task Interactive_approval_maps_the_decision_to_a_choice_id() {
        var asked = new List<AcpInteractionRequest>();
        await using var rig = await ReadyRig(interact: (req, _) => {
            lock (asked) asked.Add(req);
            return Task.FromResult(new AcpInteractionDecision("allow", "allow_session", "Allow for this session", null, null, null));
        });

        rig.Process.Push(MuseMspWire.ApprovalRequested);

        var decide = Params(await rig.Host.WaitForWriteAsync("approval/decide"));
        await Assert.That((string?)decide["approvalId"]).IsEqualTo(ApprovalId);
        await Assert.That((string?)decide["choiceId"]).IsEqualTo("allow_session");
        await Assert.That((string?)decide["sessionId"]).IsEqualTo(MuseMspWire.WireSessionId);
        await Assert.That((string?)decide["requirementId"]!["approvalId"]).IsEqualTo(ApprovalId);
        await Assert.That((long?)decide["requirementId"]!["sourceIndex"]).IsEqualTo(0L);
        await Assert.That(decide.ContainsKey("feedback")).IsFalse();

        await Assert.That(asked).Count().IsEqualTo(1);
        await Assert.That(asked[0].AcpSessionId).IsEqualTo(MintedId);
        await Assert.That(asked[0].ToolName).IsEqualTo(SearchTool);
    }

    [Test]
    public async Task Updated_requirement_is_decided_again() {
        var asks = 0;
        await using var rig = await ReadyRig(interact: (_, _) => {
            Interlocked.Increment(ref asks);
            return Task.FromResult(new AcpInteractionDecision("allow", "allow_once", null, null, null, null));
        });

        rig.Process.Push(MuseMspWire.ApprovalRequested);
        await rig.Host.WaitForWriteAsync("approval/decide");

        rig.Process.Push(ApprovalUpdated(sourceIndex: 1));

        var second = Params(await rig.Host.WaitForWriteAsync("approval/decide", 2));
        await Assert.That((long?)second["requirementId"]!["sourceIndex"]).IsEqualTo(1L);
        await Assert.That((string?)second["choiceId"]).IsEqualTo("allow_once");
        await Assert.That(Volatile.Read(ref asks)).IsEqualTo(2);
    }

    [Test]
    public async Task Update_for_an_already_decided_requirement_is_not_decided_again() {
        var asks = 0;
        var rig = await ReadyRig(interact: (_, _) => {
            Interlocked.Increment(ref asks);
            return Task.FromResult(new AcpInteractionDecision("allow", "allow_once", null, null, null, null));
        });

        rig.Process.Push(MuseMspWire.ApprovalRequested);
        await rig.Host.WaitForWriteAsync("approval/decide");

        // The pump starts any prompt for the update before it reads the shell approval, and dispose joins
        // every started prompt, so a re-ask is always counted by the time asks is read.
        rig.Process.Push(ApprovalUpdated(sourceIndex: 0));
        rig.Process.Push(MuseMspWire.ShellApprovalRequested);

        var shell = Params(await rig.Host.WaitForWriteAsync("approval/decide", 2));
        await Assert.That((string?)shell["approvalId"]).IsEqualTo(ShellApprovalId);
        await rig.DisposeAsync();

        await Assert.That(rig.Host.WritesOf("approval/decide").Count(d => (string?)Params(d)["approvalId"] == ApprovalId)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref asks)).IsEqualTo(2);
    }

    [Test]
    public async Task Resolved_approval_cancels_the_prompt_and_is_never_decided() {
        var asked     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new TaskCompletionSource<AcpInteractionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rig = await ReadyRig(interact: (_, ct) => {
            ct.Register(() => cancelled.TrySetResult());
            asked.TrySetResult();
            return release.Task;
        });

        rig.Process.Push(MuseMspWire.ApprovalRequested);
        await asked.Task.WaitAsync(HangGuard);

        rig.Process.Push(ApprovalResolved());
        await cancelled.Task.WaitAsync(HangGuard);

        // The human's answer arrives after the host already resolved it.
        release.SetResult(new AcpInteractionDecision("allow", "allow_once", null, null, null, null));
        await rig.DisposeAsync();

        await Assert.That(rig.Host.WritesOf("approval/decide")).IsEmpty();
    }

    [Test]
    public async Task Child_approval_is_labelled() {
        var asked = new TaskCompletionSource<AcpInteractionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await ReadyRig(interact: (req, _) => {
            asked.TrySetResult(req);
            return Task.FromResult(new AcpInteractionDecision("deny", "abort", null, null, "no", null));
        });

        rig.Process.Push(MuseMspWire.ApprovalRequested.Replace("\"subject\":", "\"subagentOrigin\":{\"taskId\":\"child-1\"},\"subject\":"));

        var request = await asked.Task.WaitAsync(HangGuard);
        await Assert.That(request.Prompt).StartsWith("[subagent] ");

        var decide = Params(await rig.Host.WaitForWriteAsync("approval/decide"));
        await Assert.That((string?)decide["choiceId"]).IsEqualTo("abort");
        await Assert.That((string?)decide["feedback"]).IsEqualTo("no");
    }

    [Test]
    public async Task Interaction_failure_decides_the_abort_choice() {
        await using var rig = await ReadyRig(interact: (_, _) => Task.FromException<AcpInteractionDecision>(new IOException("hub gone")));

        rig.Process.Push(MuseMspWire.ApprovalRequested);

        var decide = Params(await rig.Host.WaitForWriteAsync("approval/decide"));
        await Assert.That((string?)decide["choiceId"]).IsEqualTo("abort");
    }

    [Test]
    public async Task Reviewer_lane_decides_without_asking() {
        var asks = 0;
        await using var rig = await ReadyRig(
            Plan(isReviewer: true, admitted: new HashSet<string> { SearchTool }.ToFrozenSet()),
            interact: (_, _) => {
                Interlocked.Increment(ref asks);
                throw new InvalidOperationException("a reviewer never asks");
            });

        rig.Process.Push(MuseMspWire.ApprovalRequested);
        rig.Process.Push(MuseMspWire.ShellApprovalRequested);

        var allowed = Params(await rig.Host.WaitForWriteAsync("approval/decide"));
        var refused = Params(await rig.Host.WaitForWriteAsync("approval/decide", 2));

        var byApproval = new[] { allowed, refused }.ToDictionary(p => (string)p["approvalId"]!);
        await Assert.That((string?)byApproval[ApprovalId]["choiceId"]).IsEqualTo("allow_once");
        await Assert.That((string?)byApproval[ShellApprovalId]["choiceId"]).IsEqualTo("abort");
        await Assert.That(Volatile.Read(ref asks)).IsEqualTo(0);
    }

    /// <summary>The host can settle an approval by policy before the daemon's decision lands; the
    /// refusal that follows is expected, not a failure.</summary>
    [Test]
    public async Task A_decision_on_an_already_resolved_approval_is_benign() {
        var logger = new CaptureLogger();
        await using var rig = await ReadyRig(
            Plan(isReviewer: true, admitted: new HashSet<string> { SearchTool }.ToFrozenSet()),
            configure: h => h.Override = r => (string?)r["method"] == "approval/decide" ? AlreadyResolved((long)r["id"]!) : null,
            logger: logger);

        rig.Process.Push(MuseMspWire.ApprovalRequested);
        await rig.Host.WaitForWriteAsync("approval/decide");

        await logger.WaitForAsync(e => e.Message.Contains("already resolved", StringComparison.Ordinal)).WaitAsync(HangGuard);

        await Assert.That(logger.Entries.Where(e => e.Level >= LogLevel.Warning)).IsEmpty();
        await Assert.That(logger.Entries.Where(e => e.Exception is not null)).IsEmpty();
    }

    [Test]
    public async Task A_reviewer_decider_that_throws_declines_the_approval() {
        await using var rig = await ReadyRig(Plan(isReviewer: true, admitted: new ThrowingSet()));

        rig.Process.Push(MuseMspWire.ApprovalRequested);

        var decide = Params(await rig.Host.WaitForWriteAsync("approval/decide"));
        await Assert.That((string?)decide["choiceId"]).IsEqualTo("abort");
    }

    // ---- Questions ----

    [Test]
    public async Task Question_answer_and_cancel() {
        var answers = new Queue<AcpInteractionDecision>([
            new AcpInteractionDecision("allow", "Blue", "Blue", 1, null, null),
            new AcpInteractionDecision("cancel", null, null, null, null, null),
        ]);
        AcpInteractionRequest? seen = null;

        await using var rig = await ReadyRig(interact: (req, _) => {
            lock (answers) {
                seen = req;
                return Task.FromResult(answers.Dequeue());
            }
        });

        rig.Process.Push(MuseMspWire.UserInputRequested);

        var answer = Params(await rig.Host.WaitForWriteAsync("userInput/answer"));
        await Assert.That((string?)answer["userInputId"]).IsEqualTo("01a13696-9fe2-7cf0-9559-4c92d2d22e4e");
        await Assert.That((string?)answer["answers"]![0]!["questionId"]).IsEqualTo("colour_pref");
        await Assert.That((string?)answer["answers"]![0]!["selectedLabel"]).IsEqualTo("Blue");
        await Assert.That(seen!.Value.Kind).IsEqualTo("elicitation");

        rig.Process.Push(MuseMspWire.UserInputRequested);

        var cancel = Params(await rig.Host.WaitForWriteAsync("userInput/cancel"));
        await Assert.That((string?)cancel["userInputId"]).IsEqualTo("01a13696-9fe2-7cf0-9559-4c92d2d22e4e");
        await Assert.That(rig.Host.WritesOf("userInput/answer")).Count().IsEqualTo(1);
        await Assert.That(rig.Host.WritesOf("userInput/cancel")).Count().IsEqualTo(1);
    }

    /// <summary>Muse refuses an answer that does not cover every question it asked, and the question stays
    /// open with the agent waiting on it, so a refused answer must be followed by a cancel.</summary>
    [Test]
    public async Task A_refused_answer_cancels_the_question() {
        await using var rig = await ReadyRig(interact: (_, _) =>
            Task.FromResult(new AcpInteractionDecision("allow", "Blue", "Blue", 1, null, null)));

        rig.Host.Override = r =>
            (string?)r["method"] == "userInput/answer" ? MuseScriptedHost.ErrorResponse((long)r["id"]!, "missing answers") : null;

        rig.Process.Push(MuseMspWire.UserInputRequested);

        await rig.Host.WaitForWriteAsync("userInput/answer");
        var cancel = Params(await rig.Host.WaitForWriteAsync("userInput/cancel"));
        await Assert.That((string?)cancel["userInputId"]).IsEqualTo("01a13696-9fe2-7cf0-9559-4c92d2d22e4e");
    }

    [Test]
    public async Task Reviewer_question_is_cancelled_at_once() {
        await using var rig = await ReadyRig(Plan(isReviewer: true), interact: (_, _) => throw new InvalidOperationException("a reviewer never asks"));

        rig.Process.Push(MuseMspWire.UserInputRequested);

        await rig.Host.WaitForWriteAsync("userInput/cancel");
        await Assert.That(rig.Host.WritesOf("userInput/answer")).IsEmpty();
    }

    // ---- Stop and exit ----

    [Test]
    public async Task Graceful_stop_cancels_then_closes_stdin_then_waits_for_closed() {
        await using var rig = await ReadyRig();

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("go").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();

        var closing = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Process.OnInputClose = () =>
            closing.TrySetResult((string?)JsonNode.Parse(rig.Process.Writes[^1])!["method"]);

        var stop = rig.Runtime.RequestGracefulStopAsync();

        await Assert.That(await closing.Task.WaitAsync(HangGuard)).IsEqualTo("turn/cancel");
        await Assert.That((string?)Params(rig.Host.WritesOf("turn/cancel").Single())["turnId"]).IsEqualTo(MuseMspWire.WireTurnId);

        await rig.Host.SyncAsync();
        await Assert.That(stop.IsCompleted).IsFalse();
        await Assert.That(rig.Runtime.SessionClosedSeen).IsFalse();

        rig.Process.Push(MuseMspWire.SessionClosed);
        await stop.WaitAsync(HangGuard);

        await Assert.That(rig.Runtime.SessionClosedSeen).IsTrue();
        await Assert.That(rig.Process.InputCloseCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Graceful_stop_gives_up_after_its_grace() {
        await using var rig = await ReadyRig();

        var stop = rig.Runtime.RequestGracefulStopAsync();
        await rig.Host.SyncAsync();
        await Assert.That(stop.IsCompleted).IsFalse();

        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await stop.WaitAsync(HangGuard);

        await Assert.That(rig.Host.WritesOf("turn/cancel")).IsEmpty();
        await Assert.That(rig.Runtime.SessionClosedSeen).IsFalse();
    }

    [Test]
    public async Task Closed_mid_turn_releases_waiters() {
        await using var rig = await ReadyRig();

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("go").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        await rig.Host.SyncAsync();

        var idle   = rig.Runtime.WaitForTurnIdleAsync(CancellationToken.None);
        var output = DrainAsync(rig.Runtime);
        await Assert.That(idle.IsCompleted).IsFalse();

        rig.Process.Push(MuseMspWire.SessionClosed);

        await idle.WaitAsync(HangGuard);
        await output.WaitAsync(HangGuard);
        await Assert.That(rig.Runtime.SessionClosedSeen).IsTrue();
        await Assert.ThrowsAsync<InputNotAdmittedException>(async () => await rig.Runtime.SendUserInputAsync("late"));
    }

    [Test]
    public async Task Exit_classification_exposes_code_and_closed_flag() {
        await using (var rig = await ReadyRig()) {
            rig.Process.Push(MuseMspWire.SessionClosed);
            rig.Process.EndOfStream(0);
            await DrainAsync(rig.Runtime).WaitAsync(HangGuard);

            await Assert.That(rig.Runtime.HasExited).IsTrue();
            await Assert.That(rig.Runtime.ExitCode).IsEqualTo(0);
            await Assert.That(rig.Runtime.SessionClosedSeen).IsTrue();
        }

        await using (var rig = await ReadyRig()) {
            rig.Process.EndOfStream(137);
            await DrainAsync(rig.Runtime).WaitAsync(HangGuard);

            await Assert.That(rig.Runtime.HasExited).IsTrue();
            await Assert.That(rig.Runtime.ExitCode).IsEqualTo(137);
            await Assert.That(rig.Runtime.SessionClosedSeen).IsFalse();
        }
    }

    [Test]
    public async Task Dispose_terminates_a_live_host_and_runs_the_callback_once() {
        var disposed = 0;
        var host     = new MuseScriptedHost();
        var rt = new MuseMspHostedAgentRuntime(host.Process, NullLogger.Instance, "agent-1", "/w", null, new FakeTimeProvider(),
            (_, _) => throw new InvalidOperationException(), Plan(), "9.9.9", onDisposed: () => Interlocked.Increment(ref disposed));

        await rt.WaitForSessionReadyAsync(CancellationToken.None).WaitAsync(HangGuard);
        await rt.DisposeAsync();
        await rt.DisposeAsync();

        await Assert.That(host.Process.TerminateCalls).IsEqualTo(1);
        await Assert.That(host.Process.DisposeCalls).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref disposed)).IsEqualTo(1);
    }

    // ---- Status ----

    [Test]
    public async Task Token_usage_updates_status_only() {
        await using var rig = await ReadyRig();
        var writesBefore = rig.Process.Writes.Count;

        rig.Process.Push(MuseMspWire.SessionTokenUsage);
        await rig.Host.SyncAsync();

        var usage = rig.Runtime.LastUsage!;
        await Assert.That(usage.InputTokens).IsEqualTo(35873L);
        await Assert.That(usage.OutputTokens).IsEqualTo(259L);
        await Assert.That(usage.CachedTokens).IsEqualTo(0L);
        await Assert.That(usage.ReasoningTokens).IsEqualTo(166L);
        await Assert.That(usage.ModelId).IsEqualTo("muse-spark-1.3");

        // Only the sync receipt went back; usage is never answered or recorded.
        await Assert.That(rig.Process.Writes.Count).IsEqualTo(writesBefore + 1);
    }

    [Test]
    public async Task Model_change_updates_the_resolved_model() {
        await using var rig = await ReadyRig();

        rig.Process.Push("""{"jsonrpc":"2.0","method":"session/modelChanged","params":{"sessionId":"@0@","modelId":"muse-wonder-1.0","providerId":"meta","source":"user","sourceRange":{},"viewCursor":"v:1"}}""".Replace("@0@", MintedId));
        await rig.Host.SyncAsync();

        await Assert.That(rig.Runtime.ResolvedModel).IsEqualTo("muse-wonder-1.0");
    }

    // ---- Reviewer verdict ----

    [Test]
    public async Task Reviewer_model_failure_is_a_coded_verdict_and_stops_the_reviewer() {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await ReadyRig(Plan(isReviewer: true), configure: h => {
            h.Process.ExitsOnInputClose = true;
            h.Process.OnInputClose      = () => closed.TrySetResult();
        });

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("review").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        rig.Process.Push(FailedTurn("modelError", "provider returned 429 Too Many Requests"));

        await closed.Task.WaitAsync(HangGuard);

        var verdict = rig.Runtime.ReadVerdict()!;
        await Assert.That(verdict.Reason).StartsWith("muse_reviewer_model_failed: ");
        await Assert.That(verdict.Reason).Contains("429");
        await Assert.That(verdict.ReapedInsideLaunchWindow).IsTrue();
    }

    [Test]
    public async Task Interactive_model_failure_leaves_no_verdict() {
        await using var rig = await ReadyRig();

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("go").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        rig.Process.Push(FailedTurn("authRequired", "401 unauthorized"));
        await rig.Host.SyncAsync();

        await Assert.That(rig.Runtime.ReadVerdict()).IsNull();
        await Assert.That(rig.Process.InputCloseCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Reviewer_quota_failure_is_a_coded_verdict() {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = await ReadyRig(Plan(isReviewer: true), configure: h => {
            h.Process.ExitsOnInputClose = true;
            h.Process.OnInputClose      = () => closed.TrySetResult();
        });

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("review").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        rig.Process.Push(FailedTurn("modelError", "usage exhausted", ""","quota":{"limitKind":"daily","resetsAtMs":1791917124377}"""));

        await closed.Task.WaitAsync(HangGuard);

        await Assert.That(rig.Runtime.ReadVerdict()!.Reason).IsEqualTo("muse_reviewer_model_failed: usage exhausted");
    }

    [Test]
    [Arguments("""{"kind":"modelError","message":"provider returned 429 Too Many Requests","retryable":true}""", true)]
    [Arguments("""{"kind":"modelError","message":"rate_limit_exceeded","retryable":true}""", true)]
    [Arguments("""{"kind":"modelError","message":"auth token expired","retryable":false}""", true)]
    [Arguments("""{"kind":"modelError","message":"Unauthorized","retryable":false}""", true)]
    [Arguments("""{"kind":"authRequired","message":"sign in again","retryable":false}""", true)]
    [Arguments("""{"kind":"modelError","message":"out of credit","quota":{"limitKind":"monthly","resetsAtMs":1},"retryable":false}""", true)]
    [Arguments("""{"kind":"modelError","message":"the author of this file is unknown","retryable":false}""", false)]
    [Arguments("""{"kind":"modelError","message":"see authority record 14290","retryable":false}""", false)]
    [Arguments("""{"kind":"stepLimit","message":"step limit reached","retryable":false}""", false)]
    public async Task A_model_failure_is_recognised_by_type_or_whole_word(string error, bool expected) {
        using var doc = JsonDocument.Parse(error);

        await Assert.That(MuseMspHostedAgentRuntime.IsModelFailure(doc.RootElement)).IsEqualTo(expected);
    }

    [Test]
    public async Task Reviewer_failure_of_another_kind_leaves_no_verdict() {
        await using var rig = await ReadyRig(Plan(isReviewer: true));

        await rig.Runtime.SendUserInputAndWaitForWriteAsync("review").WaitAsync(HangGuard);
        rig.Process.Push(MuseMspWire.TurnStarted);
        rig.Process.Push(FailedTurn("stepLimit", "step limit reached"));
        await rig.Host.SyncAsync();

        await Assert.That(rig.Runtime.ReadVerdict()).IsNull();
    }

    // ---- Helpers ----

    static async Task DrainAsync(MuseMspHostedAgentRuntime rt) {
        await foreach (var _ in rt.ReadOutputAsync()) { }
    }

    static string ApprovalUpdated(int sourceIndex) =>
        """{"jsonrpc":"2.0","method":"approval/updated","params":{"sessionId":"@0@","viewCursor":"v:2","sourceRange":{},"approvalId":"@1@","currentRequirementId":{"approvalId":"@2@","sourceIndex":@3@},"subject":{"kind":"tool","toolName":"@4@"},"availableChoices":[{"choiceId":"allow_once","label":"Allow once","decision":"approved","scope":"once"},{"choiceId":"abort","label":"Reject","decision":"abort","scope":"once","acceptsFeedback":true}],"change":{"kind":"stageResolved"}}}""".Replace("@0@", MuseMspWire.WireSessionId).Replace("@1@", ApprovalId).Replace("@2@", ApprovalId).Replace("@3@", sourceIndex.ToString(CultureInfo.InvariantCulture)).Replace("@4@", SearchTool);

    static string ApprovalResolved() =>
        """{"jsonrpc":"2.0","method":"approval/resolved","params":{"sessionId":"@0@","viewCursor":"v:3","sourceRange":{},"approvalId":"@1@","itemId":"@2@","turnId":"@3@","decision":"approved","policyResult":"allow","resolvedBy":"judge","stageEvidence":[]}}""".Replace("@0@", MuseMspWire.WireSessionId).Replace("@1@", ApprovalId).Replace("@2@", ApprovalId).Replace("@3@", MuseMspWire.WireTurnId);

    static string FailedTurn(string kind, string message, string extra = "") =>
        """{"jsonrpc":"2.0","method":"turn/completed","params":{"sessionId":"@0@","viewCursor":"v:4","sourceRange":{},"turnId":"@1@","terminal":"failed","error":{"kind":"@2@","message":"@3@","retryable":false@4@}}}""".Replace("@0@", MintedId).Replace("@1@", MuseMspWire.WireTurnId).Replace("@2@", kind).Replace("@3@", message).Replace("@4@", extra);

    static string AlreadyResolved(long id) =>
        """{"jsonrpc":"2.0","id":@0@,"error":{"code":-32051,"message":"approval already resolved","data":{"kind":"approvalAlreadyResolved","retryable":false}}}""".Replace("@0@", id.ToString(CultureInfo.InvariantCulture));

    sealed class ThrowingSet : IReadOnlySet<string> {
        public int Count => throw new InvalidOperationException("admission set unavailable");
        public bool Contains(string item) => throw new InvalidOperationException("admission set unavailable");
        public bool IsProperSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<string> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<string> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<string> other) => throw new NotSupportedException();
        public IEnumerator<string> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    sealed class CaptureLogger : ILogger {
        readonly Lock           _gate    = new();
        readonly List<LogEntry> _entries = [];
        TaskCompletionSource    _logged  = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<LogEntry> Entries {
            get { lock (_gate) return _entries.ToArray(); }
        }

        public async Task WaitForAsync(Func<LogEntry, bool> match) {
            while (true) {
                Task signal;
                lock (_gate) {
                    if (_entries.Any(match)) return;
                    signal = _logged.Task;
                }

                await signal;
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            TaskCompletionSource signal;
            lock (_gate) {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
                signal  = _logged;
                _logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
        }
    }
}
