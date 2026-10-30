using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>A hosted agent's kcap MCP server answers its handshake without waiting on the hosting daemon.
/// Pi binds a hosted agent's session only after its extension's servers have answered, and gives each
/// 10 seconds: a server that asks the daemon for the session first waits on itself and is dropped.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class HostedMcpServerHandshakeTests {
    const string DaemonName = "hosting";

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    [Test]
    [Arguments("flows")]
    [Arguments("plans")]
    [Arguments("handoff")]
    public async Task Answers_initialize_while_the_daemon_holds_the_agents_session(string server) {
        await using var daemon = new FakeAgentSessionDaemon(Daemons.Store.SocketPath(DaemonName), [AgentSessionIpc.Capability], session: null);

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "mcp", server);
        psi.Environment["KCAP_URL"]                   = "http://127.0.0.1:9";
        psi.Environment["KCAP_DAEMON_NAME"]           = DaemonName;
        psi.Environment[HostedAgent.DaemonAgentIdVar] = "agent-7";
        // Any harness naming its own session would keep the server from asking the daemon at all.
        psi.Environment.Remove("CLAUDE_CODE_SESSION_ID");
        psi.Environment.Remove("CODEX_THREAD_ID");
        psi.Environment.Remove("GROK_SESSION_ID");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        try {
            await proc.StandardInput.WriteLineAsync(
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject() }.ToJsonString());
            await proc.StandardInput.FlushAsync();

            // Well under the daemon query's own 15-second budget, which is what a server asking it would wait out.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var line = await proc.StandardOutput.ReadLineAsync(cts.Token);

            await Assert.That(line).IsNotNull();
            await Assert.That(JsonNode.Parse(line!)?["result"]?["serverInfo"]?["name"]?.GetValue<string>()).IsEqualTo($"kcap-{server}");
        } finally {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await proc.WaitForExitAsync();
        }
    }
}
