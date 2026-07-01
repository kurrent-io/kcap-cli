using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit;

public class ClaudeLauncherReviewFlowTests {
    static ClaudeLauncher NewLauncher() =>
        new(new DaemonConfig { ClaudePath = "claude" }, NullLogger<ClaudeLauncher>.Instance);

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
    public async Task Review_flow_launch_loads_no_mcp_servers() {
        var args = NewLauncher().BuildArgs(NewCtx(isReviewFlow: true)).Args;

        await Assert.That(args).Contains("--strict-mcp-config");
        await Assert.That(args).Contains("--mcp-config");

        // The --mcp-config value must parse to an empty mcpServers map so the reviewer
        // cannot recursively invoke kcap-flows.
        var cfgIndex = Array.IndexOf(args, "--mcp-config");
        var parsed   = JsonNode.Parse(args[cfgIndex + 1])!.AsObject();
        await Assert.That(parsed["mcpServers"]!.AsObject().Count).IsEqualTo(0);
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
    }

    [Test]
    public async Task Claude_launcher_supports_unattended() {
        await Assert.That(NewLauncher().SupportsUnattended).IsTrue();
    }
}
