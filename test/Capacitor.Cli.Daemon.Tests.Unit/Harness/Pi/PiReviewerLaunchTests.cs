using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Pi;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Pi;

/// <summary>The reviewer launch shape: argv order, the tool allowlist, and the env a reviewer child
/// gets instead of an interactive one.</summary>
public class PiReviewerLaunchTests {
    static RuntimeStartContext Ctx(
            string? model = null, string? prompt = "", bool isReview = false, bool isReviewFlow = false,
            WorkLocation work = WorkLocation.OwnedWorktree, string? serverUrl = "http://kcap.test",
            string daemonId = "", string daemonEpoch = "") => new(
        AgentId: "agent-1", Vendor: "pi", SourceRepoPath: "/repo",
        Worktree: new WorktreeInfo(Path: "/abs/wt", Branch: "b", SourceRepo: "/repo"), Prompt: prompt,
        Model: model, Effort: null, Tools: null,
        IsReview: isReview, IsReviewFlow: isReviewFlow, Review: null,
        Cols: 80, Rows: 24,
        ServerUrl: serverUrl, DaemonBridgeUrl: null, CapacitorPath: "/usr/local/bin/kcap",
        Work: work, DaemonId: daemonId, DaemonEpoch: daemonEpoch);

    static readonly PiReviewerLaunchPaths Paths = new(
        Dir: "/state/pi-reviewers/x", Extension: "/state/pi-reviewers/x/kcap-reviewer.ts",
        Manifest: "/state/pi-reviewers/x/manifest.json", SystemPrompt: "/state/pi-reviewers/x/system-prompt.md",
        Sessions: "/state/pi-reviewers/x/sessions", Ready: "/state/pi-reviewers/x/ready.json");

    static readonly IReadOnlyList<PiReviewerTool> Tools = PiReviewerToolSurface.For(
        [new(KcapMcpRegistry.ReservedResultChannelId, "/usr/local/bin/kcap", ["mcp", "flow-result"], [])]);

    [Test]
    public async Task Reviewer_argv_is_byte_exact() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true), Paths, Tools);

        await Assert.That(psi.ArgumentList).IsEquivalentTo(new[] {
            "--mode", "rpc",
            "--no-approve", "--no-extensions", "-e", Paths.Extension,
            "--tools", "read_file,list_directory,search_files,submit_review_result,send_flow_message",
            "--no-context-files", "--no-skills", "--no-prompt-templates", "--no-themes",
            "--system-prompt", Paths.SystemPrompt, "--append-system-prompt", "",
            "--offline",
            "--session-dir", Paths.Sessions,
        }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reviewer_argv_appends_the_model_last() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true, model: "anthropic/x"), Paths, Tools);

        await Assert.That(psi.ArgumentList.TakeLast(2)).IsEquivalentTo(new[] { "--model", "anthropic/x" },
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reviewer_argv_never_carries_the_inert_builtin_flag() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true), Paths, Tools);

        await Assert.That(psi.ArgumentList).DoesNotContain("--no-builtin-tools");
    }

    [Test, NotInParallel]
    public async Task Reviewer_env_names_the_manifest_and_drops_the_variables_that_repoint_pi() {
        Environment.SetEnvironmentVariable("JITI_ALIAS", "x");
        Environment.SetEnvironmentVariable("PI_EXPERIMENTAL", "1");

        try {
            var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx(isReviewFlow: true), Paths, Tools);

            await Assert.That(psi.Environment[PiReviewerExtension.ManifestEnvVar]).IsEqualTo(Paths.Manifest);
            foreach (var name in new[] { "PI_PACKAGE_DIR", "PI_EXPERIMENTAL", "PI_CODING_AGENT_SESSION_DIR" })
                await Assert.That(psi.Environment.ContainsKey(name)).IsFalse();
            await Assert.That(psi.Environment.Keys.Any(k => k.StartsWith("JITI_", StringComparison.Ordinal))).IsFalse();
        } finally {
            Environment.SetEnvironmentVariable("JITI_ALIAS", null);
            Environment.SetEnvironmentVariable("PI_EXPERIMENTAL", null);
        }
    }

    [Test]
    public async Task An_interactive_launch_is_unchanged() {
        var psi = PiRpcHostedAgentRuntimeFactory.BuildPsi(new DaemonConfig(), Ctx());

        await Assert.That(string.Join(" ", psi.ArgumentList)).IsEqualTo("--mode rpc");
        await Assert.That(psi.Environment.ContainsKey(PiReviewerExtension.ManifestEnvVar)).IsFalse();
    }
}
