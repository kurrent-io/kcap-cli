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

    // A borrow may sit BELOW its repository root, and capture scope judges the directory the
    // runtime is handed rather than the root above it — judging the root would find the repository
    // unexcluded and run the reviewer inside the excluded subdirectory regardless.
    [Test]
    public async Task A_borrow_in_an_excluded_subdirectory_is_refused_though_its_repo_root_is_not() {
        using var cwd = GitRepo.CreateWithCommit();

        var privateDir = cwd.CreateDir("private");
        var server     = new CaptureServerConnection();
        var factory    = new SpyHostedAgentRuntimeFactory("cursor") {
            SupportsUnattended                        = true,
            SupportsBorrowedReviewFlow                = true,
            BorrowedReviewRequiresIndependentSnapshot = true
        };

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            extraRuntimeFactories: [factory],
            // The repository root is admitted; only the subdirectory is excluded.
            configure: c => File.WriteAllText(c.ConfigRoot.Path("config.json"), $$"""
                {
                  "version": 2,
                  "active_profile": "default",
                  "profiles": { "default": { "excluded_paths": ["{{privateDir.Path.Replace("\\", "/")}}"] } },
                  "profile_bindings": {}
                }
                """));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            "borrow-sub", "review", "default", null, cwd, null, null,
            Vendor: "cursor", Kind: LaunchKind.ReviewFlow, Borrowed: true, BorrowCwd: privateDir.Path));

        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "borrow-sub").Reason)
            .Contains("out_of_capture_scope");
        await Assert.That(factory.LastContext).IsNull();
    }

    // A config that cannot be read is not evidence that nothing is scoped — LoadProfileConfig
    // answers a broken one with an empty profile, which would otherwise open the gate on exactly
    // the checkout an existing list was excluding.
    [Test]
    public async Task An_unreadable_config_refuses_rather_than_admitting_the_launch() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => File.WriteAllText(c.ConfigRoot.Path("config.json"), "{ not json at all"));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "broken-1", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "broken-1").Reason)
            .Contains("could not be read");
    }

    // The counterpart: no config file at all is a real answer, not a failed read, so the default
    // install is not refused.
    [Test]
    public async Task A_missing_config_is_not_treated_as_an_unreadable_one() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "nocfg-1", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Where(c => c.AgentId == "nocfg-1")
            .Any(c => c.Reason.Contains("out_of_capture_scope"))).IsFalse();
    }

    // Syntax is not the bar: a document that parses but does not deserialize fails inside the
    // config load and lands on the same empty profile an unreadable file does. Whether the lists
    // are absent or merely unknown is what the gate turns on, so both have to refuse.
    [Test]
    public async Task A_config_that_parses_but_does_not_deserialize_refuses_too() {
        using var repoPath = GitRepo.CreateWithCommit();

        var server = new CaptureServerConnection();

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("codex"),
            allowedRepoPath: repoPath,
            configure: c => File.WriteAllText(
                c.ConfigRoot.Path("config.json"),
                """{ "version": 2, "active_profile": "default", "profiles": "not-an-object" }"""));

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "typed-1", Prompt: "review", Model: "default", Effort: null,
            RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "codex",
            Kind: LaunchKind.ReviewFlow));

        await Assert.That(server.LaunchFailedCalls.Single(c => c.AgentId == "typed-1").Reason)
            .Contains("could not be read");
    }
}
