using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The orchestrator wiring for <see cref="AgentCaptureScope"/>: which launches the profile's
/// capture scope is allowed to refuse, and what the refusal is permitted to say.
/// <para>The split is on whether a human chose the directory, not on who dispatched the launch —
/// an unattended reviewer arrives through the same server lane as a person clicking launch. So
/// both halves are asserted against the SAME out-of-scope profile, since a rule that refused
/// everything would pass the refusal test on its own.</para>
/// <para>Scope is configured by writing the config the daemon actually reads, not by handing it a
/// resolution: the gate re-reads per launch, so an in-memory-only profile would prove nothing
/// about production.</para>
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentCaptureScopeLaunchTests {
    [TempHome] public required TempHome Home { get; init; }

    /// <summary>An allowlist naming a real directory the repo under test is definitively not under
    /// — GitRepo builds its own temp root, so admitting only this home admits nothing launched
    /// here.</summary>
    void AllowOnlyTheHome(ConfigRoot root) =>
        File.WriteAllText(root.Path("config.json"), $$"""
            {
              "version": 2,
              "active_profile": "default",
              "profiles": { "default": { "allowed_paths": ["{{Home.Path.Replace("\\", "/")}}"] } },
              "profile_bindings": {}
            }
            """);

    [Test]
    public async Task An_unattended_launch_outside_the_capture_scope_is_refused() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => AllowOnlyTheHome(c.ConfigRoot));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "rev-out", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Count(c => c.AgentId == "rev-out")).IsEqualTo(1);
        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "rev-out").Reason)
            .Contains("out_of_capture_scope");
        await Assert.That(orch.GetAgentForTest("rev-out")).IsNull();
    }

    // A PR-review launch is unattended for the same reason a flow reviewer is: the repo comes from
    // the review, not from someone choosing it here.
    [Test]
    public async Task A_pr_review_launch_outside_the_capture_scope_is_refused() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => AllowOnlyTheHome(c.ConfigRoot));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "pr-out", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.Review));

        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "pr-out").Reason)
            .Contains("out_of_capture_scope");
    }

    // The other half of the rule, and the reason the gate is not simply "apply the lists": asking
    // Capacitor to run an agent in a directory IS the opt-in for that run, so the same profile that
    // refuses the reviewer above must not refuse this.
    [Test]
    public async Task A_launch_a_human_asked_for_is_never_refused_by_capture_scope() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => AllowOnlyTheHome(c.ConfigRoot));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "mine-1", Prompt: "go", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.Default));

        await Assert.That(server.LaunchFailedCalls.Where(c => c.AgentId == "mine-1")
            .Any(c => c.Reason.Contains("out_of_capture_scope"))).IsFalse();
    }

    // The refusal travels to the server, so it must not carry the identity of the checkout the
    // profile just declined to report — naming it would upload the one thing the refusal protects.
    [Test]
    public async Task The_refusal_sent_to_the_server_names_no_path() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => AllowOnlyTheHome(c.ConfigRoot));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "quiet-1", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        var reason = server.LaunchFailedCalls.Single(c => c.AgentId == "quiet-1").Reason;

        // Assert it is OUR refusal being inspected: without this the test passes for any launch
        // that failed for some other reason, or none.
        await Assert.That(reason).Contains("out_of_capture_scope");
        await Assert.That(reason).DoesNotContain((string) repoPath);
    }

    // A profile that scopes nothing is the default, and must leave every launch exactly as it was.
    [Test]
    public async Task An_unattended_launch_is_untouched_when_the_profile_scopes_nothing() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "rev-in", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Where(c => c.AgentId == "rev-in")
            .Any(c => c.Reason.Contains("out_of_capture_scope"))).IsFalse();
    }

    // The daemon outlives the config it booted with. A list added while it is running has to bind
    // to the next launch rather than the next restart — otherwise the window between adding an
    // exclusion and restarting the daemon is exactly when it keeps uploading.
    [Test]
    public async Task A_list_written_after_the_daemon_started_binds_to_the_next_launch() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        ConfigRoot? configRoot = null;

        // Built with nothing configured; the list appears only once the daemon is already up.

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => configRoot = c.ConfigRoot);

        AllowOnlyTheHome(configRoot!);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "late-1", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "late-1").Reason)
            .Contains("out_of_capture_scope");
    }

    // A borrowed launch skips the early gate on purpose: the path that will actually run is only
    // known once the borrow is authorized and canonicalized. This is the snapshot case, so the
    // refusal also has to land before the copy — a checkout the profile declines to report on
    // should not be duplicated on disk first.
    [Test]
    public async Task A_borrowed_unattended_launch_is_refused_before_its_snapshot_is_cut() {
        using var cwd = GitRepo.CreateWithCommit();

        var server  = new CaptureServerConnection();
        var factory = new SpyHostedAgentRuntimeFactory("cursor") {
            SupportsUnattended                        = true,
            SupportsBorrowedReviewFlow                = true,
            BorrowedReviewRequiresIndependentSnapshot = true
        };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [factory],
            configure: c => AllowOnlyTheHome(c.ConfigRoot));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            "borrow-out", "review", "default", null, cwd, null, null,
            Vendor: "cursor", Kind: LaunchKind.ReviewFlow, Borrowed: true, BorrowCwd: cwd));

        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "borrow-out").Reason)
            .Contains("out_of_capture_scope");
        await Assert.That(factory.LastContext).IsNull();
    }
}
