using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudeLauncherReviewFlowTests {
    static ClaudeLauncher NewLauncher(string? serverUrl = null, string capacitorPath = "kcap") =>
        new(new DaemonConfig { ClaudePath = "claude", ServerUrl = serverUrl ?? "", CapacitorPath = capacitorPath }, NullLogger<ClaudeLauncher>.Instance);

    static LauncherContext NewCtx(bool isReviewFlow, string? prompt = "review this", string model = "sonnet") =>
        new(
            AgentId:       "a-1",
            SourceRepoPath:"/tmp/repo",
            Worktree:      new WorktreeInfo(Path: "/tmp/wt", Branch: "wt-branch", SourceRepo: "/tmp/repo"),
            Prompt:        prompt,
            Model:         model,
            Effort:        null,
            Tools:         null,
            IsReview:      false,
            IsReviewFlow:  isReviewFlow,
            Review:        null,
            ReviewLaunch:  null
        );

    [Test]
    public async Task Review_flow_launch_bypasses_permissions() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--permission-mode");
        var i = Array.IndexOf(args, "--permission-mode");
        await Assert.That(args[i + 1]).IsEqualTo("bypassPermissions");
    }

    [Test]
    public async Task Review_flow_launch_without_server_url_loads_no_mcp_servers() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var parsed   = JsonNode.Parse(args[cfgIndex + 1])!.AsObject();
        await Assert.That(parsed["mcpServers"]!.AsObject().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Review_flow_launch_loads_exactly_the_flow_result_server() {
        var args = NewLauncher(serverUrl: "https://t.example", capacitorPath: "/opt/kcap").BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var servers  = JsonNode.Parse(args[cfgIndex + 1])!.AsObject()["mcpServers"]!.AsObject();

        await Assert.That(servers.Count).IsEqualTo(1);
        var flowResult = servers["kcap-flow-result"]!.AsObject();
        await Assert.That(flowResult["command"]!.GetValue<string>()).IsEqualTo("/opt/kcap");
        await Assert.That(flowResult["args"]![0]!.GetValue<string>()).IsEqualTo("mcp");
        await Assert.That(flowResult["args"]![1]!.GetValue<string>()).IsEqualTo("flow-result");
        await Assert.That(flowResult["env"]!["KCAP_URL"]!.GetValue<string>()).IsEqualTo("https://t.example");
        await Assert.That(flowResult["env"]!["KCAP_FLOW_AGENT_ID"]!.GetValue<string>()).IsEqualTo("a-1");
    }

    [Test]
    public async Task Review_flow_launch_disallows_the_agent_subagent_tool() {
        // Subagents don't inherit --mcp-config, so a spawned Agent could re-read ambient
        // MCP (incl. user-scoped kcap-flows) and recursively start a flow. Block it.
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--disallowedTools");
        var i = Array.IndexOf(args, "--disallowedTools");
        await Assert.That(args[i + 1]).IsEqualTo("Agent");
    }

    [Test]
    public async Task Review_flow_launch_still_passes_model_and_prompt() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true, prompt: "the prompt", model: "opus")).Args;

        await Assert.That(args).Contains("--model");
        await Assert.That(args).Contains("opus");
        await Assert.That(args).Contains("--");
        await Assert.That(args[^1]).IsEqualTo("the prompt");
    }

    [Test]
    public async Task Non_review_flow_launch_is_unchanged_no_bypass_or_strict_mcp() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: false)).Args;

        await Assert.That(args).DoesNotContain("--permission-mode");
        await Assert.That(args).DoesNotContain("--strict-mcp-config");
        await Assert.That(args).DoesNotContain("--mcp-config");
        await Assert.That(args).DoesNotContain("--disallowedTools");
    }

    [Test]
    public async Task Claude_launcher_supports_unattended() {
        await Assert.That(NewLauncher().SupportsUnattended).IsTrue();
    }
}
