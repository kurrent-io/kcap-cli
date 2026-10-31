using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[ParallelLimiter<SubprocessLimit>]
public class LocalControlHandoffProbeTests {
    const string Owner  = "acme";
    const string Name   = "widgets";
    const string Remote = "https://github.com/acme/widgets.git";

    /// <summary>A clone whose recorded origin is a GitHub URL while fetches are rewritten to a local upstream,
    /// so the identity check sees an owner and name and the fetch needs no network.</summary>
    sealed class Fixture : IDisposable {
        readonly TempDir _cloneDir = new("clone");

        public Fixture() {
            Upstream = GitRepo.Create("upstream");
            Upstream.CreateFile("main.txt", "main");
            Upstream.CommitAll("initial");
            Default = Upstream.CurrentBranch;
            Clone   = Upstream.Clone(_cloneDir.PathTo("repo"));
            Clone.Config("remote.origin.url", Remote);
            PointOriginAt(Upstream.Path);
        }

        public GitRepo Upstream { get; }
        public GitRepo Clone    { get; }
        public string  Default  { get; }

        public void PointOriginAt(string path) => Clone.Config($"url.{path}.insteadOf", Remote);

        public string CommitOnOrigin(string branch, string file) {
            Upstream.Checkout(branch, create: true);
            Upstream.CreateFile(file, file);
            Upstream.CommitAll(file);
            var tip = Upstream.Head;
            Upstream.Checkout(Default);

            return tip;
        }

        /// <summary>A commit that exists only in the clone, on a branch origin has never seen.</summary>
        public string CommitOnlyHere() {
            Clone.Checkout("local-only", create: true);
            Clone.CreateFile("local.txt", "local");
            Clone.CommitAll("local");
            var tip = Clone.Head;
            Clone.Checkout(Default);

            return tip;
        }

        public bool HasFetched(string branch) =>
            Clone.Try("rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{branch}").ExitCode == 0;

        public void Dispose() {
            Upstream.Dispose();
            _cloneDir.Dispose();
        }
    }

    static AgentOrchestrator Build(string? allowedRepoPath = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath);

    static HandoffProbeRequestDto Request(Fixture f, string branch, string owner = Owner, string name = Name, IReadOnlyList<string>? commits = null) =>
        new(f.Clone.Path, owner, name, branch, null, false, null, null, commits);

    [Test]
    public async Task Probe_resolves_a_branch_on_origin_and_reports_unconfirmed_work() {
        using var f = new Fixture();
        var tip = f.CommitOnOrigin("feature", "feature.txt");
        await using var orch = Build();

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature"), CancellationToken.None);

        await Assert.That(reply.Refusal).IsNull();
        await Assert.That(reply.FetchError).IsNull();
        await Assert.That(reply.Start!.StartSha).IsEqualTo(tip);
        await Assert.That(reply.Start.ToStart()).IsEqualTo(new HandoffStart(
            HandoffSource.Branch, HandoffCheckout.CreateBranch, tip, "feature", HandoffDetachReason.None, null));
        await Assert.That(reply.Start.Checkout).IsEqualTo("CreateBranch");
        await Assert.That(reply.Start.Source).IsEqualTo("Branch");
        await Assert.That(reply.Start.DetachReason).IsEqualTo("None");
        await Assert.That(reply.Start.Branch).IsEqualTo("feature");
        await Assert.That(reply.Start.SourceSessionId).IsEqualTo("");
        await Assert.That(reply.Completeness).IsEqualTo("Unconfirmed");
        await Assert.That(reply.MissingCommits!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Probe_reports_missing_commits() {
        using var f = new Fixture();
        f.CommitOnOrigin("feature", "feature.txt");
        var missing = f.CommitOnlyHere();
        await using var orch = Build();

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature", commits: [missing]), CancellationToken.None);

        await Assert.That(reply.Start!.ToStart()).IsNotNull();
        await Assert.That(reply.Completeness).IsEqualTo("PartlyMissing");
        await Assert.That(reply.MissingCommits!.Single()).IsEqualTo(missing);
    }

    [Test]
    public async Task Probe_refuses_a_path_outside_the_allowed_repositories() {
        using var f = new Fixture();
        f.CommitOnOrigin("feature", "feature.txt");
        using var elsewhere = new TempDir("allowed");
        await using var orch = Build(elsewhere.Path);

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature"), CancellationToken.None);

        await Assert.That(reply.Refusal).IsEqualTo(HandoffRefusals.RepoNotAllowed);
        await Assert.That(reply.Start).IsNull();
        await Assert.That(f.HasFetched("feature")).IsFalse();
    }

    [Test]
    public async Task Probe_refuses_a_checkout_of_another_repository() {
        using var f = new Fixture();
        f.CommitOnOrigin("feature", "feature.txt");
        await using var orch = Build();

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature", owner: "someone", name: "else"), CancellationToken.None);

        await Assert.That(reply.Refusal).IsEqualTo(HandoffRefusals.RepoMismatch);
        await Assert.That(reply.Start).IsNull();
        await Assert.That(f.HasFetched("feature")).IsFalse();
    }

    [Test]
    public async Task Probe_reports_an_unreachable_origin_as_a_fetch_error() {
        using var f = new Fixture();
        using var gone = new TempDir("gone");
        f.PointOriginAt(gone.PathTo("missing"));
        f.Clone.Do("config", "--unset-all", $"url.{f.Upstream.Path}.insteadOf");
        await using var orch = Build();

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature"), CancellationToken.None);

        await Assert.That(reply.Refusal).IsNull();
        await Assert.That(reply.Start).IsNull();
        await Assert.That(string.IsNullOrWhiteSpace(reply.FetchError)).IsFalse();
    }

    [Test]
    public async Task Probe_refuses_an_invalid_branch_name() {
        using var f = new Fixture();
        await using var orch = Build();

        var reply = await orch.ProbeHandoffAsync(Request(f, "bad..name"), CancellationToken.None);

        await Assert.That(reply.Refusal).IsEqualTo(HandoffRefusals.InvalidTarget);
    }

    [Test]
    public async Task Probe_reports_a_git_failure_that_is_not_a_validation_refusal_as_a_fetch_error() {
        using var f = new Fixture();
        await using var orch = Build();
        orch.ResolveHandoffForTest = (_, _, _) => throw new InvalidOperationException("git fetch timed out after 120s");

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature"), CancellationToken.None);

        await Assert.That(reply.Refusal).IsNull();
        await Assert.That(reply.FetchError).IsEqualTo("git fetch timed out after 120s");
    }

    [Test]
    public async Task Probe_strips_credentials_from_a_fetch_error() {
        using var f = new Fixture();
        await using var orch = Build();
        orch.ResolveHandoffForTest = (_, _, _) => Task.FromResult(
            new HandoffResolution(null, "fatal: unable to access 'https://user:s3cret@github.com/acme/widgets.git/': denied"));

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature"), CancellationToken.None);

        await Assert.That(reply.FetchError).IsEqualTo("fatal: unable to access 'https://github.com/acme/widgets.git/': denied");
    }

    [Test]
    public async Task Probe_answers_a_fetch_error_once_its_deadline_passes() {
        using var f = new Fixture();
        await using var orch = Build();
        orch.HandoffProbeDeadline = TimeSpan.FromMilliseconds(50);
        orch.ResolveHandoffForTest = (_, _, _) => new TaskCompletionSource<HandoffResolution>().Task;

        var reply = await orch.ProbeHandoffAsync(Request(f, "feature"), CancellationToken.None);

        await Assert.That(reply.Start).IsNull();
        await Assert.That(reply.FetchError!).Contains("timed out");
    }
}
