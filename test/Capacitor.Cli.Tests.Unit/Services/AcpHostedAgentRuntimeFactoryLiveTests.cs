using System.Text;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
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

    /// <summary>Borrowed-snapshot certification probe: Cursor runs in a daemon-owned copy of the
    /// authorized checkout. Even an explicit mutation changes only that disposable snapshot; the
    /// source checkout remains byte-identical and the result MCP completes with zero interaction.</summary>
    [Test]
    public async Task ReviewFlow_AgainstRealCursorAgentAcp_CallsResultMcp_WithZeroInteractionRequests() {
        Skip.Unless(
            Environment.GetEnvironmentVariable(LiveGateEnvVar) == "1",
            $"Gated live E2E against a real 'cursor-agent acp' process — set {LiveGateEnvVar}=1 to run " +
            "(spends a real Cursor turn; requires an authenticated Cursor subscription).");
        Skip.When(OperatingSystem.IsWindows(), "The gated probe's tiny stdio MCP fixture is a POSIX executable script.");

        var rootDir     = Directory.CreateTempSubdirectory("kcap-acp-review-live-");
        var sourceDir   = Directory.CreateDirectory(Path.Combine(rootDir.FullName, "borrowed-source"));
        var worktreeDir = Directory.CreateDirectory(Path.Combine(rootDir.FullName, "owned-snapshot"));
        var markerPath  = Path.Combine(rootDir.FullName, "result-called");
        var mcpPath     = Path.Combine(rootDir.FullName, "fake-kcap");
        var protectedPath = Path.Combine(sourceDir.FullName, "protected.txt");
        var snapshotPath  = Path.Combine(worktreeDir.FullName, "protected.txt");
        File.WriteAllText(protectedPath, "ORIGINAL\n");
        File.Copy(protectedPath, snapshotPath);
        File.WriteAllText(mcpPath, FakeFlowResultMcpScript);
        File.SetUnixFileMode(mcpPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var liveLoggerFactory = LoggerFactory.Create(b => b
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
                AgentId: markerPath,
                Vendor: "cursor",
                SourceRepoPath: sourceDir.FullName,
                Worktree: new WorktreeInfo(worktreeDir.FullName, "snapshot", sourceDir.FullName),
                Prompt: "Read protected.txt. Try to replace it with MUTATED using any file-edit or shell tool available, but do not work around unavailable tools. Then call submit_review_result exactly once with verdict CLEAN and summary 'live borrowed certification'.",
                Model: "",
                Effort: null,
                Tools: null,
                IsReview: false,
                IsReviewFlow: true,
                Review: null,
                Cols: 80,
                Rows: 24,
                ServerUrl: "http://kcap.test",
                DaemonBridgeUrl: null,
                CapacitorPath: mcpPath,
                Work: WorkLocation.OwnedWorktree);

            using var startCts = new CancellationTokenSource();
            var started = await factory.StartAsync(ctx, startCts.Token).WaitAsync(HandshakeTimeout);
            var runtime = (AcpHostedAgentRuntime)started.Runtime;

            try {
                var deadline = DateTime.UtcNow + LiveTurnTimeout;
                while (!File.Exists(markerPath) && !runtime.HasExited && DateTime.UtcNow < deadline)
                    await Task.Delay(100);

                await Assert.That(File.Exists(markerPath)).IsTrue();
                await Assert.That(connection.RequestAcpInteractionAsyncCalled).IsFalse();
                await Assert.That(runtime.HasExited).IsFalse();
                await Assert.That(File.ReadAllText(protectedPath)).IsEqualTo("ORIGINAL\n");
                await Assert.That(File.ReadAllText(snapshotPath)).StartsWith("MUTATED");
            } finally {
                startCts.Cancel();
                await runtime.DisposeAsync();
            }
        } finally {
            try { rootDir.Delete(recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    internal const string FakeFlowResultMcpScript = """
#!/usr/bin/env python3
import json
import os
import sys

def send(message):
    print(json.dumps(message, separators=(",", ":")), flush=True)

for line in sys.stdin:
    try:
        request = json.loads(line)
        method = request.get("method")
        request_id = request.get("id")
        if request_id is None:
            continue
        if method == "initialize":
            send({"jsonrpc":"2.0","id":request_id,"result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}},"serverInfo":{"name":"live-flow-result","version":"1"}}})
        elif method == "tools/list":
            send({"jsonrpc":"2.0","id":request_id,"result":{"tools":[{"name":"submit_review_result","description":"Submit the final review result","inputSchema":{"type":"object","properties":{"verdict":{"type":"string","enum":["CLEAN","FINDINGS"]},"summary":{"type":"string"},"findings":{"type":"array"}},"required":["verdict","summary"]}}]}})
        elif method == "tools/call":
            with open(os.environ["KCAP_FLOW_AGENT_ID"], "w", encoding="utf-8") as marker:
                marker.write(json.dumps(request.get("params", {})))
            send({"jsonrpc":"2.0","id":request_id,"result":{"content":[{"type":"text","text":"review result accepted"}]}})
        else:
            send({"jsonrpc":"2.0","id":request_id,"error":{"code":-32601,"message":"Method not found"}})
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
""";

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
}
