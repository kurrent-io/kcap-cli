using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
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
/// </summary>
public class AgentCaptureScopeLaunchTests {
    [TempHome] public required TempHome Home { get; init; }

    // An allowlist naming a real directory the repo under test is definitively not under — GitRepo
    // builds its own temp root, so admitting only this home admits nothing the tests launch in.
    ProfileContext OutOfScope() =>
        Resolutions.Of(new Profile { AllowedPaths = [Home.Path] });

    [Test]
    public async Task An_unattended_launch_outside_the_capture_scope_is_refused() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => c.Profiles = OutOfScope());

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
            configure: c => c.Profiles = OutOfScope());

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
            configure: c => c.Profiles = OutOfScope());

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
            configure: c => c.Profiles = OutOfScope());

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
}
