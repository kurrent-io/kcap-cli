using System.Net.Http.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1292: an unattended review-flow launch mints a per-reviewer LocalPermissionBridge token
/// (bound to its read-only allowlist), gives the reviewer that token's URL as KCAP_DAEMON_URL,
/// and revokes it on teardown. An allowlist with a non-auto-approvable server fails the launch
/// fast. Reuses the harness in <see cref="AgentOrchestratorVendorTests"/>.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    static HttpClient ReviewerClient() => new() { Timeout = TimeSpan.FromSeconds(5) };

    [Test, NotInParallel("LocalPermissionBridgeTests")]   // starts a real bridge (binds a port) — serialize with the other port-binding tests
    public async Task ReviewFlow_launch_mints_a_live_reviewer_bridge_token() {
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

                // The minted token is LIVE and auto-approves the bound read tool without a server round-trip.
                using var client = ReviewerClient();
                using var r = await client.PostAsync(
                    $"{agent.ReviewerBridgeToken}/codex/permission-request",
                    JsonContent.Create(new { session_id = "s", tool_name = "get_pr_summary" }));
                await Assert.That((int)r.StatusCode).IsEqualTo(200);   // live reviewer token, auto-approved
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]   // starts a real bridge (binds a port) — serialize with the other port-binding tests
    public async Task Default_launch_uses_the_shared_token_no_reviewer_token() {
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
                    AgentId: "def-1", Prompt: "work", Model: "opus", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

                var agent = orch.GetAgentForTest("def-1");
                await Assert.That(agent).IsNotNull();
                await Assert.That(agent!.ReviewerBridgeToken).IsNull();
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]   // starts a real bridge (binds a port) — serialize with the other port-binding tests
    public async Task ReviewFlow_launch_with_non_auto_approvable_allowlist_fails_fast() {
        var (repoPath, cleanup) = CreateGitRepo();

        try {
            var server     = new CaptureServerConnection();
            var ptyFactory = new SpyPtyProcessFactory();
            var claudeSpy  = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") { SupportsUnattended = true };
            var launchers  = new Dictionary<string, IHostedAgentLauncher> { ["claude"] = claudeSpy };

            await using var orch = BuildOrchestrator(server, ptyFactory, launchers, allowedRepoPath: repoPath);
            var bridge = orch.PermissionBridgeForTest;
            await bridge.StartAsync(CancellationToken.None);

            try {
                await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
                    AgentId: "rev-bad", Prompt: "review", Model: "opus", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-memory"]));   // write server → not auto-approvable

                await Assert.That(server.LaunchFailedCalls.Count).IsEqualTo(1);
                await Assert.That(server.LaunchFailedCalls[0].AgentId).IsEqualTo("rev-bad");
                await Assert.That(server.LaunchFailedCalls[0].Reason).Contains("not auto-approvable");
                // Failed fast: no PTY spawned, no agent registered — and NOT deferred to a prompt.
                await Assert.That(ptyFactory.SpawnCalls).IsEqualTo(0);
                await Assert.That(orch.GetAgentForTest("rev-bad")).IsNull();
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }

    [Test, NotInParallel("LocalPermissionBridgeTests")]   // starts a real bridge (binds a port) — serialize with the other port-binding tests
    public async Task Reviewer_token_is_revoked_when_the_agent_stops() {
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
                    AgentId: "rev-stop", Prompt: "review", Model: "opus", Effort: null,
                    RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                    Kind: LaunchKind.ReviewFlow, McpAllowlist: ["kcap-review"]));

                var reviewerUrl = orch.GetAgentForTest("rev-stop")!.ReviewerBridgeToken!;

                await orch.HandleStopAgentForTest("rev-stop");

                // After stop, the reviewer token is revoked — its prefix no longer accepts requests.
                using var client = ReviewerClient();
                using var r = await client.PostAsync(
                    $"{reviewerUrl}/codex/permission-request",
                    JsonContent.Create(new { session_id = "s", tool_name = "get_pr_summary" }));
                await Assert.That((int)r.StatusCode).IsEqualTo(404);
            } finally {
                await bridge.DisposeAsync();
            }
        } finally {
            cleanup();
        }
    }
}
