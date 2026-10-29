using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The ACP factory honours a restart resume only for a vendor whose descriptor declares it, and never
/// answers one it cannot honour with a fresh session.
/// </summary>
public class AcpHostedAgentRuntimeFactoryRestartResumeTests {
    static readonly TimeSpan HangGuard = WaitHarness.AcpHangGuard;

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Pid       => 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            _exited.TrySetResult();

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static readonly AcpVendorDescriptor Unprobed = new(
        Harness:             HarnessId.Cursor,
        Argv:                ["acp"],
        UnattendedTrustArgv: [],
        SupportsUnattended:  false,
        ModelSelector:       NoOpModelSelector.Instance,
        SupportsMcpServers:  false);

    static RuntimeStartContext Context(string? resumeSessionId, bool reviewFlow = false) => new(
        AgentId: "agent-1", Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "branch-name", SourceRepo: "/repo"), Prompt: "the nudge",
        Model: "default", Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: reviewFlow, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        ResumeSessionId: resumeSessionId);

    static (IHostedAgentRuntimeFactory Factory, Func<int> Spawns) Build(AcpVendorDescriptor descriptor, FakeAcpAgent fake) {
        var spawns = 0;
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: new CaptureServerConnection(),
            timeProvider: TimeProvider.System,
            connectionSource: _ => {
                spawns++;
                return (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess());
            });

        return (factory, () => spawns);
    }

    /// <summary>Read through the interface, as the resume lane reads it: deleting the override would
    /// fall back to the interface default rather than fail to compile.</summary>
    [Test]
    public async Task The_factory_reports_its_descriptors_restart_resume_support() {
        await using var fake = new FakeAcpAgent();

        await Assert.That(Build(AcpVendorDescriptors.Cursor, fake).Factory.SupportsRestartResume("acp")).IsTrue();
        await Assert.That(Build(Unprobed, fake).Factory.SupportsRestartResume("acp")).IsFalse();
    }

    [Test]
    public async Task A_restart_resume_reopens_the_ledgers_session() {
        await using var fake = new FakeAcpAgent();
        using var cts = new CancellationTokenSource();
        var loop = fake.RunAsync(cts.Token);

        var (factory, _) = Build(AcpVendorDescriptors.Cursor, fake);
        var started = await factory.StartAsync(Context("vendor-session-1"), cts.Token).WaitAsync(HangGuard);

        var load = fake.ReceivedCalls.Single(c => c.Method == "session/load").Params!.Value;
        await Assert.That(load.GetProperty("sessionId").GetString()).IsEqualTo("vendor-session-1");
        await Assert.That(fake.ReceivedCalls.Select(c => c.Method)).DoesNotContain("session/new");
        await Assert.That(started.Transcript!.AcpSessionId).IsEqualTo("vendor-session-1");

        await cts.CancelAsync();
        try { await loop.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_resume_the_factory_cannot_honour_fails_before_spawning(bool reviewFlow) {
        await using var fake = new FakeAcpAgent();
        var (factory, spawns) = Build(reviewFlow ? AcpVendorDescriptors.Cursor : Unprobed, fake);

        await Assert.That(async () => await factory.StartAsync(Context("vendor-session-1", reviewFlow), CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageContaining("vendor-session-1");
        await Assert.That(spawns()).IsEqualTo(0);
    }
}
