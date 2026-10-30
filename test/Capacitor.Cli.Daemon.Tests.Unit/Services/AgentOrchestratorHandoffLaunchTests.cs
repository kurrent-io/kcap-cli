using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A launch carrying a hand-off block starts in a worktree at the probed start, with the hand-off
/// prompt, or is refused without leaving a worktree or starting a runtime.</summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorHandoffLaunchTests {
    const string AgentId  = "handoff-1";
    const string UserText = "Finish the retry loop.";

    static readonly HandoffSourceContext Source = new(
        "session-ended-1", "Wire the widget", "User One", "acme", "widgets", "feature", null, null, "build it", "now test it");

    sealed class Fixture : IAsyncDisposable {
        public Fixture(Exception? startThrow = null) {
            Repo    = GitRepo.CreateWithCommit();
            Repo.AddRemote("https://github.com/acme/widgets.git");
            Server  = new CaptureServerConnection();
            Factory = new SpyHostedAgentRuntimeFactory("cursor") { StartThrow = startThrow };
            Orch    = AgentOrchestratorHarness.BuildOrchestrator(
                Server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
                allowedRepoPath: Repo.Path, extraRuntimeFactories: [Factory]);
        }

        public GitRepo                      Repo    { get; }
        public CaptureServerConnection      Server  { get; }
        public SpyHostedAgentRuntimeFactory Factory { get; }
        public AgentOrchestrator            Orch    { get; }

        public string[] Worktrees() {
            var root = Path.Combine(Repo.Path, ".capacitor", "worktrees");

            return Directory.Exists(root) ? Directory.GetDirectories(root) : [];
        }

        public string? Refusal() => Server.LaunchFailedCalls.Where(c => c.AgentId == AgentId).Select(c => c.Reason).SingleOrDefault();

        public async ValueTask DisposeAsync() {
            await Orch.DisposeAsync();
            Repo.Dispose();
        }
    }

    static HandoffLaunchStart Start(string sha, string checkout, string? localBranchSha = null, string detachReason = "None") =>
        new(Source.SessionId, sha, checkout, "feature", localBranchSha, "Branch", detachReason, null, false, null, null);

    static LaunchAgentCommand Launch(string repoPath, HandoffLaunchStart start, string? prompt = UserText, HandoffSourceContext? source = null) =>
        new(AgentId: AgentId, Prompt: prompt ?? "", Model: "", Effort: null, RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "cursor") {
            Handoff = new HandoffLaunch(start, source ?? Source, SessionCommits: [])
        };

    [Test]
    [Arguments("CreateBranch", false, true)]
    [Arguments("CheckoutBranch", true, false)]
    [Arguments("Detached", false, false)]
    public async Task A_handoff_launch_starts_in_a_handoff_worktree_at_the_resolved_sha(string checkout, bool localBranch, bool ownsBranch) {
        await using var f = new Fixture();
        var sha = f.Repo.Head;
        if (localBranch) f.Repo.Do("branch", "feature", sha);

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(sha, checkout, localBranch ? sha : null,
            checkout == "Detached" ? "CheckedOutElsewhere" : "None")));

        await Assert.That(f.Refusal()).IsNull();
        var worktree = f.Factory.LastContext!.Worktree;
        await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(sha);
        await Assert.That(worktree.OwnsBranch).IsEqualTo(ownsBranch);
        await Assert.That(f.Orch.GetAgentForTest(AgentId)!.HandoffSource).IsEqualTo(Source);
    }

    [Test]
    public async Task A_moved_start_is_refused_as_handoff_start_changed() {
        await using var f = new Fixture();
        var probed = f.Repo.Head;
        f.Repo.Do("branch", "feature", probed);
        f.Repo.CreateFile("later.txt", "later");
        f.Repo.CommitAll("later");
        f.Repo.Do("branch", "-f", "feature", f.Repo.Head);

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(probed, "CheckoutBranch", probed)));

        await Assert.That(f.Refusal()).StartsWith("handoff_start_changed");
        await Assert.That(f.Factory.StartCalls).IsEqualTo(0);
        await Assert.That(f.Worktrees()).IsEmpty();
        await Assert.That(f.Repo.RevParse("refs/heads/feature")).IsEqualTo(f.Repo.Head);
    }

    [Test]
    public async Task A_handoff_with_an_unknown_shape_is_refused() {
        await using var f = new Fixture();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(f.Repo.Head, "Sideways")));

        await Assert.That(f.Refusal()).StartsWith("invalid_handoff");
        await Assert.That(f.Factory.StartCalls).IsEqualTo(0);
        await Assert.That(f.Worktrees()).IsEmpty();
    }

    [Test]
    [Arguments("borrowed")]
    [Arguments("review")]
    [Arguments("review-flow")]
    [Arguments("scratch")]
    [Arguments("resume-session")]
    public async Task A_borrowed_or_review_launch_with_a_handoff_block_is_refused(string shape) {
        await using var f = new Fixture();
        var plain = Launch(f.Repo.Path, Start(f.Repo.Head, "Detached", detachReason: "CheckedOutElsewhere"));
        var cmd = shape switch {
            "borrowed"    => plain with { Borrowed = true, BorrowCwd = f.Repo.Path },
            "review"      => plain with { Kind = LaunchKind.Review, Review = new ReviewLaunchInfo("acme", "widgets", 7) },
            "review-flow" => plain with { Kind = LaunchKind.ReviewFlow },
            "scratch"     => plain with { Kind = LaunchKind.ReviewFlow, ScratchWorkspace = true, RepoPath = "" },
            _             => plain with { ResumeSessionId = "vendor-session-1" }
        };

        await f.Orch.HandleLaunchAgentForTest(cmd);

        await Assert.That(f.Refusal()).StartsWith("invalid_handoff");
        await Assert.That(f.Factory.StartCalls).IsEqualTo(0);
        await Assert.That(f.Worktrees()).IsEmpty();
    }

    /// <summary>The app chose the checkout, so the daemon checks its origin again before creating anything.</summary>
    [Test]
    public async Task A_checkout_of_another_repository_is_refused() {
        await using var f = new Fixture();
        f.Repo.Config("remote.origin.url", "https://github.com/acme/gadgets.git");

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(f.Repo.Head, "CreateBranch")));

        await Assert.That(f.Refusal()).StartsWith("repo_mismatch");
        await Assert.That(f.Factory.StartCalls).IsEqualTo(0);
        await Assert.That(f.Worktrees()).IsEmpty();
        await Assert.That(f.Repo.Try("rev-parse", "--verify", "--quiet", "refs/heads/feature").ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task The_runtime_receives_the_handoff_prompt_not_the_raw_text() {
        await using var f = new Fixture();

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(f.Repo.Head, "CreateBranch")));

        var prompt = f.Factory.LastContext!.Prompt!;
        await Assert.That(prompt).StartsWith("This session continues an earlier session, which has ended;");
        await Assert.That(prompt).Contains("<previous-session-data>");
        await Assert.That(prompt).EndsWith(UserText);
    }

    /// <summary>With no text in the box the prompt is all boilerplate, so the title names the session handed off,
    /// by its title or else by its id.</summary>
    [Test]
    [Arguments("Wire the widget", "", "Hand-off of Wire the widget")]
    [Arguments(null, "", "Hand-off of 0f0f0f0f")]
    [Arguments("Wire the widget", UserText, UserText)]
    public async Task A_handoff_without_text_is_titled_after_its_source(string? title, string text, string expected) {
        await using var f = new Fixture();
        var source = Source with { SessionId = "0f0f0f0f-2222-4222-8222-000000000002", Title = title };

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(f.Repo.Head, "CreateBranch"), text, source));

        await Assert.That(f.Orch.GetAgentForTest(AgentId)!.StartTitle?.Text).IsEqualTo(expected);
        await Assert.That(f.Server.AgentRegisteredTitles.Single(t => t.AgentId == AgentId).Title?.Text).IsEqualTo(expected);
    }

    [Test]
    public async Task A_handoff_title_is_shortened_like_a_derived_title() {
        await using var f = new Fixture();
        var source = Source with { Title = "\n" + new string('w', 200) + "\nsecond line" };

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(f.Repo.Head, "CreateBranch"), "", source));

        var title = f.Orch.GetAgentForTest(AgentId)!.StartTitle!.Value.Text;
        await Assert.That(title.Length).IsEqualTo(AgentStartTitle.MaxDerivedLength);
        await Assert.That(title).StartsWith("Hand-off of www");
        await Assert.That(title).EndsWith("…");
    }

    /// <summary>The worktree checks out the user's own branch, so the failed launch's cleanup must leave
    /// that branch where it was.</summary>
    [Test]
    public async Task A_failed_start_keeps_a_pre_existing_branch() {
        await using var f = new Fixture(startThrow: new InvalidOperationException("the agent exited before its handshake"));
        var tip = f.Repo.Head;
        f.Repo.Do("branch", "feature", tip);

        await f.Orch.HandleLaunchAgentForTest(Launch(f.Repo.Path, Start(tip, "CheckoutBranch", tip)));

        await Assert.That(f.Factory.StartCalls).IsEqualTo(1);
        await Assert.That(f.Factory.LastContext!.Worktree.Branch).IsEqualTo("feature");
        await Assert.That(f.Factory.LastContext.Worktree.OwnsBranch).IsFalse();
        await Assert.That(f.Refusal()).IsEqualTo("the agent exited before its handshake");
        await Assert.That(f.Repo.RevParse("refs/heads/feature")).IsEqualTo(tip);
        await Assert.That(f.Worktrees()).IsEmpty();
    }
}
