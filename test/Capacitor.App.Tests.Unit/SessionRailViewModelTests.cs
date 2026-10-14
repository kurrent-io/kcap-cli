using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class SessionRailViewModelTests {
    // "/x/wt/<leaf>" resolves to "/x" — a stand-in for GitRepository.ResolveMainRepoRoot so no
    // test touches real .git files.
    static string Resolve(string path) {
        var marker = path.IndexOf("/wt/", StringComparison.Ordinal);
        return marker < 0 ? path : path[..marker];
    }

    static AgentStatusDto Dto(string id, string? repoPath, string status = "Running", DateTime? created = null) =>
        new(id, "agent", "claude", repoPath, status, null, null, null, created ?? DateTime.UtcNow, null, null);

    static AgentInstanceDto RemoteDto(string id, string owner, string repo, string daemon = "work-mac") => new() {
        AgentId = id, Status = "Running", DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude",
        RepoOwner = owner, RepoName = repo,
    };

    /// Builds the rail over a fresh AgentDirectory — daemon and remote fed by the fakes, repo
    /// identity resolved by originUrl (null unless a test needs merged remote/local groups).
    static (FakeDaemonClientService Service, FakeRemoteAgents Remote, SessionRailViewModel Rail) Build(
            Action<string>? open = null, Action<string>? openRemote = null,
            Func<string, string>? resolveRepoRoot = null, Func<string, string?>? originUrl = null) {
        var service = new FakeDaemonClientService();
        var remote = new FakeRemoteAgents();
        var directory = new AgentDirectory(
            service, remote, new FakeServerLane(), new RepoIdentityResolver(originUrl ?? (_ => null)),
            resolveRepoRoot ?? Resolve, null, null, TimeProvider.System);
        var rail = new SessionRailViewModel(
            directory, open ?? (_ => { }), openRemote ?? (_ => { }), TimeProvider.System);
        return (service, remote, rail);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Revealing_a_session_honors_origin_and_covers_pending_sessions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("same", "/local"), new RepoIdentity("path:/local", "local")));
            directory.Rows.AddOrUpdate(AgentRow.FromRemote(RemoteDto("same", "owner", "remote")));
            directory.Rows.AddOrUpdate(AgentRow.FromPending(new PendingLaunchDto("pending", "claude", "/pending", "Title", DateTime.UtcNow, "spawned"),
                new RepoIdentity("path:/pending", "pending")));
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);
            foreach (var repo in rail.Repos) repo.ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("same", AgentOrigin.Remote);
            await Assert.That(rail.Repos.Single(r => r.IsExpanded).Label).IsEqualTo("owner/remote");
            rail.Repos.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("same", AgentOrigin.Local);
            await Assert.That(rail.Repos.Single(r => r.IsExpanded).Label).IsEqualTo("local");
            rail.Repos.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("pending", AgentOrigin.Local);
            await Assert.That(rail.Repos.Single(r => r.IsExpanded).Label).IsEqualTo("pending");
            rail.Repos.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("missing", AgentOrigin.Local);
            await Assert.That(rail.Repos.Any(r => r.IsExpanded)).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Creation_time_order_survives_updates_and_reversed_refreshes() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
                var rows = new[] { Dto("c", "/repo", created: created.AddHours(1)),
                    Dto("b", "/repo", created: created), Dto("a", "/repo", created: created) };
                service.Agents.AddOrUpdate(rows);
                await Assert.That(rail.Repos.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
                service.Agents.AddOrUpdate(rows[1] with { Title = "Updated title", AwaitingInput = true });
                await Assert.That(rail.Repos.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
                service.Agents.Clear();
                service.Agents.AddOrUpdate(rows.Reverse());
                await Assert.That(rail.Repos.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Groups_sessions_under_repository_with_no_repository_last() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/zeta"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a4", null));

                await Assert.That(rail.Repos.Select(r => r.Label))
                    .IsEquivalentTo(["alpha", "zeta", "No repository"], CollectionOrdering.Matching);

                var alpha = rail.Repos[0];
                await Assert.That(alpha.Sessions.Select(row => row.Id)).IsEquivalentTo(["a2", "a3"]);

                var noRepo = rail.Repos[2];
                await Assert.That(noRepo.IsNoRepository).IsTrue();
                await Assert.That(noRepo.Sessions.Select(row => row.Id)).IsEquivalentTo(["a4"]);
            }
        });
    }

    /// `/repo` and `/repo/` are one repository — without separator normalization the rail would
    /// show two same-leaf groups for one checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Trailing_separators_do_not_split_a_repository_group() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/"));

                await Assert.That(rail.Repos).Count().IsEqualTo(1);
                await Assert.That(rail.Repos[0].RootPath).IsEqualTo("path:/dev/alpha");
                await Assert.That(rail.Repos[0].RootDisplay).IsEqualTo("/dev/alpha");
                await Assert.That(rail.Repos[0].Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["a1", "a2"]);
            }
        });
    }

    /// A reviewer that borrowed a checkout belongs on that checkout's node, beside the session it
    /// reviews, including a snapshot reviewer that names its borrowed checkout.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_borrowed_reviewer_files_under_the_worktree_it_reviews() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                const string worktree = "/dev/alpha/wt/agent-1";
                service.Agents.AddOrUpdate(Dto("p1", "/dev/alpha") with { WorktreePath = worktree, WorkLocation = "owned" });
                service.Agents.AddOrUpdate(Dto("r1", "/dev/alpha") with {
                    WorktreePath = worktree, WorkLocation = "borrowed", BorrowedFrom = worktree });
                service.Agents.AddOrUpdate(Dto("s1", "/dev/alpha") with {
                    WorktreePath = "/snapshots/borrowed-1", WorkLocation = "borrowed", BorrowedFrom = worktree });

                var node = rail.Repos.Single();
                await Assert.That(node.Sessions.Select(s => s.Id)).IsEquivalentTo(["p1", "r1", "s1"]);

                await Assert.That(node.Sessions.Single(row => row.Id == "s1").CheckoutPath).IsEqualTo(worktree);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Counts_track_the_cache() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                await Assert.That(rail.IsEmpty).IsTrue();
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha/wt/feature-x"));
                await Assert.That(rail.IsEmpty).IsFalse();
                await Assert.That(rail.Repos[0].CountText).IsEqualTo("2 sessions");

                service.Agents.RemoveKey("a2");
                await Assert.That(rail.Repos[0].CountText).IsEqualTo("1 session");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selecting_a_session_does_not_reorder_the_directory_rows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                var old = Dto("a1", "/dev/alpha", created: DateTime.UtcNow.AddHours(-2));
                service.Agents.AddOrUpdate(old);
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha", created: DateTime.UtcNow.AddHours(-1)));
                await Assert.That(rail.Repos[0].Sessions[0].Id).IsEqualTo("a1");
                await Assert.That(rail.Repos[0].Sessions[1].Id).IsEqualTo("a2");
                rail.SelectedAgentId = "a1";
                await Assert.That(rail.Repos[0].Sessions[0].Id).IsEqualTo("a1");
                await Assert.That(rail.Repos[0].Sessions[0].IsSelected).IsTrue();
                await Assert.That(rail.Repos[0].Sessions[0].Id).IsEqualTo("a1");
                await Assert.That(rail.Repos[0].Sessions[1].Id).IsEqualTo("a2");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repository_collapse_survives_updates_and_group_recreation() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                service.Agents.AddOrUpdate(Dto("b1", "/dev/beta"));
                var alpha = rail.Repos[0];
                await Assert.That(alpha.IsExpanded).IsTrue();
                alpha.ToggleCommand.Execute().Subscribe();
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha"));
                rail.SelectedAgentId = "a2";
                await Assert.That(alpha.IsExpanded).IsFalse();
                await Assert.That(alpha.CountText).IsEqualTo("2 sessions");
                await Assert.That(rail.Repos[1].IsExpanded).IsTrue();
                service.Agents.RemoveKeys(["a1", "a2"]);
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                var recreated = rail.Repos[0];
                await Assert.That(recreated.IsExpanded).IsFalse();
                recreated.ToggleCommand.Execute().Subscribe();
                service.Agents.RemoveKey("a3");
                service.Agents.AddOrUpdate(Dto("a4", "/dev/alpha"));
                await Assert.That(rail.Repos[0].IsExpanded).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Dispose_stops_tracking() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
            rail.Dispose();
            service.Agents.AddOrUpdate(Dto("a2", "/dev/zeta"));
            await Assert.That(rail.Repos).Count().IsEqualTo(1);
        });
    }

    /// A local checkout and a remote agent on the same GitHub repo land in one repo group — the
    /// remote session carries the machine badge, the local one doesn't.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRowsGroupWithLocalRowsOfTheSameRepository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, remote, rail) = Build(originUrl: _ => "git@github.com:kurrent-io/kcap-cli.git");
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                remote.Cache.AddOrUpdate(RemoteDto("b1", "kurrent-io", "kcap-cli"));

                await Assert.That(rail.Repos).Count().IsEqualTo(1);
                await Assert.That(rail.Repos.Single().RootDisplay).IsEqualTo("kurrent-io/kcap-cli");
                var sessions = rail.Repos.Single().Sessions.ToList();
                await Assert.That(sessions).Count().IsEqualTo(2);

                var local = sessions.Single(s => s.Id == "a1");
                var remoteSession = sessions.Single(s => s.Id == "b1");
                await Assert.That(local.MachineBadge).IsNull();
                await Assert.That(remoteSession.MachineBadge).IsEqualTo("work-mac");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RemoteRowOpensInWebNotInWorkspace() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? openedLocal = null;
            string? openedRemote = null;
            var (_, remote, rail) = Build(open: id => openedLocal = id, openRemote: id => openedRemote = id);
            using (rail) {
                remote.Cache.AddOrUpdate(RemoteDto("b1", "o", "r"));

                var session = rail.Repos.Single().Sessions.Single();
                session.OpenCommand.Execute().Subscribe();

                await Assert.That(openedRemote).IsEqualTo("b1");
                await Assert.That(openedLocal).IsNull();
            }
        });
    }

    /// A remote agent with no resolved GitHub identity falls to the "daemon:" group key — its
    /// RootDisplay names the checkout and the daemon, never the raw key.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task RootDisplay_names_a_daemon_scoped_repo_by_path_and_daemon() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, remote, rail) = Build();
            using (rail) {
                service.Agents.AddOrUpdate(Dto("a1", "/dev/alpha"));
                remote.Cache.AddOrUpdate(new AgentInstanceDto {
                    AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                    Vendor = "claude", RepoPath = "/srv/beta",
                });

                var alpha = rail.Repos.Single(r => r.Label == "alpha");
                await Assert.That(alpha.RootDisplay).IsEqualTo("/dev/alpha");

                var beta = rail.Repos.Single(r => r.Label == "beta");
                await Assert.That(beta.RootDisplay).IsEqualTo("/srv/beta on work-mac");
            }
        });
    }
}
