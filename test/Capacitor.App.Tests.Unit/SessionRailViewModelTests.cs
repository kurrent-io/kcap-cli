using System.Globalization;
using System.Reactive.Linq;
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
    public async Task Row_revisions_preserve_commands_and_bindings_and_refresh_all_session_facts() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var opened = new List<string>();
            using var prs = new System.Reactive.Subjects.BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                new Dictionary<string, RailPullRequestSummary> {
                    ["published"] = new([new(Capacitor.Cli.Core.PullRequests.PullRequestWire.Subject(FakePullRequestSource.Link(42)), null, "Open")])
                });
            using var rail = new SessionRailViewModel(directory, opened.Add, _ => { }, TimeProvider.System, pullRequests: prs);
            var row = AgentRow.FromLocal(Dto("a", "/repo") with { SessionId = null }, new RepoIdentity("path:/repo", "repo"));
            directory.Rows.AddOrUpdate(row);
            var session = rail.Groups.Single().Sessions.Single();
            var command = session.OpenCommand;
            rail.SelectedSessionKey = "local:a";
            directory.Rows.AddOrUpdate(row with { Title = "Updated", Model = "new model", SessionId = "published", LiveSubagents = 2, AwaitingInput = false });
            await Assert.That(ReferenceEquals(session, rail.Groups.Single().Sessions.Single())).IsTrue();
            await Assert.That(ReferenceEquals(command, session.OpenCommand)).IsTrue();
            await Assert.That(session.Primary).IsEqualTo("Updated");
            await Assert.That(session.Model).IsEqualTo("new model");
            await Assert.That(session.Meta).EndsWith("2 subagents");
            await Assert.That(session.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(session.IsSelected).IsTrue();
            await Assert.That(session.PullRequestLabel).IsEqualTo("PR #42");
            await session.OpenCommand.Execute();
            await Assert.That(opened).IsEquivalentTo(["a"]);
            directory.Rows.RemoveKey(row.Key);
            prs.OnNext(new Dictionary<string, RailPullRequestSummary>());
            await Assert.That(session.PullRequestLabel).IsEqualTo("PR #42");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_selected_pending_launch_stays_selected_when_published_without_selecting_its_remote_namesake() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var identity = new RepoIdentity("path:/repo", "repo");
            var pending = AgentRow.FromPending(new PendingLaunchDto("same", "claude", "/repo", "Launching", DateTime.UtcNow, "spawned"), identity);
            directory.Rows.AddOrUpdate(pending);
            directory.Rows.AddOrUpdate(AgentRow.FromRemote(RemoteDto("same", "owner", "repo")));
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System);

            rail.SelectedSessionKey = "local:same";
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("pending:same");

            directory.Rows.Edit(rows => {
                rows.RemoveKey(pending.Key);
                rows.AddOrUpdate(AgentRow.FromLocal(Dto("same", "/repo"), identity));
            });
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("local:same");

            rail.SelectedSessionKey = "remote:same";
            await Assert.That(rail.Groups.SelectMany(repo => repo.Sessions).Single(row => row.IsSelected).Key).IsEqualTo("remote:same");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Unrelated_pending_changes_leave_other_repository_and_session_presentations_untouched() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var pending = new System.Reactive.Subjects.BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, pending);
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("a", "/a"), new RepoIdentity("path:/a", "a")));
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("b", "/b"), new RepoIdentity("path:/b", "b")));
            foreach (var repo in rail.Groups) await repo.ToggleCommand.Execute();
            var unaffected = rail.Groups.Single(repo => repo.Label == "b");
            var header = unaffected.HeaderStatus;
            var status = unaffected.Sessions.Single().Status;
            pending.OnNext(new HashSet<string> { "a" });
            await Assert.That(ReferenceEquals(header, unaffected.HeaderStatus)).IsTrue();
            await Assert.That(ReferenceEquals(status, unaffected.Sessions.Single().Status)).IsTrue();
            await Assert.That(rail.Groups.Single(repo => repo.Label == "a").HeaderStatus!.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Session_age_advances_without_directory_revisions() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, time);
            directory.Rows.AddOrUpdate(AgentRow.FromLocal(Dto("a", "/repo", created: time.GetUtcNow().UtcDateTime), new RepoIdentity("path:/repo", "repo")));
            var session = rail.Groups.Single().Sessions.Single();
            await Assert.That(session.Meta).IsEqualTo("0s");
            time.Advance(TimeSpan.FromMinutes(2));
            await Assert.That(session.Meta).IsEqualTo("2m");
        });
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
            foreach (var repo in rail.Groups) repo.ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("same", AgentOrigin.Remote);
            await Assert.That(rail.Groups.Single(r => r.IsExpanded).Label).IsEqualTo("owner/remote");
            rail.Groups.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("same", AgentOrigin.Local);
            await Assert.That(rail.Groups.Single(r => r.IsExpanded).Label).IsEqualTo("local");
            rail.Groups.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("pending", AgentOrigin.Local);
            await Assert.That(rail.Groups.Single(r => r.IsExpanded).Label).IsEqualTo("pending");
            rail.Groups.Single(r => r.IsExpanded).ToggleCommand.Execute().Subscribe();
            rail.NotifySessionOpened("missing", AgentOrigin.Local);
            await Assert.That(rail.Groups.Any(r => r.IsExpanded)).IsFalse();
        });
    }

    /// The default order is newest first, ties by id; a status flip never moves a row, a changed
    /// time does, and the same rows arriving in another order land in the same places.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Newest_first_order_survives_updates_and_reversed_refreshes() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (service, _, rail) = Build();
            using (rail) {
                var created = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
                var rows = new[] { Dto("c", "/repo", created: created.AddHours(1)),
                    Dto("b", "/repo", created: created), Dto("a", "/repo", created: created) };
                service.Agents.AddOrUpdate(rows);
                await Assert.That(rail.Groups.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
                service.Agents.AddOrUpdate(rows[1] with { Title = "Updated title", AwaitingInput = true });
                await Assert.That(rail.Groups.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
                var session = rail.Groups.Single().Sessions.Single(row => row.Id == "b");
                service.Agents.AddOrUpdate(rows[1] with { CreatedAt = created.AddHours(2) });
                await Assert.That(rail.Groups.Single().Sessions.Select(row => row.Id))
                    .IsEquivalentTo(["b", "c", "a"], CollectionOrdering.Matching);
                await Assert.That(ReferenceEquals(session, rail.Groups.Single().Sessions.First())).IsTrue();
                service.Agents.Clear();
                service.Agents.AddOrUpdate(rows.Reverse());
                await Assert.That(rail.Groups.Single().Sessions.Select(s => s.Id))
                    .IsEquivalentTo(["c", "a", "b"], CollectionOrdering.Matching);
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

                await Assert.That(rail.Groups.Select(r => r.Label))
                    .IsEquivalentTo(["alpha", "zeta", "No repository"], CollectionOrdering.Matching);

                var alpha = rail.Groups[0];
                await Assert.That(alpha.Sessions.Select(row => row.Id)).IsEquivalentTo(["a2", "a3"]);

                var noRepo = rail.Groups[2];
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

                await Assert.That(rail.Groups).Count().IsEqualTo(1);
                await Assert.That(rail.Groups[0].Key).IsEqualTo("path:/dev/alpha");
                await Assert.That(rail.Groups[0].RootDisplay).IsEqualTo("/dev/alpha");
                await Assert.That(rail.Groups[0].Sessions.Select(s => s.Id))
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

                var node = rail.Groups.Single();
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
                await Assert.That(rail.Groups[0].CountText).IsEqualTo("2 sessions");

                service.Agents.RemoveKey("a2");
                await Assert.That(rail.Groups[0].CountText).IsEqualTo("1 session");
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
                await Assert.That(rail.Groups[0].Sessions[0].Id).IsEqualTo("a2");
                await Assert.That(rail.Groups[0].Sessions[1].Id).IsEqualTo("a1");
                rail.SelectedSessionKey = "local:a1";
                await Assert.That(rail.Groups[0].Sessions[1].Id).IsEqualTo("a1");
                await Assert.That(rail.Groups[0].Sessions[1].IsSelected).IsTrue();
                await Assert.That(rail.Groups[0].Sessions[0].Id).IsEqualTo("a2");
                await Assert.That(rail.Groups[0].Sessions[1].Id).IsEqualTo("a1");
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
                var alpha = rail.Groups[0];
                await Assert.That(alpha.IsExpanded).IsTrue();
                alpha.ToggleCommand.Execute().Subscribe();
                service.Agents.AddOrUpdate(Dto("a2", "/dev/alpha"));
                rail.SelectedSessionKey = "local:a2";
                await Assert.That(alpha.IsExpanded).IsFalse();
                await Assert.That(alpha.CountText).IsEqualTo("2 sessions");
                await Assert.That(rail.Groups[1].IsExpanded).IsTrue();
                service.Agents.RemoveKeys(["a1", "a2"]);
                service.Agents.AddOrUpdate(Dto("a3", "/dev/alpha"));
                var recreated = rail.Groups[0];
                await Assert.That(recreated.IsExpanded).IsFalse();
                recreated.ToggleCommand.Execute().Subscribe();
                service.Agents.RemoveKey("a3");
                service.Agents.AddOrUpdate(Dto("a4", "/dev/alpha"));
                await Assert.That(rail.Groups[0].IsExpanded).IsTrue();
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
            await Assert.That(rail.Groups).Count().IsEqualTo(1);
        });
    }

    /// The window's close and the app's quit both clear the selection after the app has disposed
    /// the rail; that must not throw.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selection_changes_after_dispose_are_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) {
                rail.SelectedSessionKey = "server:s1";
                rail.Dispose();
                rail.SelectedSessionKey = null;
                rail.SelectedSessionKey = "local:a1";
                await Assert.That(sessions.Kept).IsEqualTo("s1");
            }
        });
    }

    /// A quit can still drive the rail's controls after the app has disposed it; that must not
    /// throw, reach the service or persist.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Control_changes_after_dispose_are_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, persisted, rail) = BuildWithControls();
            using (directory) using (sessions) {
                rail.Dispose();
                rail.SetScopeCommand.Execute(RailScope.Team).Subscribe();
                rail.Scope = RailScope.Team;
                rail.State = RailState.Ended;
                rail.Grouping = RailGrouping.Date;
                rail.Sort = RailSort.Title;
                rail.Descending = !rail.Descending;
                rail.ApplyPreferences(new RailPreferences(RailScope.Team, RailState.Ended, RailGrouping.Date, RailSort.Title, false, []));

                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(persisted).IsEmpty();
            }
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

                await Assert.That(rail.Groups).Count().IsEqualTo(1);
                await Assert.That(rail.Groups.Single().RootDisplay).IsEqualTo("kurrent-io/kcap-cli");
                var sessions = rail.Groups.Single().Sessions.ToList();
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

                var session = rail.Groups.Single().Sessions.Single();
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

                var alpha = rail.Groups.Single(r => r.Label == "alpha");
                await Assert.That(alpha.RootDisplay).IsEqualTo("/dev/alpha");

                var beta = rail.Groups.Single(r => r.Label == "beta");
                await Assert.That(beta.RootDisplay).IsEqualTo("/srv/beta on work-mac");
            }
        });
    }

    // ── controls, grouping, sorting, paging ────────────────────────────────────────────────────

    static readonly DateTime Noon = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

    static AgentRow Server(string id, string status = "ended", DateTime? lastActivity = null, string? title = null, string? repoName = "kcap-cli") =>
        AgentRow.FromServer(new RepoSessionDto {
            SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = title ?? id,
            Owner = new() { UserId = "me", DisplayName = "Alexey" },
            StartedAt = new DateTimeOffset((lastActivity ?? Noon).AddHours(-1)),
            LastActivityAt = new DateTimeOffset(lastActivity ?? Noon),
            EndedAt = status == "ended" ? new DateTimeOffset(lastActivity ?? Noon) : null,
            Repo = repoName is null ? null : new() { Hash = "h", Owner = "kurrent-io", Name = repoName },
        }, hostedPresentation: false);

    static AgentRow Local(string id, string status = "Running", DateTime? created = null, string? title = null, bool? awaiting = null) =>
        AgentRow.FromLocal(Dto(id, "/repo", status, created ?? Noon) with { Title = title ?? id, AwaitingInput = awaiting }, new RepoIdentity("repo:kurrent-io/kcap-cli", "kurrent-io/kcap-cli"));

    static (FakeAgentDirectory Directory, FakeServerSessionsService Sessions, Microsoft.Extensions.Time.Testing.FakeTimeProvider Time, List<RailPreferences> Persisted, SessionRailViewModel Rail)
        BuildWithControls(RailPreferences? preferences = null) {
        var directory = new FakeAgentDirectory();
        var sessions = new FakeServerSessionsService();
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(Noon));
        var persisted = new List<RailPreferences>();
        var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, time,
            openServerSession: _ => { }, sessions: sessions, preferences: preferences, persistPreferences: persisted.Add);
        return (directory, sessions, time, persisted, rail);
    }

    static IEnumerable<string> Labels(SessionRailViewModel rail) => rail.Groups.Select(g => g.Label);
    static IEnumerable<string> Ids(RailGroupViewModel group) => group.Sessions.Select(s => s.Id);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_is_empty_when_the_state_filter_leaves_no_rows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("ended"));
                await Assert.That(rail.IsEmpty).IsFalse();

                rail.State = RailState.Live;

                await Assert.That(rail.IsEmpty).IsTrue();
            }
        });
    }

    /// The PR tone cache reads only what this filter admits, so it must follow the state filter and
    /// the selected row the rail shows whatever the filter says.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_rail_publishes_the_rows_it_shows() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var shown = new System.Reactive.Subjects.ReplaySubject<Func<AgentRow, bool>>(1);
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, shownRows: shown);
            var ended = Server("ended");
            Func<AgentRow, bool>? current = null;
            using var _s = shown.Subscribe(filter => current = filter);
            await Assert.That(current!(ended)).IsTrue();

            rail.State = RailState.Live;
            await Assert.That(current!(ended)).IsFalse();

            rail.SelectedSessionKey = ended.Key;
            await Assert.That(current!(ended)).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Selecting_a_server_row_keeps_its_session_in_the_listing() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                rail.SelectedSessionKey = "server:s1";
                await Assert.That(sessions.Kept).IsEqualTo("s1");
                rail.SelectedSessionKey = "local:a1";
                await Assert.That(sessions.Kept).IsNull();
            }
        });
    }

    /// Two viewers in a row: the earlier one's read, however late, is not the shape to show.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_preferences_load_for_an_earlier_viewer_is_ignored() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var directory = new FakeAgentDirectory();
            var sessions = new FakeServerSessionsService();
            var loads = new Dictionary<string, TaskCompletionSource<RailPreferences>>();
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System, sessions: sessions,
                bindUserPreferences: id => (loads[id] = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task);
            using (directory) using (sessions) using (rail) {
                sessions.ViewerUserIdSubject.OnNext("u1");
                sessions.ViewerUserIdSubject.OnNext("u2");
                loads["u2"].SetResult(RailPreferences.Default with { Scope = RailScope.Team });
                await WorkspaceFixtures.WaitUntilAsync(() => rail.Scope == RailScope.Team, what: "the later viewer's shape");

                loads["u1"].SetResult(RailPreferences.Default with { Grouping = RailGrouping.Date });

                await Task.Delay(100);
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Team);
                await Assert.That(rail.Grouping).IsEqualTo(RailGrouping.Repository);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_named_viewer_applies_that_users_remembered_shape_without_persisting_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var directory = new FakeAgentDirectory();
            var sessions = new FakeServerSessionsService();
            var persisted = new List<RailPreferences>();
            var bound = new List<string>();
            var teams = RailPreferences.Default with { Scope = RailScope.Team, Grouping = RailGrouping.Date };
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                sessions: sessions, persistPreferences: persisted.Add,
                bindUserPreferences: id => { bound.Add(id); return Task.FromResult(teams); });
            using (directory) using (sessions) using (rail) {
                sessions.ViewerUserIdSubject.OnNext("u1");

                await WorkspaceFixtures.WaitUntilAsync(() => rail.Scope == RailScope.Team, what: "the viewer's shape applied");
                await Assert.That(rail.Grouping).IsEqualTo(RailGrouping.Date);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Team);
                await Assert.That(bound).IsEquivalentTo(new[] { "u1" });
                await Assert.That(persisted).IsEmpty();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_locked_scope_stays_on_mine() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var directory = new FakeAgentDirectory();
            var sessions = new FakeServerSessionsService();
            var teams = RailPreferences.Default with { Scope = RailScope.Team, Grouping = RailGrouping.Date };
            var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                sessions: sessions, preferences: teams with { Grouping = RailGrouping.Repository },
                scopeLocked: true, bindUserPreferences: _ => Task.FromResult(teams));
            using (directory) using (sessions) using (rail) {
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(rail.CanChangeScope).IsFalse();
                await Assert.That(rail.ScopeTip).IsEqualTo(SessionRailViewModel.ScopeLockedTip);

                sessions.ViewerUserIdSubject.OnNext("u1");
                await WorkspaceFixtures.WaitUntilAsync(() => rail.Grouping == RailGrouping.Date, what: "the viewer's shape applied");
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);

                rail.SetScopeCommand.Execute(RailScope.Team).Subscribe();
                await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Mine);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Date_grouping_names_today_and_yesterday_and_regroups_at_midnight() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, time, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("today", lastActivity: Noon.AddHours(-3)));
                directory.Rows.AddOrUpdate(Server("yesterday", lastActivity: Noon.AddHours(-14)));

                rail.Grouping = RailGrouping.Date;
                await Assert.That(Labels(rail)).IsEquivalentTo(["Today", "Yesterday"], CollectionOrdering.Matching);
                await Assert.That(rail.Groups[0].Kind).IsEqualTo(RailGrouping.Date);

                time.Advance(TimeSpan.FromHours(12).Add(TimeSpan.FromMinutes(1)));
                await Assert.That(Labels(rail)).IsEquivalentTo(["Yesterday", "Fri 23 Oct"], CollectionOrdering.Matching);
                await Assert.That(rail.Groups[0].RootDisplay).IsEqualTo("Yesterday");
                await Assert.That(rail.Groups[0].Tooltip).StartsWith("Yesterday");
            }
        });
    }

    /// The day key is a Gregorian date whatever calendar the user's culture counts in: it is parsed
    /// back invariantly for the label and persisted as the collapsed-group key.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Date_groups_key_on_the_gregorian_date_under_a_buddhist_calendar_culture() {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try {
            await AvaloniaSession.WithImmediateRxScheduler(async () => {
                var (directory, sessions, _, _, rail) = BuildWithControls();
                using (directory) using (sessions) using (rail) {
                    directory.Rows.AddOrUpdate(Server("today", lastActivity: Noon.AddHours(-3)));

                    rail.Grouping = RailGrouping.Date;

                    await Assert.That(rail.Groups[0].Key).IsEqualTo("day:2026-10-24");
                    await Assert.That(rail.Groups[0].Label).IsEqualTo("Today");
                }
            });
        } finally {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Status_grouping_orders_waiting_running_failed_ended_and_omits_empty_groups() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("ended"));
                directory.Rows.AddOrUpdate(Local("failed", status: "Failed"));
                directory.Rows.AddOrUpdate(Local("running", awaiting: false));
                directory.Rows.AddOrUpdate(Local("waiting", awaiting: true));

                rail.Grouping = RailGrouping.Status;
                await Assert.That(Labels(rail)).IsEquivalentTo(["Waiting on you", "Running", "Failed", "Ended"], CollectionOrdering.Matching);

                directory.Rows.RemoveKey("local:failed");
                await Assert.That(Labels(rail)).IsEquivalentTo(["Waiting on you", "Running", "Ended"], CollectionOrdering.Matching);
            }
        });
    }

    /// The chip reads Working for a parent waiting on input with a subagent still running, so the
    /// group must say Running too.
    [Test]
    public async Task Status_grouping_puts_a_waiting_parent_with_a_live_subagent_under_running() {
        var row = Local("parent", awaiting: true) with { LiveSubagents = 1 };

        await Assert.That(SessionStatusDots.Label(row)).IsEqualTo("Working");
        await Assert.That(SessionRailViewModel.GroupKey(row, RailGrouping.Status, DateOnly.FromDateTime(Noon), TimeZoneInfo.Utc))
            .IsEqualTo("status:1");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task None_grouping_is_one_headerless_group() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("e1"));
                directory.Rows.AddOrUpdate(Local("a1"));

                rail.Grouping = RailGrouping.None;
                var group = rail.Groups.Single();
                await Assert.That(group.ShowsHeader).IsFalse();
                await Assert.That(group.Sessions.Count).IsEqualTo(2);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sort_by_title_ascending_orders_within_a_group_and_descending_flips_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("1", title: "b"));
                directory.Rows.AddOrUpdate(Server("2", title: "A"));
                directory.Rows.AddOrUpdate(Server("3", title: "c"));

                rail.Sort = RailSort.Title;
                rail.Descending = false;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["2", "1", "3"], CollectionOrdering.Matching);
                rail.Descending = true;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["3", "1", "2"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Last_updated_sort_uses_last_activity_then_created() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("s1", lastActivity: Noon.AddHours(-2)));
                directory.Rows.AddOrUpdate(Server("s2", lastActivity: Noon.AddHours(-1)));
                directory.Rows.AddOrUpdate(Local("a1", created: Noon));

                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["a1", "s2", "s1"], CollectionOrdering.Matching);
                rail.Descending = false;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["s1", "s2", "a1"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_filter_hides_ended_rows_except_the_selected_one() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Local("a1"));
                directory.Rows.AddOrUpdate(Server("e1"));
                directory.Rows.AddOrUpdate(Server("e2"));

                rail.State = RailState.Live;
                await Assert.That(Ids(rail.Groups.Single())).IsEquivalentTo(["a1"]);
                rail.SelectedSessionKey = "server:e1";
                await Assert.That(Ids(rail.Groups.Single()).OrderBy(id => id, StringComparer.Ordinal)).IsEquivalentTo(["a1", "e1"], CollectionOrdering.Matching);

                rail.State = RailState.Ended;
                await Assert.That(Ids(rail.Groups.Single()).OrderBy(id => id, StringComparer.Ordinal)).IsEquivalentTo(["e1", "e2"], CollectionOrdering.Matching);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Show_more_loads_ended_sessions_and_the_footer_names_what_is_counted() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                sessions.PagingSubject.OnNext(new ServerSessionsPaging(12, 212, 1, true, false));
                await Assert.That(rail.LoadedText).IsEqualTo("12 of 212 ended sessions loaded");
                await Assert.That(rail.ShowsShowMore).IsTrue();
                await Assert.That(rail.AtCap).IsFalse();

                await rail.ShowMoreCommand.Execute();
                await Assert.That(sessions.ShowMoreCalls).IsEqualTo(1);

                sessions.PagingSubject.OnNext(new ServerSessionsPaging(500, 1000, 25, false, true));
                await Assert.That(rail.ShowsShowMore).IsFalse();
                await Assert.That(rail.AtCap).IsTrue();
                await Assert.That(SessionRailViewModel.CapNote).IsEqualTo("Loading limit reached · search or narrow the scope for older sessions");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Completed_ended_listing_hides_the_count_and_pagination() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                sessions.PagingSubject.OnNext(new ServerSessionsPaging(4, 4, 1, false, false));
                await Assert.That(rail.LoadedText).IsEqualTo("");
                await Assert.That(rail.ShowsShowMore).IsFalse();
                await Assert.That(rail.AtCap).IsFalse();

                sessions.PagingSubject.OnNext(new ServerSessionsPaging(500, 500, 25, false, true));
                await Assert.That(rail.LoadedText).IsEqualTo("");
                await Assert.That(rail.AtCap).IsFalse();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_filter_hides_ended_pagination_and_switching_back_restores_it() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                sessions.PagingSubject.OnNext(new ServerSessionsPaging(20, 84, 1, true, false));
                rail.State = RailState.Live;
                await Assert.That(rail.LoadedText).IsEqualTo("");
                await Assert.That(rail.ShowsShowMore).IsFalse();
                await Assert.That(await rail.ShowMoreCommand.CanExecute.FirstAsync()).IsFalse();

                rail.State = RailState.Ended;
                await Assert.That(rail.LoadedText).IsEqualTo("20 of 84 ended sessions loaded");
                await Assert.That(rail.ShowsShowMore).IsTrue();
                await Assert.That(await rail.ShowMoreCommand.CanExecute.FirstAsync()).IsTrue();

                sessions.PagingSubject.OnNext(new ServerSessionsPaging(500, 1000, 25, false, true));
                rail.State = RailState.Live;
                await Assert.That(rail.LoadedText).IsEqualTo("");
                await Assert.That(rail.AtCap).IsFalse();

                rail.State = RailState.All;
                await Assert.That(rail.LoadedText).IsEqualTo("500 of 1000 ended sessions loaded");
                await Assert.That(rail.AtCap).IsTrue();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_not_shown_renders_the_remainder_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, _, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                await Assert.That(rail.LiveNotShownText).IsNull();
                sessions.LiveNotShownSubject.OnNext(100);
                await Assert.That(rail.LiveNotShownText).IsEqualTo("100 more live sessions · narrow the scope");
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_a_control_persists_the_preferences_and_scope_reaches_the_service() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, persisted, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Local("a1"));

                rail.Scope = RailScope.Team;
                await Assert.That(persisted[^1].Scope).IsEqualTo(RailScope.Team);
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Team);

                rail.Grouping = RailGrouping.Date;
                await Assert.That(persisted[^1].Grouping).IsEqualTo(RailGrouping.Date);

                await rail.Groups.Single().ToggleCommand.Execute();
                await Assert.That(persisted[^1].Collapsed).IsEquivalentTo([rail.Groups.Single().Key]);
                await Assert.That(rail.Preferences).IsEqualTo(persisted[^1]);
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Apply_preferences_rearranges_without_persisting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (directory, sessions, _, persisted, rail) = BuildWithControls();
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Server("e1"));

                rail.ApplyPreferences(new RailPreferences(RailScope.Team, RailState.Ended, RailGrouping.Date, RailSort.Title, false, []));

                await Assert.That(rail.Grouping).IsEqualTo(RailGrouping.Date);
                await Assert.That(rail.Sort).IsEqualTo(RailSort.Title);
                await Assert.That(rail.Descending).IsFalse();
                await Assert.That(sessions.Scope).IsEqualTo(RailScope.Team);
                await Assert.That(Labels(rail)).IsEquivalentTo(["Today"]);
                await Assert.That(persisted).IsEmpty();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Initial_preferences_shape_the_rail_and_remember_collapsed_groups() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var prefs = new RailPreferences(RailScope.Mine, RailState.All, RailGrouping.Repository, RailSort.Created, true, ["repo:kurrent-io/kcap-cli"]);
            var (directory, sessions, _, _, rail) = BuildWithControls(prefs);
            using (directory) using (sessions) using (rail) {
                directory.Rows.AddOrUpdate(Local("a1"));

                await Assert.That(rail.Sort).IsEqualTo(RailSort.Created);
                await Assert.That(rail.Groups.Single().IsExpanded).IsFalse();
            }
        });
    }
}
