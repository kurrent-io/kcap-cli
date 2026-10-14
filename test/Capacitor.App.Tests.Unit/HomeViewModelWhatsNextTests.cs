using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.NextWork;

namespace Capacitor.App.Tests.Unit;

public class HomeViewModelWhatsNextTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string ValidAgentId = "0123456789abcdef0123456789abcdef";

    sealed class ConfigurableNextWorkSource : INextWorkSource {
        public NextWorkRead Next { get; set; } = NextWorkRead.Success(new NextWorkFeedDto("2026-10-14T00:00:00Z", []));
        public int Calls { get; private set; }

        public Task<NextWorkRead> FetchAsync(int limit = 20, CancellationToken ct = default) {
            Calls++;
            return Task.FromResult(Next);
        }
    }

    sealed class RecordingLaunchClient : ILaunchClient {
        public List<LaunchRequest> Requests { get; } = [];
        public LaunchOutcome Next { get; set; } = new(true, ValidAgentId, null);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Task.FromResult(Next);
        }
    }

    [Test]
    public async Task Partitions_feed_into_Waiting_Finish_and_PickUp_lanes_and_excludes_housekeeping() {
        var source = new ConfigurableNextWorkSource {
            Next = NextWorkRead.Success(new NextWorkFeedDto("2026-10-14T00:00:00Z", [
                new NextWorkFeedRowDto("pr:hash1#1", "ReviewRequested", "PR #1", "hash1", 100, "Waiting on review", []),
                new NextWorkFeedRowDto("pr:hash1#2", "ApprovedUnmerged", "PR #2", "hash1", 95, "Approved and green", []),
                new NextWorkFeedRowDto("wi:AI-1", "FinishYours", "AI-1 · Feature", "hash1", 85, "Needs push", [], WorkItemId: "wi-1"),
                new NextWorkFeedRowDto("session:s-1", "ResumeInterrupted", "Interrupted session", "hash1", 80, "Stopped mid-turn", [], SessionId: "s-1"),
                new NextWorkFeedRowDto("le:le-1", "LooseEnd", "Orphan branch", "hash1", 60, "Unlinked commits", []),
                new NextWorkFeedRowDto("evals:hash1", "EvalRegression", "Eval regression", "hash1", 40, "Eval score dropped", [], Group: "housekeeping")
            ]))
        };

        var daemon = new FakeDaemonClientService();
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));

        using var vm = new HomeViewModel(
            daemon,
            new AppStateStore(Tmp.PathTo("app-state.json")),
            new RecordingLaunchClient(),
            () => Task.FromResult(Array.Empty<string>()),
            TimeProvider.System,
            nextWork: source);

        await vm.RefreshWhatsNextAsync();

        await Assert.That(vm.WaitingOnYouLane.TotalCount).IsEqualTo(2);
        await Assert.That(vm.FinishLane.TotalCount).IsEqualTo(2);
        await Assert.That(vm.PickUpLane.TotalCount).IsEqualTo(1);
    }

    [Test]
    public async Task Caps_lane_at_three_cards_and_expands_on_Show_more() {
        var rows = Enumerable.Range(1, 5)
            .Select(i => new NextWorkFeedRowDto($"pr:hash#{i}", "ReviewRequested", $"PR #{i}", "hash", 100 - i, "Review requested", []))
            .ToArray();

        var source = new ConfigurableNextWorkSource {
            Next = NextWorkRead.Success(new NextWorkFeedDto("2026-10-14T00:00:00Z", rows))
        };

        var daemon = new FakeDaemonClientService();
        using var vm = new HomeViewModel(
            daemon,
            new AppStateStore(Tmp.PathTo("app-state.json")),
            new RecordingLaunchClient(),
            () => Task.FromResult(Array.Empty<string>()),
            TimeProvider.System,
            nextWork: source);

        await vm.RefreshWhatsNextAsync();

        await Assert.That(vm.WaitingOnYouLane.VisibleItems.Count).IsEqualTo(3);
        await Assert.That(vm.WaitingOnYouLane.CanExpand).IsTrue();
        await Assert.That(vm.WaitingOnYouLane.ExpandButtonLabel).IsEqualTo("Show more (2)");

        await vm.WaitingOnYouLane.ToggleExpandCommand.Execute().ToTask();
        await Assert.That(vm.WaitingOnYouLane.VisibleItems.Count).IsEqualTo(5);
        await Assert.That(vm.WaitingOnYouLane.ExpandButtonLabel).IsEqualTo("Show less");
    }

    [Test]
    public async Task Hover_pinning_defers_feed_replacement_until_lane_is_unpinned() {
        var source = new ConfigurableNextWorkSource {
            Next = NextWorkRead.Success(new NextWorkFeedDto("2026-10-14T00:00:00Z", [
                new NextWorkFeedRowDto("pr:hash#1", "ReviewRequested", "First PR", "hash", 100, "Because 1", [])
            ]))
        };

        var daemon = new FakeDaemonClientService();
        using var vm = new HomeViewModel(
            daemon,
            new AppStateStore(Tmp.PathTo("app-state.json")),
            new RecordingLaunchClient(),
            () => Task.FromResult(Array.Empty<string>()),
            TimeProvider.System,
            nextWork: source);

        await vm.RefreshWhatsNextAsync();
        await Assert.That(vm.WaitingOnYouLane.VisibleItems[0].Title).IsEqualTo("First PR");

        vm.WaitingOnYouLane.Pin();
        source.Next = NextWorkRead.Success(new NextWorkFeedDto("2026-10-14T00:01:00Z", [
            new NextWorkFeedRowDto("pr:hash#2", "ReviewRequested", "Second PR", "hash", 100, "Because 2", [])
        ]));

        await vm.RefreshWhatsNextAsync();
        await Assert.That(vm.WaitingOnYouLane.VisibleItems[0].Title).IsEqualTo("First PR");

        vm.WaitingOnYouLane.Release();
        vm.TryApplyQueuedFeedOnUnpin();
        await Assert.That(vm.WaitingOnYouLane.VisibleItems[0].Title).IsEqualTo("Second PR");
    }

    [Test]
    public async Task Selecting_a_card_populates_composer_selects_matching_repo_and_detects_local_worktree() {
        var existingWorktree = Tmp.CreateDir("existing-wt");
        var cliRepoHash = LocalRepoHashIndex.ComputeRepoHash("kurrent-io", "kcap-cli");

        var source = new ConfigurableNextWorkSource {
            Next = NextWorkRead.Success(new NextWorkFeedDto("2026-10-14T00:00:00Z", [
                new NextWorkFeedRowDto(
                    "wi:AI-777",
                    "FinishYours",
                    "AI-777 · Ship launcher",
                    cliRepoHash,
                    95,
                    "1 open PR, checks passing.",
                    [],
                    Group: "finish",
                    WorkItemId: "wi-777",
                    SessionId: "sess-777",
                    WorktreePath: existingWorktree,
                    LaunchPrompt: "Continue AI-777 and merge the PR.")
            ]))
        };

        var repoIndex = new LocalRepoHashIndex(path =>
            path == "/repos/kcap-cli" ? "https://github.com/kurrent-io/kcap-cli.git" : null);

        var daemon = new FakeDaemonClientService();
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var launch = new RecordingLaunchClient();

        using var vm = new HomeViewModel(
            daemon,
            new AppStateStore(Tmp.PathTo("app-state.json")),
            launch,
            () => Task.FromResult(new[] { "/repos/other", "/repos/kcap-cli" }),
            TimeProvider.System,
            nextWork: source,
            repoHashIndex: repoIndex);

        await vm.RefreshWhatsNextAsync();
        var card = vm.FinishLane.VisibleItems.Single();
        await Assert.That(card.Subtitle).Contains("Continues in the same worktree.");

        await vm.SelectWhatsNextCardAsync(card);

        await Assert.That(card.IsSelected).IsTrue();
        await Assert.That(vm.Goal).IsEqualTo("Continue AI-777 and merge the PR.");
        await Assert.That(vm.SelectedRepoPath).IsEqualTo(PlatformPaths.Normalize("/repos/kcap-cli"));
        await Assert.That(vm.ContinuesInExistingWorktree).IsTrue();
        await Assert.That(vm.WorktreeChipLabel).IsEqualTo("Same worktree · automatic");

        await vm.StartCommand.Execute().ToTask();

        await Assert.That(launch.Requests.Count).IsEqualTo(1);
        var req = launch.Requests[0];
        await Assert.That(req.TargetKey).IsEqualTo("wi:AI-777");
        await Assert.That(req.WorkItemId).IsEqualTo("wi-777");
        await Assert.That(req.ResumeSessionId).IsEqualTo("sess-777");
        await Assert.That(req.Borrowed).IsTrue();
        await Assert.That(req.BorrowCwd).IsEqualTo(existingWorktree);
    }

    [Test]
    public async Task Pasting_a_GitHub_link_into_Goal_auto_selects_matching_local_repo_and_marks_chip() {
        var repoIndex = new LocalRepoHashIndex(path =>
            path == "/repos/kcap-cli" ? "git@github.com:kurrent-io/kcap-cli.git" : null);

        var daemon = new FakeDaemonClientService();
        using var vm = new HomeViewModel(
            daemon,
            new AppStateStore(Tmp.PathTo("app-state.json")),
            new RecordingLaunchClient(),
            () => Task.FromResult(new[] { "/repos/kcap-cli" }),
            TimeProvider.System,
            repoHashIndex: repoIndex);

        await vm.ListRepositoriesAsync();
        vm.Goal = "Review https://github.com/kurrent-io/kcap-cli/pull/512";

        for (var i = 0; i < 20 && !vm.RepoSelectionFromPastedLink; i++)
            await Task.Delay(10);

        await Assert.That(vm.SelectedRepoPath).IsEqualTo(PlatformPaths.Normalize("/repos/kcap-cli"));
        await Assert.That(vm.RepoSelectionFromPastedLink).IsTrue();
        await Assert.That(vm.RepositoryChipLabel).IsEqualTo("kcap-cli · from the pasted link");

        await vm.SelectRepositoryAsync("/repos/kcap-cli");
        await Assert.That(vm.RepoSelectionFromPastedLink).IsFalse();
        await Assert.That(vm.RepositoryChipLabel).IsEqualTo("Repo · kcap-cli");
    }

    [Test]
    public async Task Start_from_blueprints_toggle_selection_watermark_worktree_mode_and_prompt_framing() {
        var daemon = new FakeDaemonClientService();
        daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap());
        daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, null));
        var launch = new RecordingLaunchClient();

        using var vm = new HomeViewModel(
            daemon,
            new AppStateStore(Tmp.PathTo("app-state.json")),
            launch,
            () => Task.FromResult(new[] { "/repos/kcap-cli" }),
            TimeProvider.System);

        await vm.SelectRepositoryAsync("/repos/kcap-cli");

        var explore = vm.StartFromBlueprints.Single(b => b.Kind == StartFromBlueprintKind.ExploreIdea);
        vm.SelectBlueprint(explore);

        await Assert.That(explore.IsSelected).IsTrue();
        await Assert.That(vm.SelectedWorktreeMode).IsEqualTo(WorktreeLaunchMode.NoBranch);
        await Assert.That(vm.WorktreeChipLabel).IsEqualTo("No branch until you ask for one");

        vm.Goal = "How should we structure the new cache layer?";
        await vm.StartCommand.Execute().ToTask();

        await Assert.That(launch.Requests.Count).IsEqualTo(1);
        var req = launch.Requests[0];
        await Assert.That(req.Borrowed).IsTrue();
        await Assert.That(req.BorrowCwd).IsEqualTo(PlatformPaths.Normalize("/repos/kcap-cli"));
        await Assert.That(req.Goal).Contains("How should we structure the new cache layer?");
        await Assert.That(req.Goal).Contains("Do not create a branch");
    }
}
