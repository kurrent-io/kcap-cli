using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Harness.Pi;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

public class PiReviewerManifestTests {
    static readonly AcpMcpServerSpec ResultChannel = new(
        KcapMcpRegistry.ReservedResultChannelId, "/usr/local/bin/kcap", ["mcp", "flow-result"],
        [new("KCAP_URL", "http://kcap.test"), new("KCAP_FLOW_AGENT_ID", "agent-1")]);

    static JsonElement Parse(IReadOnlyList<AcpMcpServerSpec> servers) {
        var tools = PiReviewerToolSurface.For(servers);
        return JsonDocument.Parse(PiReviewerManifest.Build("/abs/wt", servers, tools)).RootElement;
    }

    [Test]
    public async Task Carries_the_root_and_the_file_tools() {
        var m = Parse([ResultChannel]);

        await Assert.That(m.GetProperty("root").GetString()).IsEqualTo("/abs/wt");
        await Assert.That(m.GetProperty("fileTools").EnumerateArray().Select(e => e.GetString()!))
            .IsEquivalentTo(PiReviewerToolSurface.FileTools);
    }

    [Test]
    public async Task Carries_each_server_with_command_args_env_and_tools() {
        var server = Parse([ResultChannel]).GetProperty("servers")[0];

        await Assert.That(server.GetProperty("id").GetString()).IsEqualTo(KcapMcpRegistry.ReservedResultChannelId);
        await Assert.That(server.GetProperty("command").GetString()).IsEqualTo("/usr/local/bin/kcap");
        await Assert.That(server.GetProperty("args").EnumerateArray().Select(e => e.GetString()!))
            .IsEquivalentTo(new[] { "mcp", "flow-result" });
        await Assert.That(server.GetProperty("env").GetProperty("KCAP_FLOW_AGENT_ID").GetString()).IsEqualTo("agent-1");
        await Assert.That(server.GetProperty("tools")[0].GetProperty("mcp").GetString()).IsEqualTo("submit_review_result");
        await Assert.That(server.GetProperty("tools")[0].GetProperty("pi").GetString()).IsEqualTo("submit_review_result");
    }

    [Test]
    public async Task The_manifest_tool_set_equals_the_allowlist() {
        var servers  = new[] { ResultChannel, new AcpMcpServerSpec("kcap-review", "/k", ["mcp", "review"], []) };
        var tools    = PiReviewerToolSurface.For(servers);
        var manifest = JsonDocument.Parse(PiReviewerManifest.Build("/abs/wt", servers, tools)).RootElement;

        var fromManifest = manifest.GetProperty("fileTools").EnumerateArray().Select(e => e.GetString()!)
            .Concat(manifest.GetProperty("servers").EnumerateArray()
                .SelectMany(s => s.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("pi").GetString()!)));

        await Assert.That(fromManifest).IsEquivalentTo(PiReviewerToolSurface.AllowlistArg(tools).Split(','));
    }
}
