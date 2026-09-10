using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The composer's SendText handler. Every outcome — refusal, drop, delivery, quit — is one
/// SendTextAck the composer can word, written only once the delivery core has settled, and no
/// path lets an exception escape onto the socket handler.
/// </summary>
public class LocalSendTextTests {
    static AgentOrchestrator Build() =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    static async Task<SendTextAckDto> Send(AgentOrchestrator orch, string payload) {
        using var ms = new MemoryStream();
        await orch.HandleLocalSendTextAsync(payload, ms, CancellationToken.None);
        ms.Position = 0;
        var frame = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.SendTextAck);

        return JsonSerializer.Deserialize(frame.Text, InputIpcJsonContext.Default.SendTextAckDto)!;
    }

    static string Payload(string agentId, string text) =>
        JsonSerializer.Serialize(new SendTextDto(agentId, text), InputIpcJsonContext.Default.SendTextDto);

    /// <summary>A real Antigravity runtime whose pending-turns queue is genuinely full: capacity 1,
    /// a turn that never ends, one message executing and one queued behind it. The conversation-id
    /// barrier is load-bearing — without it the second send races the worker's own dequeue and the
    /// refusal lands on the wrong message.</summary>
    static async Task<AntigravityHostedAgentRuntime> FullQueueRuntime() {
        var rt = AntigravityRuntimeFakes.FakeRuntime(FakeTurn.NeverEnds, queueCap: 1);
        await rt.SendUserInputAsync("first");
        await rt.WaitForConversationIdAsync(CancellationToken.None);
        await rt.SendUserInputAsync("queued");

        return rt;
    }

    [Test]
    [Arguments("not json")]
    [Arguments("{}")]
    [Arguments("""{"agent_id":"a1"}""")]
    [Arguments("""{"agent_id":null,"text":"x"}""")]
    [Arguments("""{"agent_id":"a1","text":null}""")]
    [Arguments("[]")]
    public async Task Malformed_payloads_ack_malformed(string payload) {
        await using var orch = Build();

        var ack = await Send(orch, payload);

        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.Malformed);
    }

    [Test]
    public async Task Empty_and_oversized_text_and_unknown_agent_are_named() {
        await using var orch = Build();

        await Assert.That((await Send(orch, Payload("a1", "   "))).Reason).IsEqualTo(SendTextReasons.TextEmpty);
        await Assert.That((await Send(orch, Payload("a1", new string('x', InputWire.MaxTextBytes + 1)))).Reason)
            .IsEqualTo(SendTextReasons.TooLarge);
        await Assert.That((await Send(orch, Payload("nope", "hi"))).Reason).IsEqualTo(SendTextReasons.NoSuchAgent);
    }

    [Test]
    public async Task Protected_kind_and_not_running_are_refused_before_the_core() {
        await using var orch = Build();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "rev", new FakeAcpRuntime(), kind: LaunchKind.Review);

        var ack = await Send(orch, Payload("rev", "hi"));

        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.ProtectedKind);
        await Assert.That(ack.Error).Contains("review");

        AgentOrchestratorHarness.SeedAcpAgent(orch, "starting", new FakeAcpRuntime(), status: "Starting");
        await Assert.That((await Send(orch, Payload("starting", "hi"))).Reason).IsEqualTo(SendTextReasons.NotRunning);
        AgentOrchestratorHarness.SeedAcpAgent(orch, "done", new FakeAcpRuntime(), status: "Completed");
        await Assert.That((await Send(orch, Payload("done", "hi"))).Reason).IsEqualTo(SendTextReasons.NotRunning);
    }

    [Test]
    public async Task Delivery_acks_ok_delivered_only_when_the_core_settles() {
        await using var orch = Build();
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt);

        var pending = Send(orch, Payload("a1", "hello"));
        await Task.Delay(200);

        await Assert.That(pending.IsCompleted).IsFalse();

        rt.SendUserInputGate!.SetResult();
        var ack = await pending;

        await Assert.That(ack).IsEqualTo(new SendTextAckDto(true, null, null, SendTextOutcomes.Delivered));
        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "hello" });
    }

    [Test]
    public async Task Queue_full_reaper_claim_and_delivery_failure_are_coded() {
        await using var orch = Build();
        await using var agy  = await FullQueueRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "full", agy);

        await Assert.That((await Send(orch, Payload("full", "x"))).Reason).IsEqualTo(SendTextReasons.QueueFull);

        var claimed = AgentOrchestratorHarness.SeedAcpAgent(orch, "claimed", new FakeAcpRuntime());
        AgentOrchestratorHarness.ClaimReap(claimed);

        await Assert.That((await Send(orch, Payload("claimed", "x"))).Reason).IsEqualTo(SendTextReasons.ReaperClaimed);

        var late = AgentOrchestratorHarness.SeedAcpAgent(orch, "late", new FakeAcpRuntime());
        orch.SendInputBeforeWriteHookForTest = () => { AgentOrchestratorHarness.ClaimReap(late); return Task.CompletedTask; };

        await Assert.That((await Send(orch, Payload("late", "x"))).Reason).IsEqualTo(SendTextReasons.ReaperClaimedLate);

        orch.SendInputBeforeWriteHookForTest = null;

        AgentOrchestratorHarness.SeedAcpAgent(orch, "faulty", new FakeAcpRuntime { SendUserInputThrow = new IOException("pipe closed") });
        var ack = await Send(orch, Payload("faulty", "x"));

        await Assert.That(ack.Reason).IsEqualTo(SendTextReasons.DeliveryFailed);
        await Assert.That(ack.Error).IsEqualTo("pipe closed");
    }

    /// <summary>A quit rides the owner's stop, which a private agent is eligible for — the
    /// server-origin stop handler would refuse it, and the composer typing into its own local agent
    /// would then get an ack for a stop that never happened.</summary>
    [Test]
    public async Task Quit_stops_a_private_and_a_public_agent_through_the_owner_stop_and_acks_stopped() {
        await using var orch = Build();
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        var pub = new FakeAcpRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "pub", pub);
        var prv = new FakeAcpRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "prv", prv, isPrivate: true);

        await Assert.That((await Send(orch, Payload("pub", "/quit"))).Outcome).IsEqualTo(SendTextOutcomes.Stopped);
        await Assert.That(pub.HasExited).IsTrue();
        await Assert.That((await Send(orch, Payload("prv", "/exit"))).Outcome).IsEqualTo(SendTextOutcomes.Stopped);
        await Assert.That(prv.HasExited).IsTrue();

        var stuck = new FakeAcpRuntime { NeverExits = true };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "stuck", stuck);

        await Assert.That((await Send(orch, Payload("stuck", "/quit"))).Reason).IsEqualTo(SendTextReasons.StopFailed);
    }

    [Test]
    public async Task Two_frames_against_a_pending_write_deliver_two_turns_in_order() {
        await using var orch = Build();
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt);

        var first  = Send(orch, Payload("a1", "one"));
        var second = Send(orch, Payload("a1", "two"));
        await Task.Delay(100);
        rt.SendUserInputGate!.SetResult();
        await Task.WhenAll(first, second);

        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "one", "two" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_cancelled_wait_and_a_failing_ack_write_each_still_deliver_exactly_once() {
        await using var orch = Build();
        var rt = new FakeAcpRuntime { SendUserInputGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a1", rt);

        using var cts       = new CancellationTokenSource();
        using var ackStream = new MemoryStream();
        var       cancelled = orch.HandleLocalSendTextAsync(Payload("a1", "one"), ackStream, cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        rt.SendUserInputGate!.SetResult();
        try { await cancelled; } catch (OperationCanceledException) { /* the ack never crossed; the write did */ }

        var rt2 = new FakeAcpRuntime();
        AgentOrchestratorHarness.SeedAcpAgent(orch, "a2", rt2);
        using var throwing = new ThrowingStream();
        await orch.HandleLocalSendTextAsync(Payload("a2", "two"), throwing, CancellationToken.None);

        await Assert.That(rt.SentInputs).IsEquivalentTo(new[] { "one" });
        await Assert.That(rt2.SentInputs).IsEquivalentTo(new[] { "two" });
    }

    sealed class ThrowingStream : Stream {
        public override bool CanRead  => false;
        public override bool CanSeek  => false;
        public override bool CanWrite => true;
        public override long Length   => 0;

        public override long Position { get => 0; set { } }

        public override void Flush() { }
        public override int  Read(byte[]  buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long    offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("socket closed");
    }
}
