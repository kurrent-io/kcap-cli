using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Acp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// AI-686 round-4, Finding 3: proves <see cref="AcpHostedAgentRuntimeFactory"/> — constructed for
/// real, driven through its REAL <see cref="AcpHostedAgentRuntimeFactory.StartAsync"/> — actually
/// wires the ACP interaction bridge into the runtime it produces, by observing an inbound
/// <c>session/request_permission</c> genuinely dispatch to the injected <c>requestInteraction</c>
/// delegate. Does NOT spawn a real <c>cursor-agent acp</c> process (unavailable/non-portable in
/// CI, `.github/workflows/ci.yml`'s `ubuntu-latest`/`windows-latest` matrix) — the factory's
/// process-spawning is swapped out via its <c>connectionSource</c> constructor seam for one backed
/// by <see cref="FakeAcpAgent"/>'s existing in-memory pipe streams, the same fake this project
/// already uses for <c>AcpHostedAgentRuntimeTests</c>/<c>AcpHostedAgentRuntimePermissionTests</c>.
/// A regression that left <c>StartAsync</c> passing <c>requestInteraction: null</c> (AI-684's
/// original default-decline posture) would make this test's <c>session/request_permission</c>
/// resolve with a JSON-RPC "Method not found" error instead of the well-formed <c>cancelled</c>
/// outcome asserted below — i.e. this test FAILS on that regression, unlike the pre-round-4 test it
/// replaces.
/// </summary>
public class AcpHostedAgentRuntimeFactoryTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    sealed class FakeAcpProcess : IAcpProcess {
        readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int  Pid       { get; init; } = 4242;
        public bool HasExited { get; private set; }
        public int? ExitCode  { get; private set; }
        public void SignalExited(int exitCode = 0) { HasExited = true; ExitCode = exitCode; _exited.TrySetResult(); }
        public Task WaitForExitAsync(TimeSpan? timeout = null) => _exited.Task;
        public Task TerminateAsync(TimeSpan? timeout = null) { SignalExited(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Records whether <see cref="ServerConnection.RequestAcpInteractionAsync"/> was actually
    /// invoked BY THE RUNTIME THE FACTORY PRODUCED (not by the test calling it directly) — a real
    /// (non-connecting) <see cref="ServerConnection"/> subclass, matching the established
    /// <c>CaptureServerConnection</c>-style pattern used elsewhere in this test project (e.g.
    /// <c>AgentOrchestratorVendorTests.cs</c>) rather than a mocking framework, since
    /// <see cref="ServerConnection"/> is not an interface.
    /// </summary>
    sealed class CaptureServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance
        ) {
        public bool RequestAcpInteractionAsyncCalled { get; private set; }
        public AcpInteractionRequest? LastRequest     { get; private set; }

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(AcpInteractionRequest request, CancellationToken ct = default) {
            RequestAcpInteractionAsyncCalled = true;
            LastRequest                      = request;

            return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
        }
    }

    static RuntimeStartContext MakeContext(string agentId) => new(
        AgentId: agentId, Vendor: "cursor", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/worktree", Branch: "branch-name", SourceRepo: "/repo"), Prompt: "",
        Model: "default", Effort: null, Tools: null,
        IsReview: false, IsReviewFlow: false, Review: null,
        Cols: 80, Rows: 24, ServerUrl: null, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap");

    [Test]
    public async Task StartAsync_WiresRequestInteractionDelegate_DispatchesInboundPermissionRequestToTheBridge() {
        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            config: new DaemonConfig { CursorPath = "cursor-agent" }, // never actually spawned — connectionSource below bypasses Process.Start
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(MakeContext("agent-1"), cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await started.Runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        // The factory-produced runtime dispatched the inbound session/request_permission to the
        // bridge, which called connection.RequestAcpInteractionAsync — proving requestInteraction
        // was genuinely wired (not left null) by StartAsync, observed through the REAL runtime the
        // REAL factory produced, not a direct delegate invocation.
        var deadline = DateTime.UtcNow + HangGuard;
        while (!connection.RequestAcpInteractionAsyncCalled && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsTrue();
        await Assert.That(connection.LastRequest?.AgentId).IsEqualTo("agent-1");
        await Assert.That(connection.LastRequest?.Kind).IsEqualTo("permission");

        var responseDeadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestResponse is null && DateTime.UtcNow < responseDeadline)
            await Task.Delay(10);

        // connection.RequestAcpInteractionAsync above returns a "cancel" decision — the bridge
        // (Task B3) must map that to the well-formed ACP "cancelled" outcome, proving the FULL
        // chain (factory → runtime → bridge → injected ServerConnection → back to the wire) works,
        // not just that SOME delegate got called.
        await Assert.That(fake.LastServerRequestResponse).IsNotNull();
        await Assert.That(fake.LastServerRequestResponse!.Value.GetProperty("outcome").GetProperty("outcome").GetString()).IsEqualTo("cancelled");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>
    /// AI-688 gap 1: <c>ctx.Model</c> (the launch's own model override) must take precedence over
    /// <c>DaemonConfig.CursorModel</c> (the daemon-wide family-prefix default) — proves the full
    /// chain (factory merges the two, runtime resolves against `session/new`'s `availableModels`,
    /// sends `session/set_config_option`) picks the PER-LAUNCH model, not the daemon default.
    /// </summary>
    [Test]
    public async Task StartAsync_CtxModelOverridesConfigCursorModel_AndSendsSetConfigOptionForIt() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(
            FakeAcpAgent.FixedSessionId,
            currentModelId: "composer-2.5[fast=true]",
            availableModels: [
                ("composer-2.5[fast=true]", "composer-2.5"),
                ("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
                ("claude-opus-4-8[thinking=true]", "claude-opus-4-8"),
            ]));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            config: new DaemonConfig { CursorPath = "cursor-agent", CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx = MakeContext("agent-1") with { Model = "claude-opus-4-8" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "session/set_config_option") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var setConfigCall = fake.ReceivedCalls.Single(c => c.Method == "session/set_config_option");
        await Assert.That(setConfigCall.Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-opus-4-8[thinking=true]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>
    /// Negative control proving this test suite WOULD catch the round-4 regression it replaces: a
    /// factory built with <c>connectionSource</c> returning a runtime whose <c>requestInteraction</c>
    /// is deliberately left <see langword="null"/> (simulating the pre-Finding-4 defect) answers the
    /// SAME inbound request with a JSON-RPC "Method not found" error, not a "cancelled" outcome —
    /// demonstrating this test file's assertions are sensitive to the exact bug Finding 4 fixed and
    /// Finding 3 makes verifiable end-to-end.
    /// </summary>
    [Test]
    public async Task StartAsync_IfRequestInteractionWereNull_PermissionRequestWouldGetMethodNotFound() {
        var fake = new FakeAcpAgent();
        var conn = new AcpConnection(fake.ClientWriteStream, fake.ClientReadStream, NullLogger.Instance);
        var runtime = new AcpHostedAgentRuntime(conn, new FakeAcpProcess(), NullLogger.Instance); // no requestInteraction — AI-684 default

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        await runtime.StartAsync("/abs/worktree", "", cts.Token).WaitAsync(HangGuard);

        fake.EnqueuePermissionRequestDuringNextPrompt(
            toolCallJson: """{"toolCallId":"call-1","title":"Run ls"}""",
            optionsJson: """[{"optionId":"allow-once","name":"Allow","kind":"allow_once"}]""");

        await runtime.SendUserInputAsync("run ls").WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.LastServerRequestError is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(fake.LastServerRequestError).IsNotNull();
        await Assert.That(fake.LastServerRequestError!.Value.GetProperty("code").GetInt32()).IsEqualTo(-32601);

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await runtime.DisposeAsync();
        await fake.DisposeAsync();
    }
}
