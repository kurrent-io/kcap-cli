using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Daemon;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

// Phase B2-b (sequenced-settlement design): the orchestrator now owns an epoch-scoped
// SequencedCommandProcessor, advertises its Epoch/HighestAcceptedSeq/LastProcessedSeq counters on the
// self-report + connect payload, and exposes the single confirmed-death-precedence liveness read
// (ReadLiveness) the processor uses to answer duplicate CommandAcks. This partial reuses the vendor
// test class's BuildOrchestrator/CaptureServerConnection/SpyPtyProcessFactory/SeedAgentForTest harness.
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Report_advertises_sequencing_capability_and_counters() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        var report = orch.BuildStatusReport();
        await Assert.That(report.Epoch).IsNotNull();
        await Assert.That(report.HighestAcceptedSeq).IsEqualTo(0L); // fresh epoch, nothing accepted yet
        await Assert.That(report.LastProcessedSeq).IsEqualTo(0L);
    }

    [Test]
    public async Task ReadLiveness_follows_confirmed_death_precedence() {
        await using var orch = BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("live", LaunchKind.ReviewFlow, status: "Running");
        await Assert.That(orch.ReadLiveness("live")).IsEqualTo(AgentLiveness.Live);
        await Assert.That(orch.ReadLiveness("never")).IsEqualTo(AgentLiveness.Dead);
    }

    [Test]
    public async Task ReadLiveness_racing_transitions_never_yields_a_transient_false_dead() {
        // Parent §8: CommandAck.CurrentState racing live->quarantine / quarantine->dead transitions must
        // never surface a transient false Dead. Hammer ReadLiveness while the agent moves live -> quarantine
        // (teardown of a surviving child) -> dead (drain after the child exits). The shipped ordering
        // invariant (CleanupAgentAsync adds to _quarantine BEFORE removing from _agents) keeps the id
        // continuously in _agents ∪ _quarantine until the drain, so deadness is monotonic.
        var server = new CaptureServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());
        using var dummy = DummyProcess.StartSleep(30);
        orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f", flowRole: "reviewer",
            pty: new LivePtyDouble(dummy.Pid), startIdentity: ProcessIdentity.Capture(dummy.Pid));

        var observed = new System.Collections.Concurrent.ConcurrentQueue<AgentLiveness>();
        using var stop = new CancellationTokenSource();
        var hammer = Task.Run(() => { while (!stop.IsCancellationRequested) observed.Enqueue(orch.ReadLiveness("rev")); });

        await orch.CleanupAgentForTest("rev");                        // live -> quarantine (child still alive)
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));
        await orch.RetryQuarantineForTest();                          // quarantine -> dead (drain confirms death)
        stop.Cancel(); await hammer;

        // No transient false Dead: once Dead is observed it is never followed by a non-Dead reading.
        var seq = observed.ToArray();
        var firstDead = Array.IndexOf(seq, AgentLiveness.Dead);
        if (firstDead >= 0)
            await Assert.That(seq.Skip(firstDead).All(s => s == AgentLiveness.Dead)).IsTrue();
        await Assert.That(orch.ReadLiveness("rev")).IsEqualTo(AgentLiveness.Dead); // genuinely dead at the end
    }

    // Phase B2-b (sequenced-settlement design §4.2.2/§5.5): a Seq'd LaunchAgentCommand the shipped launch
    // would reject at capacity flows through the processor as a terminal LaunchRejected(daemon_capacity)
    // CommandOutcome, so the sequenced lane emits a CommandRejected (in addition to the legacy
    // LaunchFailed) and the watermark still advances. MaxConcurrentAgents=0 makes the very first admission
    // check reject with no launcher/worktree side effects.
    [Test]
    public async Task Sequenced_launch_over_capacity_emits_daemon_capacity_rejection() {
        var server = new SeqCaptureServerConnection();
        await using var orch = BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") },
            configure: c => c.MaxConcurrentAgents = 0);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "cap", Prompt: "hi", Model: "opus", Effort: null,
            RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
            Epoch: orch.DaemonEpochForTest, Seq: 1, CommandId: "cmd-1"));

        await Assert.That(server.Rejects.Count).IsEqualTo(1);
        await Assert.That(server.Rejects[0].Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
        await Assert.That(server.Rejects[0].Seq).IsEqualTo(1L);
        // The terminal (daemon_capacity) processing advanced the watermark through the accepted prefix.
        await Assert.That(orch.BuildStatusReport().LastProcessedSeq).IsEqualTo(1L);
    }

    /// <summary>A live-pid-backed pty double whose TerminateAsync deliberately does NOT kill (so teardown
    /// quarantines the "surviving" child). Mirrors LaunchCleanupTests' private LiveNoKillPtyProcess.</summary>
    sealed class LivePtyDouble(int pid) : IPtyProcess {
        public int  Pid       => pid;
        public bool HasExited => false;
        public int? ExitCode  => null;
        public ValueTask DisposeAsync() => default;
        public Task WaitForExitAsync(TimeSpan? _) => Task.CompletedTask;
        public Task TerminateAsync(TimeSpan?   _) => Task.CompletedTask; // deliberately does NOT kill
#pragma warning disable CS1998
        public async IAsyncEnumerable<byte[]> ReadOutputAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken _ = default) { yield break; }
#pragma warning restore CS1998
        public Task WriteAsync(string _) => Task.CompletedTask;
        public Task WriteAsync(byte[] _) => Task.CompletedTask;
        public void Resize(ushort     _, ushort __) { }
        public void SendInterrupt() { }
    }

    /// <summary>Captures the one-way sequenced CommandAck/CommandRejected sends (synchronously, so an
    /// awaited SubmitAsync has already recorded them) and no-ops everything else the capacity-reject path
    /// touches. IsReady is forced true so the base sends aren't short-circuited before our override.</summary>
    sealed class SeqCaptureServerConnection() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        NullLoggerFactory.Instance,
        NullLogger<ServerConnection>.Instance
    ) {
        internal override bool IsReady => true;
        public List<CommandRejected> Rejects { get; } = [];
        public List<CommandAck>      Acks    { get; } = [];

        public override Task LaunchFailedAsync(string agentId, string reason) => Task.CompletedTask;
        public override Task CommandRejectedAsync(CommandRejected rej) { lock (Rejects) Rejects.Add(rej); return Task.CompletedTask; }
        public override Task CommandAckAsync(CommandAck ack)           { lock (Acks)    Acks.Add(ack);    return Task.CompletedTask; }
    }
}
