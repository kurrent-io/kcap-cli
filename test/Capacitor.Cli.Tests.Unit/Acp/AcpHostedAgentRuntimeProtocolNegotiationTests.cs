// test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeProtocolNegotiationTests.cs
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Acp;

/// <summary>
/// AI-689 Workstream A: exercises <see cref="AcpHostedAgentRuntime.StartAsync"/>'s handling of the
/// <c>initialize</c> response — protocol-version validation (A1), captured
/// <c>agentCapabilities</c> (A2), and the auth/subscription hint appended to a handshake failure
/// without masking the original error (A4). Mirrors the <c>AcpHostedAgentRuntimeTests</c> harness
/// pattern; no real <c>cursor-agent acp</c> process.
/// </summary>
public class AcpHostedAgentRuntimeProtocolNegotiationTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    /// <summary>Minimal <see cref="IAcpProcess"/> stand-in — these tests never exercise process exit/terminate.</summary>
    sealed class FakeAcpProcess : IAcpProcess {
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }

        public Task WaitForExitAsync(TimeSpan? timeout = null) =>
            timeout is { } t ? Task.Delay(t) : Task.Delay(Timeout.InfiniteTimeSpan);

        public Task TerminateAsync(TimeSpan? timeout = null) {
            HasExited = true;
            ExitCode  = 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class Harness : IAsyncDisposable {
        public FakeAcpAgent          Fake    { get; }
        public AcpConnection         Conn    { get; }
        public FakeAcpProcess        Process { get; }
        public AcpHostedAgentRuntime Runtime { get; }
        public CancellationTokenSource Cts   { get; } = new();

        Task _fakeRunTask = Task.CompletedTask;

        public Harness() {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, NullLogger.Instance);
            Process = new FakeAcpProcess();
            Runtime = new AcpHostedAgentRuntime(Conn, Process, NullLogger.Instance);
        }

        public void StartFakeAgentLoop() => _fakeRunTask = Fake.RunAsync(Cts.Token);

        public async ValueTask DisposeAsync() {
            Cts.Cancel();
            try {
                await _fakeRunTask.WaitAsync(HangGuard);
            } catch (OperationCanceledException) {
                // expected shutdown path
            }
            await Runtime.DisposeAsync();
            await Fake.DisposeAsync();
            Cts.Dispose();
        }
    }

    [Test]
    public async Task StartAsync_ProtocolVersionMismatch_ThrowsClearVersionError() {
        await using var h = new Harness();
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 2, loadSession: true));
        h.StartFakeAgentLoop();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).Contains("version 2");
        await Assert.That(ex.Message).Contains("version 1");
    }

    [Test]
    public async Task StartAsync_LoadSessionTrue_ExposesSupportsLoadSession() {
        await using var h = new Harness();
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 1, loadSession: true));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        await Assert.That(h.Runtime.SupportsLoadSession).IsTrue();
        await Assert.That(h.Runtime.NegotiatedCapabilities).IsNotNull();
        await Assert.That(h.Runtime.NegotiatedCapabilities!.LoadSession).IsTrue();
    }

    [Test]
    public async Task StartAsync_LoadSessionFalse_ExposesSupportsLoadSessionFalse() {
        await using var h = new Harness();
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 1, loadSession: false));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        await Assert.That(h.Runtime.SupportsLoadSession).IsFalse();
    }

    [Test]
    public async Task StartAsync_AgentCapabilitiesAbsent_ExposesSupportsLoadSessionFalse_AndDoesNotThrow() {
        await using var h = new Harness();
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 1, loadSession: null));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);

        await Assert.That(h.Runtime.SupportsLoadSession).IsFalse();
    }

    [Test]
    public async Task StartAsync_InitializeRpcError_SurfacesOriginalErrorAndAuthHint() {
        await using var h = new Harness();
        h.Fake.FailNextInitialize(-32000, "Unauthorized: no active session");
        h.StartFakeAgentLoop();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).Contains("Unauthorized: no active session");
        await Assert.That(ex.Message).Contains("cursor-agent login");
    }
}
