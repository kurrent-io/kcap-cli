using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1292: an unattended review-flow launch mints a per-reviewer LocalPermissionBridge token
/// (bound to its read-only allowlist), gives the reviewer that token's URL as KCAP_DAEMON_URL,
/// and revokes it on teardown. An allowlist with a non-auto-approvable server fails the launch
/// fast. Reuses the harness in <see cref="AgentOrchestratorVendorTests"/>. The bridge's request
/// classification itself is covered exhaustively by <see cref="LocalPermissionBridgeTests"/>; these
/// assert the orchestrator WIRING via <c>ReviewerTokenCountForTest</c> so they needn't do real HTTP.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    // Starts a real bridge (binds a loopback port) → serialize with the other port-binding tests.
    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_launch_mints_a_reviewer_token_and_revokes_it_on_cleanup() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") { SupportsUnattended = true };
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-1", Prompt: "review", Model: "opus", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"]));

                var agent = orch.GetAgentForTest("rev-1");
                await Assert.That(agent).IsNotNull();
                await Assert.That(agent!.ReviewerBridgeToken).IsNotNull();
                await Assert.That(agent.ReviewerBridgeToken).IsNotEqualTo(bridge.BaseUrl);   // a dedicated token, not the shared one
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(1);

                // Teardown revokes the token, closing the auto-approve window.
                await orch.CleanupAgentForTest("rev-1");
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    // No bridge started (no port bind): a Default launch never mints a reviewer token regardless.
    [Test]
    public async Task Default_launch_uses_the_shared_token_no_reviewer_token() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess());
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") { SupportsUnattended = true };
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);

            await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "def-1", Prompt: "work", Model: "opus", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

            var agent = orch.GetAgentForTest("def-1");
            await Assert.That(agent).IsNotNull();
            await Assert.That(agent!.ReviewerBridgeToken).IsNull();
        } finally {
            cleanup();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]
    public async Task ReviewFlow_launch_with_non_auto_approvable_allowlist_fails_fast() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") { SupportsUnattended = true };
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);   // BaseUrl must be non-null so the mint/validate runs

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-bad", Prompt: "review", Model: "opus", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-memory"]));   // write server → not auto-approvable

                await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
                await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("rev-bad");
                await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("not auto-approvable");
                // Failed fast: no PTY spawned, no agent registered, no reviewer token left behind.
                await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
                await Assert.That(orch.GetAgentForTest("rev-bad")).IsNull();
                await Assert.That(bridge.ReviewerTokenCountForTest).IsEqualTo(0);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    // Secrecy: the reviewer token rides in KCAP_DAEMON_URL, so it must be in PtyEnvScrub's scrub
    // list — otherwise it could leak into a recorded/child env another hosted agent reads.
    [Test]
    public async Task Reviewer_token_env_var_KCAP_DAEMON_URL_is_scrubbed() {
        await Assert.That(Capacitor.Cli.Daemon.Pty.PtyEnvScrub.HostedAgentVars).Contains("KCAP_DAEMON_URL");
    }
}
