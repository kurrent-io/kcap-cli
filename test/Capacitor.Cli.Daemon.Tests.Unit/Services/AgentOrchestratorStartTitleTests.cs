using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorStartTitleTests {
    static async Task<CaptureServerConnection> LaunchAsync(string repoPath, string agentId, string? prompt, string? title, bool titleDerived) {
        var server = new CaptureServerConnection();
        var claude = new SpyHostedAgentRuntimeFactory("claude") { EmitsTerminalOutput = false, SupportsUnattended = true };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), extraRuntimeFactories: [claude]);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: agentId, Prompt: prompt, Model: "default", Effort: null, RepoPath: repoPath,
            Tools: null, AttachmentIds: null, Vendor: "claude", Title: title, TitleDerived: titleDerived));

        return server;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Server_launch_registers_the_server_title_as_sent(bool derived) {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = await LaunchAsync(repoPath, "agent-title", "fix the login redirect", "Fix login", derived);

        await Assert.That(server.AgentRegisteredTitles)
            .Contains(("agent-title", (AgentStartTitle?)new AgentStartTitle("Fix login", derived)));
    }

    [Test]
    public async Task Launch_from_a_server_sending_no_title_registers_one_derived_from_the_prompt() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = await LaunchAsync(repoPath, "agent-old-server", "\nfix the login redirect\nand add a test", null, false);

        await Assert.That(server.AgentRegisteredTitles)
            .Contains(("agent-old-server", (AgentStartTitle?)new AgentStartTitle("fix the login redirect", Derived: true)));
    }

    [Test]
    public async Task Launch_with_neither_title_nor_prompt_registers_no_title() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = await LaunchAsync(repoPath, "agent-untitled", null, null, false);

        await Assert.That(server.AgentRegisteredTitles).Contains(("agent-untitled", (AgentStartTitle?)null));
    }

    [Test]
    public async Task Reregistration_resends_the_start_title() {
        using var worktree = new TempDir();
        var path   = worktree.CreateDir("worktree");
        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.RegisterAgentForTest(new AgentInstance(
            "agent-rereg-title", null, "", null, path, "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess(), TimeProvider.System),
            new WorktreeInfo(path, "", path, IsStandalone: true), new CancellationTokenSource()
        ) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow,
            StartTitle    = new AgentStartTitle("Fix login", Derived: false)
        });

        await server.ReRegisterAgentsHook!();

        await Assert.That(server.AgentRegisteredTitles)
            .Contains(("agent-rereg-title", (AgentStartTitle?)new AgentStartTitle("Fix login", Derived: false)));
    }

    [Test]
    public async Task Status_title_seeds_from_the_start_title_over_the_prompt() {
        using var worktree = new TempDir();
        var path = worktree.CreateDir("worktree");

        var agent = new AgentInstance(
            "agent-seed", "fix the login redirect", "", null, path, "claude",
            new PtyHostedAgentRuntime("claude", new StubPtyProcess(), TimeProvider.System),
            new WorktreeInfo(path, "", path, IsStandalone: true), new CancellationTokenSource()
        ) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow,
            StartTitle    = new AgentStartTitle("Fix login", Derived: false)
        };

        await Assert.That(agent.Title).IsEqualTo("Fix login");
    }
}
