using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Acp;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// gap 1 — GATED live end-to-end test that drives the REAL <see cref="AcpHostedAgentRuntimeFactory"/>
/// against a REAL <c>cursor-agent acp</c> child process (no <c>FakeAcpAgent</c>, no in-memory pipe —
/// see <see cref="AcpHostedAgentRuntimeFactoryTests"/> for that coverage of the same code path) to
/// prove that model selection (<c>session/set_config_option</c>, sent from
/// <c>ConfigOptionModelSelector.TrySelectAsync</c> before the first turn) and a real
/// <c>session/prompt</c> turn actually work end-to-end against the live Cursor CLI at the daemon
/// level: real process spawn (<see cref="AcpHostedAgentRuntimeFactory"/>'s default
/// <c>connectionSource</c>, i.e. <c>connectionSource: null</c>) → real stdio JSON-RPC → real
/// <c>initialize</c>/<c>session/new</c>/<c>session/set_config_option</c>/<c>session/prompt</c>
/// handshake → real <c>session/update</c> notifications reduced by
/// <see cref="AcpHostedAgentRuntime"/> into <see cref="AcpSessionUpdate"/>.
///
/// <b>Gated</b> behind <c>KCAP_ACP_LIVE=1</c> so CI (no <c>cursor-agent</c> binary, no Cursor
/// account) never runs this, and no ordinary local test run silently spends a real Cursor turn.
/// Requires: <c>cursor-agent</c> on PATH, authenticated, Team-tier (or higher) subscription — see
/// <c>docs/ai-688-cursor-prototype-findings.md</c>'s "Free tier" plan-limit gotcha (on Free, every
/// model just returns "Upgrade your plan to continue" and no real turn runs, which would make this
/// test fail even though the daemon code path is correct).
/// </summary>
public class AcpHostedAgentRuntimeFactoryLiveTests {
    const string LiveGateEnvVar = "KCAP_ACP_LIVE";

    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan LiveTurnTimeout  = TimeSpan.FromSeconds(40);

    /// <summary>
    /// A real (non-connecting) <see cref="ServerConnection"/> subclass, matching this project's
    /// established <c>CaptureServerConnection</c>-style pattern (see
    /// <see cref="AcpHostedAgentRuntimeFactoryTests"/>) — not a mocking framework, since
    /// <see cref="ServerConnection"/> is not an interface. The HELLO-only prompt this test sends
    /// exercises no tool calls, so <see cref="RequestAcpInteractionAsync"/> is not expected to fire;
    /// it is still wired to a well-formed "cancel" response (rather than throwing) so an unexpected
    /// inbound request degrades gracefully instead of crashing the whole run.
    /// </summary>
    sealed class CaptureServerConnection() : ServerConnection(
            new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
            NullLoggerFactory.Instance,
            NullLogger<ServerConnection>.Instance
        ) {
        public bool RequestAcpInteractionAsyncCalled { get; private set; }

        public override Task<AcpInteractionDecision> RequestAcpInteractionAsync(AcpInteractionRequest request, CancellationToken ct = default) {
            RequestAcpInteractionAsyncCalled = true;
            Console.WriteLine($"[ai-688-live] UNEXPECTED RequestAcpInteractionAsync: kind={request.Kind} tool={request.ToolName}");

            return Task.FromResult(new AcpInteractionDecision("cancel", null, null, null, null, null));
        }
    }

    sealed class CaptureLoggerProvider : ILoggerProvider {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }

        sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NoopScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }

        sealed class NoopScope : IDisposable {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
        }
    }

    [Test]
    public async Task StartAsync_AgainstRealCursorAgentAcp_SelectsModelAndProducesHelloTurn() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'cursor-agent acp' process — set {LiveGateEnvVar}=1 to run " +
            "(spends a real Cursor turn; requires an authenticated Team-tier `cursor-agent` on PATH).");

        var worktreeDir = Directory.CreateTempSubdirectory("kcap-acp-live-");

        // A real (console) logger factory rather than NullLoggerFactory — AcpHostedAgentRuntime logs
        // a warning if the requested model can't be resolved against session/new's availableModels,
        // or if session/set_config_option itself fails (both non-fatal — see
        // ConfigOptionModelSelector.TrySelectAsync's remarks) — so a real logger is the only way this test can surface those failures instead
        // of silently swallowing them.
        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        try {
            var connection = new CaptureServerConnection();

            // config.CursorModel stays at its DaemonConfig default ("claude-sonnet-4-5") — ctx.Model
            // below is "" so AcpHostedAgentRuntimeFactory.ResolveRequestedModel falls back to it,
            // proving the daemon-wide default reaches a real cursor-agent process, not just the fake.
            var factory = new AcpHostedAgentRuntimeFactory(
                descriptor: AcpVendorDescriptors.Cursor,
                config: new DaemonConfig(), // CursorPath="cursor-agent", CursorModel="claude-sonnet-4-5"
                loggerFactory: liveLoggerFactory,
                connection: connection,
                connectionSource: null // real cursor-agent acp spawn — gap 1's production path
            );

            var ctx = new RuntimeStartContext(
                AgentId: "ai-688-live-gap1",
                Vendor: "cursor",
                SourceRepoPath: worktreeDir.FullName,
                Worktree: WorktreeInfo.Borrowed(worktreeDir.FullName),
                Prompt: "Respond with only the single word HELLO and nothing else.",
                Model: "", // falls back to DaemonConfig.CursorModel
                Effort: null,
                Tools: null,
                IsReview: false,
                IsReviewFlow: false,
                Review: null,
                Cols: 80,
                Rows: 24,
                ServerUrl: null,
                DaemonBridgeUrl: null,
                CapacitorPath: "/usr/local/bin/kcap");

            using var startCts = new CancellationTokenSource();

            var started = await factory.StartAsync(ctx, startCts.Token).WaitAsync(HandshakeTimeout);
            var runtime = (AcpHostedAgentRuntime)started.Runtime;

            try {
                var result = await CollectUntilHelloAsync(runtime.Updates, LiveTurnTimeout);

                Console.WriteLine($"[ai-688-live] observed {result.Updates.Count} session/update notification(s):");
                foreach (var update in result.Updates)
                    Console.WriteLine($"[ai-688-live]   kind={update.Kind} text={update.Text} raw={update.Raw?.GetRawText()}");
                Console.WriteLine($"[ai-688-live] concatenated agent_message_chunk text: \"{result.ConcatenatedText}\"");

                await Assert.That(result.SawHello).IsTrue();
            } finally {
                startCts.Cancel();
                await runtime.DisposeAsync();
            }
        } finally {
            try { worktreeDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>AI-1408 go/no-go: the real Cursor ACP process must load the review-flow result
    /// server delivered in <c>session/new.mcpServers</c>, call its submission tool, and keep every
    /// permission decision inside the unattended ACP bridge rather than routing one to the UI.</summary>
    [Test]
    public async Task StartAsync_AgainstRealCursorAgentAcp_LoadsFlowResultMcpWithoutHumanInteraction() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'cursor-agent acp' review-flow turn — set {LiveGateEnvVar}=1 to run " +
            "(spends a real Cursor turn; requires an authenticated Team-tier `cursor-agent` and `kcap` on PATH).");

        var worktreeDir = Directory.CreateTempSubdirectory("kcap-acp-live-review-");
        using var captureLoggerProvider = new CaptureLoggerProvider();

        using var liveLoggerFactory = LoggerFactory.Create(b => b
            .AddProvider(captureLoggerProvider)
            .AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; })
            .SetMinimumLevel(LogLevel.Debug));

        try {
            var connection = new CaptureServerConnection();
            var factory = new AcpHostedAgentRuntimeFactory(
                descriptor: AcpVendorDescriptors.Cursor,
                config: new DaemonConfig(),
                loggerFactory: liveLoggerFactory,
                connection: connection,
                connectionSource: null);

            var ctx = new RuntimeStartContext(
                AgentId: "ai-1408-live-review",
                Vendor: "cursor",
                SourceRepoPath: worktreeDir.FullName,
                Worktree: new WorktreeInfo(worktreeDir.FullName, "ai-1408-live-review", worktreeDir.FullName),
                Prompt: "Call the submit_review_result MCP tool now with round_token `ai-1408-live`, kind `clean`, and an empty findings array. Do not merely describe the call.",
                Model: "",
                Effort: null,
                Tools: null,
                IsReview: false,
                IsReviewFlow: true,
                Review: null,
                Cols: 80,
                Rows: 24,
                ServerUrl: "http://127.0.0.1:1",
                DaemonBridgeUrl: null,
                CapacitorPath: "kcap");

            using var startCts = new CancellationTokenSource();
            var started = await factory.StartAsync(ctx, startCts.Token).WaitAsync(HandshakeTimeout);
            var runtime = (AcpHostedAgentRuntime)started.Runtime;

            try {
                var updates = await CollectUntilToolCompletionAsync(runtime.Updates, LiveTurnTimeout);
                foreach (var update in updates)
                    Console.WriteLine($"[ai-1408-live] kind={update.Kind} title={update.ToolTitle} raw={update.Raw?.GetRawText()}");

                await Assert.That(updates.Any(u => u.Kind == AcpUpdateKind.ToolCall)).IsTrue();
                await Assert.That(captureLoggerProvider.Messages.Any(message =>
                    message.Contains("ACP unattended review-flow: auto-approved", StringComparison.Ordinal) &&
                    message.Contains("kcap-flow-result-submit_review_result", StringComparison.Ordinal))).IsTrue();
                await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();
            } finally {
                startCts.Cancel();
                await runtime.DisposeAsync();
            }
        } finally {
            try { worktreeDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    sealed record HelloCollectionResult(bool SawHello, string ConcatenatedText, List<AcpSessionUpdate> Updates);

    /// <summary>
    /// Drains <paramref name="updates"/> until an <c>agent_message_chunk</c> (concatenated across
    /// however many chunks Cursor streams the answer in — the probe observed the reply
    /// arriving split across multiple chunks) contains "HELLO" (case-insensitive), or
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    static async Task<HelloCollectionResult> CollectUntilHelloAsync(ChannelReader<AcpSessionUpdate> updates, TimeSpan timeout) {
        var collected   = new List<AcpSessionUpdate>();
        var textBuffer  = new StringBuilder();

        using var timeoutCts = new CancellationTokenSource(timeout);

        try {
            while (await updates.WaitToReadAsync(timeoutCts.Token)) {
                while (updates.TryRead(out var update)) {
                    collected.Add(update);

                    if (update.Kind == AcpUpdateKind.AgentMessageChunk && update.Text is { Length: > 0 } text) {
                        textBuffer.Append(text);

                        if (textBuffer.ToString().Contains("HELLO", StringComparison.OrdinalIgnoreCase))
                            return new HelloCollectionResult(true, textBuffer.ToString(), collected);
                    }
                }
            }
        } catch (OperationCanceledException) {
            // Timed out waiting for the turn to produce HELLO — fall through and report what we saw
            // so the caller can log the observed updates either way.
        }

        return new HelloCollectionResult(false, textBuffer.ToString(), collected);
    }

    static async Task<List<AcpSessionUpdate>> CollectUntilToolCompletionAsync(
            ChannelReader<AcpSessionUpdate> updates, TimeSpan timeout) {
        var collected = new List<AcpSessionUpdate>();
        using var timeoutCts = new CancellationTokenSource(timeout);

        try {
            while (await updates.WaitToReadAsync(timeoutCts.Token)) {
                while (updates.TryRead(out var update)) {
                    collected.Add(update);
                    if (update.Kind == AcpUpdateKind.ToolCallUpdate &&
                        string.Equals(update.ToolStatus, "completed", StringComparison.OrdinalIgnoreCase))
                        return collected;
                }
            }
        } catch (OperationCanceledException) {
            // Return the observed frames so the assertion and test log show what Cursor did.
        }

        return collected;
    }
}
