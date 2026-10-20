using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A local stop reaches a launch whose agent has not registered yet: it cancels the start,
/// answers once the launch has settled, and the server hears the launch was cancelled.</summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorLaunchCancelTests {
    static LaunchAgentCommand Launch(string id, string repoPath) =>
        new(AgentId: id, Prompt: "go", Model: "", Effort: null, RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "cursor");

    static async Task<LocalFrame> StopAsync(AgentOrchestrator orch, string id, bool force = false) {
        using var stream = new MemoryStream();
        await orch.HandleLocalStopV2Async(force, id, stream, CancellationToken.None);
        stream.Position = 0;

        return (await FrameCodec.ReadAsync(stream, CancellationToken.None))!;
    }

    [Test]
    public async Task A_stop_during_the_handshake_cancels_the_launch() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server  = new CaptureServerConnection();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SpyHostedAgentRuntimeFactory("cursor") { StartGate = new(), OnStart = () => started.TrySetResult() };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        var launching = orch.HandleLaunchAgentForTest(Launch("starting-1", repoPath));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var reply = await StopAsync(orch, "starting-1");
        await launching.WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(reply.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("starting-1\tstopped");
        await Assert.That(server.LaunchFailedCalls).Contains(("starting-1", AgentOrchestrator.LaunchCancelledReason));
        await Assert.That(orch.GetAgentForTest("starting-1")).IsNull();
    }

    /// <summary>A launch still queued behind another on the command lane is refused when it starts.</summary>
    [Test]
    public async Task A_stop_while_queued_refuses_the_launch_when_it_starts() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server  = new CaptureServerConnection();
        var gate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SpyHostedAgentRuntimeFactory("cursor") { StartGate = gate, OnStart = () => started.TrySetResult() };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        var first = orch.HandleLaunchAgentForTest(Launch("ahead-1", repoPath));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var queued = orch.HandleLaunchAgentForTest(Launch("queued-1", repoPath));

        var reply = await StopAsync(orch, "queued-1");
        gate.SetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(reply.Text).IsEqualTo("queued-1\tstopped");
        await Assert.That(server.LaunchFailedCalls).Contains(("queued-1", AgentOrchestrator.LaunchCancelledReason));
        await Assert.That(factory.StartCalls).IsEqualTo(1);
    }

    /// <summary>A protected launch needs force, exactly as its agent will once it registers, including
    /// while it is still queued behind another.</summary>
    [Test]
    public async Task A_queued_protected_launch_is_not_cancelled_without_force() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server  = new CaptureServerConnection();
        var gate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new SpyHostedAgentRuntimeFactory("cursor") { StartGate = gate, OnStart = () => started.TrySetResult() };
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        var first = orch.HandleLaunchAgentForTest(Launch("ahead-1", repoPath));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var queued = orch.HandleLaunchAgentForTest(Launch("review-1", repoPath) with { Kind = LaunchKind.ReviewFlow });

        var reply = await StopAsync(orch, "review-1");
        gate.SetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(reply.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("--force");
        await Assert.That(server.LaunchFailedCalls).DoesNotContain(("review-1", AgentOrchestrator.LaunchCancelledReason));
    }
}
