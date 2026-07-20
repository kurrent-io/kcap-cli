using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Acp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// Round-4 review finding 3: proves <see cref="AcpHostedAgentRuntimeFactory"/> — constructed for
/// real, driven through its REAL <see cref="AcpHostedAgentRuntimeFactory.StartAsync"/> — actually
/// wires the ACP interaction bridge into the runtime it produces, by observing an inbound
/// <c>session/request_permission</c> genuinely dispatch to the injected <c>requestInteraction</c>
/// delegate. Does NOT spawn a real <c>cursor-agent acp</c> process (unavailable/non-portable in
/// CI, `.github/workflows/ci.yml`'s `ubuntu-latest`/`windows-latest` matrix) — the factory's
/// process-spawning is swapped out via its <c>connectionSource</c> constructor seam for one backed
/// by <see cref="FakeAcpAgent"/>'s existing in-memory pipe streams, the same fake this project
/// already uses for <c>AcpHostedAgentRuntimeTests</c>/<c>AcpHostedAgentRuntimePermissionTests</c>.
/// A regression that left <c>StartAsync</c> passing <c>requestInteraction: null</c> (reverting to
/// the runtime's original default-decline posture) would make this test's <c>session/request_permission</c>
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
            descriptor: AcpVendorDescriptors.Cursor,
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
    /// Model-precedence gap: <c>ctx.Model</c> (the launch's own model override) must take precedence over
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
            descriptor: AcpVendorDescriptors.Cursor,
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
        var runtime = new AcpHostedAgentRuntime(conn, new FakeAcpProcess(), NullLogger.Instance); // no requestInteraction — default behavior

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

    // ── Payload-free "ACP hosted agent launch" Info logging ────────────────────────────────────

    /// <summary>Records every log call across every category (one instance shared by every
    /// <c>CreateLogger&lt;T&gt;()</c> call) — mirrors <c>AcpTranscriptAggregationTests.CaptureLogger</c>'s
    /// established pattern, wrapped in a minimal <see cref="ILoggerFactory"/> so it can be handed to
    /// the factory's real constructor.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    sealed class CaptureLoggerFactory : ILoggerFactory {
        public readonly CaptureLogger Logger = new();

        public ILogger CreateLogger(string categoryName) => Logger;
        public void    AddProvider(ILoggerProvider provider) { }
        public void    Dispose() { }
    }

    [Test]
    public async Task StartAsync_LogsAcpHostedAgentLaunch_WithAgentIdVendorAndCwd() {
        var fake           = new FakeAcpAgent();
        var connection     = new CaptureServerConnection();
        var loggerFactory  = new CaptureLoggerFactory();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: loggerFactory,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-launch-log") with { Worktree = new WorktreeInfo(Path: "/abs/some-worktree", Branch: "b", SourceRepo: "/repo") };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var infoEntries = loggerFactory.Logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e =>
            e.Message.Contains("ACP hosted agent launch")
            && e.Message.Contains("agent-launch-log")
            && e.Message.Contains("cursor")
            && e.Message.Contains("/abs/some-worktree"));

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Test plan item 1: Cursor pin test (spec-review Finding 4) ──────────────────────────

    /// <summary>
    /// (a) proves the actual PRODUCTION <see cref="AcpHostedAgentRuntimeFactory.BuildProcessStartInfo"/>
    /// shape for the real Cursor descriptor — no <c>connectionSource</c>, no fake, no
    /// <c>StartAsync</c> — this is the only place in the suite that can observe it, since every
    /// other test replaces process-spawning entirely via <c>connectionSource</c>. (b) drives a full
    /// <c>StartAsync</c> against <see cref="FakeAcpAgent"/> and asserts the exact
    /// <c>initialize</c>/<c>session/new</c> frames are byte-identical to today's — in particular
    /// <c>session/new</c>'s <c>mcpServers</c> is <c>[]</c> (an empty array, not omitted, not
    /// populated) when <c>ctx.McpServers</c> is left at its default <see langword="null"/>. Together
    /// these are the primary regression guard for the whole refactor.
    /// </summary>
    [Test]
    public async Task StartAsync_ForCursorDescriptor_SpawnsExactSameArgvAndHandshakeAsBeforeAI1401() {
        // (a) — pure BuildProcessStartInfo assertion.
        var config = new DaemonConfig { CursorPath = "/usr/local/bin/cursor-agent" };
        var ctx    = MakeContext("agent-1");

        var psi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(AcpVendorDescriptors.Cursor, config, ctx);

        await Assert.That(psi.FileName).IsEqualTo(config.CursorPath);
        await Assert.That(psi.ArgumentList.SequenceEqual(["acp"])).IsTrue();
        await Assert.That(psi.WorkingDirectory).IsEqualTo(ctx.Worktree.Path);

        // (b) — full StartAsync against FakeAcpAgent; assert the exact initialize/session/new frames.
        var fake       = new FakeAcpAgent();
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorPath = "cursor-agent" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count(c => c.Method is "initialize" or "session/new") < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var initializeCall = fake.ReceivedCalls.Single(c => c.Method == "initialize");
        await Assert.That(initializeCall.Params!.Value.GetProperty("protocolVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(initializeCall.Params!.Value.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean()).IsFalse();
        await Assert.That(initializeCall.Params!.Value.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean()).IsFalse();

        var sessionNewCall = fake.ReceivedCalls.Single(c => c.Method == "session/new");
        await Assert.That(sessionNewCall.Params!.Value.GetProperty("cwd").GetString()).IsEqualTo(ctx.Worktree.Path);
        await Assert.That(sessionNewCall.Params!.Value.GetProperty("mcpServers").GetRawText()).IsEqualTo("[]");

        await Assert.That(started.Runtime.Vendor).IsEqualTo("cursor");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Test plan item 5: descriptor-driven spawn args via BuildProcessStartInfo ──────────

    /// <summary>Second, test-only descriptor — not <see cref="AcpVendorDescriptors.Cursor"/> — used
    /// by test plan items 5 and 6. <c>SupportsMcpServers</c> is parameterized since item 6(c) needs
    /// it <see langword="false"/> while items 6(a)/6(b) need it <see langword="true"/>; every other
    /// field is identical across both.</summary>
    static AcpVendorDescriptor SyntheticDescriptor(bool supportsMcpServers) => new(
        Vendor:              "test-acp-vendor",
        ResolveBinaryPath:   _ => "test-acp-vendor-cli",
        ResolveDefaultModel: _ => null,
        Argv:                ["acp", "--flag-a"],
        UnattendedTrustArgv: ["--trust"],
        SupportsUnattended:  true,
        ModelSelector:       NoOpModelSelector.Instance,
        SupportsMcpServers:  supportsMcpServers
    );

    /// <summary>
    /// Exercises the trust-argv seam against the REAL production builder — no REAL descriptor sets
    /// <c>SupportsUnattended: true</c> yet (Cursor's stays <c>false</c> — see
    /// <c>UnattendedLaunchPolicyTests.Cursor_descriptor_unattended_launch_is_rejected</c>), so this
    /// proves the MECHANISM works ahead of the vendor that will eventually use it.
    /// </summary>
    [Test]
    public async Task BuildProcessStartInfo_DescriptorDriven_AppendsTrustArgvOnlyForReviewFlow() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: false);
        var config     = new DaemonConfig();

        var interactivePsi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = false });
        var reviewFlowPsi = AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = true });

        await Assert.That(interactivePsi.ArgumentList.SequenceEqual(["acp", "--flag-a"])).IsTrue();
        await Assert.That(reviewFlowPsi.ArgumentList.SequenceEqual(["acp", "--flag-a", "--trust"])).IsTrue();
    }

    /// <summary>Qodo finding 3: defense-in-depth — even though the orchestrator's
    /// <c>UnattendedLaunchPolicy</c> is expected to reject a review-flow launch for a vendor that
    /// doesn't support it before the factory ever runs, <c>BuildProcessStartInfo</c> refuses to
    /// build review-flow argv for a <c>SupportsUnattended: false</c> descriptor rather than
    /// trusting that gate alone.</summary>
    [Test]
    public async Task BuildProcessStartInfo_Throws_ForReviewFlow_WhenDescriptorDoesNotSupportUnattended() {
        var descriptor = AcpVendorDescriptors.Cursor; // SupportsUnattended: false
        var config     = new DaemonConfig();

        await Assert.That(() => AcpHostedAgentRuntimeFactory.BuildProcessStartInfo(
            descriptor, config, MakeContext("agent-1") with { IsReviewFlow = true }
        )).Throws<InvalidOperationException>();
    }

    // ── Test plan item 6: mcpServers gating and wire shape ─────────────────────────────────

    static async Task<HostedRuntimeStart> RunSyntheticStartAsync(
            AcpVendorDescriptor descriptor, FakeAcpAgent fake, RuntimeStartContext ctx, CancellationToken ct) {
        var connection = new CaptureServerConnection();
        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: descriptor,
            config: new DaemonConfig(),
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        return await factory.StartAsync(ctx, ct).WaitAsync(HangGuard, ct);
    }

    static async Task<string> WaitForSessionNewMcpServersJsonAsync(FakeAcpAgent fake) {
        var deadline = DateTime.UtcNow + HangGuard;
        while (!fake.ReceivedCalls.Any(c => c.Method == "session/new") && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return fake.ReceivedCalls.Single(c => c.Method == "session/new").Params!.Value.GetProperty("mcpServers").GetRawText();
    }

    [Test]
    public async Task StartAsync_SupportsMcpServersTrue_PopulatedContext_ForwardsServerVerbatim() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "fs", Command: "npx",
                Args: ["-y", "@modelcontextprotocol/server-filesystem"],
                Env: [new AcpMcpServerEnvVar("FOO", "bar")])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started = await RunSyntheticStartAsync(descriptor, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo(
            """[{"name":"fs","command":"npx","args":["-y","@modelcontextprotocol/server-filesystem"],"env":[{"name":"FOO","value":"bar"}]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>The exact regression Finding 1 flagged: an empty <c>Env</c> must still serialize as
    /// <c>"env":[]</c>, NOT an omitted key and NOT <c>"env":null</c>.</summary>
    [Test]
    public async Task StartAsync_SupportsMcpServersTrue_EmptyEnv_SerializesAsEmptyArray_NotOmittedNotNull() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: true);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "fs", Command: "npx", Args: ["-y", "server-filesystem"], Env: [])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started = await RunSyntheticStartAsync(descriptor, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo("""[{"name":"fs","command":"npx","args":["-y","server-filesystem"],"env":[]}]""");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>Proves the DESCRIPTOR flag — not just an unpopulated context — is what gates
    /// forwarding: even with a populated <c>ctx.McpServers</c>, <c>SupportsMcpServers: false</c>
    /// still sends <c>mcpServers: []</c> on the wire.</summary>
    [Test]
    public async Task StartAsync_SupportsMcpServersFalse_PopulatedContext_StillSendsEmptyArray() {
        var descriptor = SyntheticDescriptor(supportsMcpServers: false);
        var fake        = new FakeAcpAgent();

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        AcpMcpServerSpec[] mcpServers = [
            new AcpMcpServerSpec(Name: "fs", Command: "npx",
                Args: ["-y", "@modelcontextprotocol/server-filesystem"],
                Env: [new AcpMcpServerEnvVar("FOO", "bar")])
        ];
        var ctx = MakeContext("agent-1") with { McpServers = mcpServers };

        var started = await RunSyntheticStartAsync(descriptor, fake, ctx, cts.Token);
        var mcpServersJson = await WaitForSessionNewMcpServersJsonAsync(fake);

        await Assert.That(mcpServersJson).IsEqualTo("[]");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    // ── Test plan item 11: factory-selector integration + frame ordering ──────────────────

    static async Task<IReadOnlyList<(string Method, System.Text.Json.JsonElement? Params)>> WaitForCallCountAsync(FakeAcpAgent fake, int minCount) {
        var deadline = DateTime.UtcNow + HangGuard;
        while (fake.ReceivedCalls.Count < minCount && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        return fake.ReceivedCalls;
    }

    static readonly (string ModelId, string Name)[] TeamAvailableModels = [
        ("default[]", "default"),
        ("composer-2.5[fast=true]", "composer-2.5"),
        ("claude-sonnet-4-5[thinking=true,context=200k]", "claude-sonnet-4-5"),
        ("claude-opus-4-8[thinking=true]", "claude-opus-4-8"),
    ];

    /// <summary>(a) An explicit <c>ctx.Model</c> that resolves → order is <c>initialize</c>,
    /// <c>session/new</c>, <c>session/set_config_option</c>, <c>session/prompt</c>, and the
    /// started runtime's <c>Vendor == "cursor"</c>.</summary>
    [Test]
    public async Task StartAsync_ExplicitResolvableModel_FrameOrderIsInitializeNewSetConfigPrompt() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "claude-opus-4-8", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[2].Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-opus-4-8[thinking=true]");
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");
        await Assert.That(started.Runtime.Vendor).IsEqualTo("cursor");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>(b) <c>ctx.Model: "default"</c> (the UI's "no override" sentinel) → resolves to
    /// <c>config.CursorModel</c> against <c>session/new</c>'s <c>availableModels</c>, same
    /// four-call order — the sentinel still resolves TO a model (the configured default), not a
    /// caller override.</summary>
    [Test]
    public async Task StartAsync_DefaultSentinelModel_ResolvesToConfigCursorModel_FrameOrderIsInitializeNewSetConfigPrompt() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "default", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[2].Params!.Value.GetProperty("value").GetString()).IsEqualTo("claude-sonnet-4-5[thinking=true,context=200k]");
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>(c) A requested model NOT present in <c>availableModels</c> → order is
    /// <c>initialize</c>, <c>session/new</c>, <c>session/prompt</c> only — NO
    /// <c>session/set_config_option</c> call, proving an unresolvable model never even attempts
    /// the RPC.</summary>
    [Test]
    public async Task StartAsync_UnresolvableModel_FrameOrderSkipsSetConfigOption() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "totally-unknown-model", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 3);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(3);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/prompt");
        await Assert.That(calls.Any(c => c.Method == "session/set_config_option")).IsFalse();

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }

    /// <summary>(d) A resolvable model but <c>fake.FailNextSetConfigOption()</c> → the full
    /// four-call order (the RPC IS attempted and fails) and <c>session/prompt</c> still fires
    /// afterward with no exception — the integration-level counterpart to test plan item 10, now
    /// proving the FACTORY-produced runtime (not a hand-built one) behaves the same way.</summary>
    [Test]
    public async Task StartAsync_SetConfigOptionRpcError_FrameOrderStillReachesPrompt_NoException() {
        var fake = new FakeAcpAgent();
        fake.SetSessionNewResult(FakeAcpAgent.BuildSessionNewResult(FakeAcpAgent.FixedSessionId, currentModelId: "composer-2.5[fast=true]", TeamAvailableModels));
        fake.FailNextSetConfigOption();
        var connection = new CaptureServerConnection();

        var factory = new AcpHostedAgentRuntimeFactory(
            descriptor: AcpVendorDescriptors.Cursor,
            config: new DaemonConfig { CursorModel = "claude-sonnet-4-5" },
            loggerFactory: NullLoggerFactory.Instance,
            connection: connection,
            connectionSource: _ => (fake.ClientWriteStream, fake.ClientReadStream, new FakeAcpProcess())
        );

        using var cts = new CancellationTokenSource();
        var fakeRunTask = fake.RunAsync(cts.Token);

        var ctx     = MakeContext("agent-1") with { Model = "claude-opus-4-8", Prompt = "do the thing" };
        var started = await factory.StartAsync(ctx, cts.Token).WaitAsync(HangGuard);

        var calls = await WaitForCallCountAsync(fake, minCount: 4);
        await Assert.That(calls.Count).IsGreaterThanOrEqualTo(4);
        await Assert.That(calls[0].Method).IsEqualTo("initialize");
        await Assert.That(calls[1].Method).IsEqualTo("session/new");
        await Assert.That(calls[2].Method).IsEqualTo("session/set_config_option");
        await Assert.That(calls[3].Method).IsEqualTo("session/prompt");

        cts.Cancel();
        try { await fakeRunTask.WaitAsync(HangGuard); } catch (OperationCanceledException) { }
        await started.Runtime.DisposeAsync();
        await fake.DisposeAsync();
    }
}
