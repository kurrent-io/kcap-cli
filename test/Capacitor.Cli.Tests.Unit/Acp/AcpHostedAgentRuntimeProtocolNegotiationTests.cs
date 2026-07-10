// test/Capacitor.Cli.Tests.Unit/Acp/AcpHostedAgentRuntimeProtocolNegotiationTests.cs
using System.Text.Json;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
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

        public Harness(ILogger? logger = null, string agentId = "") {
            Fake    = new FakeAcpAgent();
            Conn    = new AcpConnection(Fake.ClientWriteStream, Fake.ClientReadStream, logger ?? NullLogger.Instance);
            Process = new FakeAcpProcess();
            Runtime = new AcpHostedAgentRuntime(Conn, Process, logger ?? NullLogger.Instance, agentId: agentId);
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
        // A protocol-version mismatch is NOT an auth issue — it must not carry the auth/subscription hint.
        await Assert.That(ex.Message).DoesNotContain("cursor-agent login");
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

    [Test]
    public async Task StartAsync_MalformedInitializeResult_ThrowsClearVersionError_WithoutAuthHint() {
        await using var h = new Harness();
        // A wrong-typed protocolVersion makes the defensive parse throw JsonException internally; it
        // must fall back to negotiated version 0 (rejected with the clear version error) rather than
        // surfacing a raw JsonException — and, being a version problem, carry no auth hint.
        using var doc = JsonDocument.Parse("""{"protocolVersion":"not-a-number","agentCapabilities":{"loadSession":true}}""");
        h.Fake.SetInitializeResult(doc.RootElement.Clone());
        h.StartFakeAgentLoop();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard));

        await Assert.That(ex!.Message).Contains("malformed");   // reported as a parse failure, not "negotiated version 0"
        await Assert.That(ex.Message).Contains("version 1");
        await Assert.That(ex.Message).DoesNotContain("version 0");
        await Assert.That(ex.Message).DoesNotContain("cursor-agent login");
    }

    // ── Payload-free handshake/session-lifecycle Info logging ──────────────────────────────────

    /// <summary>Records every log call — mirrors <c>AcpTranscriptAggregationTests.CaptureLogger</c>'s
    /// established pattern.</summary>
    sealed class CaptureLogger : ILogger {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool         IsEnabled(LogLevel logLevel)                            => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Entries.Add((level, formatter(state, ex)));
    }

    [Test]
    public async Task StartAsync_Success_LogsSessionStartedAndHandshakeOk_NeverThePromptText() {
        var logger = new CaptureLogger();
        await using var h = new Harness(logger, agentId: "agent-42");
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 1, loadSession: true));
        h.StartFakeAgentLoop();

        const string secretPrompt = "do the super-secret prompt thing";
        await h.Runtime.StartAsync("/abs/worktree", secretPrompt, h.Cts.Token).WaitAsync(HangGuard);

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();

        await Assert.That(infoEntries).Contains(e =>
            e.Message.Contains("session started") && e.Message.Contains("agent-42"));

        await Assert.That(infoEntries).Contains(e =>
            e.Message.Contains("handshake OK")
            && e.Message.Contains("protocolVersion")
            && e.Message.Contains("loadSession=True")
            && e.Message.Contains("agent-42"));

        // Payload-free: the prompt text must never appear in any Info-level log line.
        await Assert.That(infoEntries).DoesNotContain(e => e.Message.Contains(secretPrompt));
    }

    [Test]
    public async Task StartAsync_ProtocolVersionMismatch_NeverLogsSessionStartedOrHandshakeOk() {
        var logger = new CaptureLogger();
        await using var h = new Harness(logger);
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 2, loadSession: true));
        h.StartFakeAgentLoop();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard));

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).DoesNotContain(e => e.Message.Contains("session started"));
        await Assert.That(infoEntries).DoesNotContain(e => e.Message.Contains("handshake OK"));
    }

    [Test]
    public async Task DisposeAsync_AfterSuccessfulStart_LogsSessionEnded() {
        var logger = new CaptureLogger();
        var h = new Harness(logger, agentId: "agent-7");
        h.Fake.SetInitializeResult(FakeAcpAgent.BuildInitializeResult(protocolVersion: 1, loadSession: false));
        h.StartFakeAgentLoop();

        await h.Runtime.StartAsync("/abs/worktree", "do the thing", h.Cts.Token).WaitAsync(HangGuard);
        await h.DisposeAsync();

        var infoEntries = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        await Assert.That(infoEntries).Contains(e =>
            e.Message.Contains("session ended") && e.Message.Contains("agent-7"));
    }
}
