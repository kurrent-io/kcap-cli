using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The remote chat host over a scripted lane: the pane follows the lease, which follows the row's
/// session id, and the chat is shown only while the lease's verdict says the session is readable.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteSessionViewModelTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly FakeWorkContextSource WorkSource = new();
        public readonly SessionAccessService Access;
        public readonly FakePermissionService Permissions = new();
        public readonly FakeAgentDirectory Directory = new();
        public readonly AgentActionService Actions;
        public readonly RecordingOpener Opener = new();
        public readonly FakeTimeProvider Time = new();
        public SessionDetailFetch Detail = new(RemoteFixtures.Detail());

        public Harness() {
            Actions = new AgentActionService(new ScriptedLocalControlOps(), new RecordingNotifier(), Opener,
                new ReplaySubject<Cli.Core.LocalIpc.DaemonStatusDto>(1), CancellationToken.None, NeverConfirm.Confirm,
                fallbackServerUrl: "https://a.kcap.ai", lane: Lane);
            Access = new SessionAccessService(Lane, Time);
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        }

        public static AgentRow Row(string id = "a1", string? sessionId = "s1", string status = "Running", string vendor = "gemini") =>
            AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = id, SessionId = sessionId, Status = status, DaemonName = "work-mac",
                Vendor = vendor, OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            });

        /// A row the server lists with no daemon behind it: ended, or live in a terminal.
        public static AgentRow ServerRow(string sessionId = "s1", string status = "ended", bool hosted = false) =>
            AgentRow.FromServer(new Capacitor.Remote.Models.RepoSessionDto {
                SessionId = sessionId, Status = status, AccessLevel = "full", Vendor = "claude", Title = "Desktop app RAM growth",
                Owner = new() { UserId = "u1", DisplayName = "Alexey" },
                StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
                EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch.AddHours(1) : null,
                Repo = new() { Hash = "h", Owner = "kurrent-io", Name = "kcap-cli" },
            }, hosted);

        public readonly FakeRemoteAgents Remote = new();
        public readonly FakeServerSessionsService Sessions = new();
        public readonly RecordingNotifier Notifier = new();

        public RemoteSessionViewModel Build(AgentRow row, IPlanArtifactSource? planArtifacts = null,
                Capacitor.Cli.Core.PullRequests.IPullRequestSource? pullRequests = null, IPlanSource? plans = null,
                BackgroundCommandActivity? commands = null, SubagentEventsReader? readSubagent = null, AccountReadings? readings = null,
                Func<AgentRow, HandoffComposerViewModel>? handoff = null, HandoffOutcomeReader? handoffOutcomes = null) {
            Directory.Rows.AddOrUpdate(row);
            return new RemoteSessionViewModel(row, Directory, Access, Permissions, Actions, Lane, (_, _) => Task.FromResult(Detail), new RecordingOpener(), Time, WorkSource, () => new FakeTerminalSurface(), planArtifacts, commands, pullRequests: pullRequests, plans: plans, readSubagent: readSubagent, readings: readings,
                remoteAgents: Remote, notifier: Notifier, serverSessions: Sessions, handoff: handoff, handoffOutcomes: handoffOutcomes);
        }

        /// One chat poll: the pane reads its feed on the timer this harness owns.
        public async Task TickAsync(RemoteSessionViewModel vm) {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (vm.Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        /// Polls the condition, ticking the chat between checks, since rows land on the poll.
        public async Task UntilAsync(RemoteSessionViewModel vm, Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
                await TickAsync(vm);
                await Task.Delay(10);
            }
        }

        public void Dispose() {
            Access.Dispose();
            Permissions.Dispose();
            Directory.Dispose();
            Remote.Dispose();
            Sessions.Dispose();
        }
    }

    /// The strip's reading comes through the real gate: the service keeps the daemon's accounts,
    /// so only the gate can take the bars away.
    static (RemoteAgentsServiceTests.GateRig Rig, AccountReadings Readings) StripGate() {
        var rig = new RemoteAgentsServiceTests.GateRig("work-mac");
        rig.Accounts = [StripReadings.Remote("work-mac", StripReadings.Reading(StripReadings.Window(300, 42, TimeSpan.FromHours(2))))];
        var readings = new AccountReadings(
            Observable.Return<Capacitor.Cli.Core.Accounts.AccountsSnapshotDto?>(null), rig.Service.DrawableDaemonAccounts,
            Observable.Return(StripReadings.Now));
        return (rig, readings);
    }

    static AgentRow StripRow() => Harness.Row(vendor: "claude") with {
        AccountDirectory = StripReadings.Directory, AccountId = "acc1", AccountLabel = "work",
    };

    [Test]
    public async Task Remote_strip_drops_bars_on_lane_loss() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var (rig, readings) = StripGate();
            using var _ = rig;
            var vm = h.Build(StripRow(), readings: readings);
            try {
                rig.Connect();
                await WaitUntilAsync(() => vm.Chat.ShowsAccountUsage, what: "the bars");
                await Assert.That(vm.Chat.AccountStripUsage!.Bars[0].PercentText).IsEqualTo("42%");

                rig.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
                await WaitUntilAsync(() => !vm.Chat.ShowsAccountUsage, what: "the bars to go");

                await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
                await Assert.That(vm.Chat.ShowsAccountDirectory).IsTrue();
                await Assert.That(vm.Chat.AccountReadingNote).IsNull();
            } finally { await vm.TeardownAsync(); }
        });
    }

    [Test]
    public async Task Remote_strip_drops_bars_when_the_daemon_leaves_and_reopens_after_a_fresh_fetch() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var (rig, readings) = StripGate();
            using var _ = rig;
            var vm = h.Build(StripRow(), readings: readings);
            try {
                rig.Connect();
                await WaitUntilAsync(() => vm.Chat.ShowsAccountUsage, what: "the bars");

                rig.Listing = [];
                await rig.RelistAsync();
                await WaitUntilAsync(() => !vm.Chat.ShowsAccountUsage, what: "the bars to go");
                await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
                await Assert.That(vm.Chat.ShowsAccountDirectory).IsTrue();

                rig.Listing = [RemoteAgentsServiceTests.Host("work-mac", connectedAt: RemoteAgentsServiceTests.FirstConnect.AddMinutes(5))];
                await rig.RelistAsync();
                await WaitUntilAsync(() => vm.Chat.ShowsAccountUsage, what: "the bars to return");
            } finally { await vm.TeardownAsync(); }
        });
    }

    [Test]
    [Arguments("u1", "You")]
    [Arguments("u2", "User")]
    [Arguments(null, "User")]
    [Arguments("", "User")]
    public async Task Chat_label_compares_daemon_owner_with_signed_in_account(string? owner, string expected) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = Harness.Row() with { OwnerUserId = owner };
            var vm = h.Build(row);
            await WaitUntilAsync(() => vm.Chat.UserTitle == expected, what: "chat ownership label");
            await Assert.That(vm.Chat.UserTitle).IsEqualTo(expected);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Chat_label_follows_account_and_owner_changes() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = Harness.Row();
            var vm = h.Build(row);
            await WaitUntilAsync(() => vm.Chat.UserTitle == "You", what: "own daemon label");

            h.Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2", Epoch: 2));
            await WaitUntilAsync(() => vm.Chat.UserTitle == "User", what: "different account label");
            h.Directory.Rows.AddOrUpdate(row with { OwnerUserId = "u2" });
            await WaitUntilAsync(() => vm.Chat.UserTitle == "You", what: "updated daemon owner label");

            h.Lane.StatusSubject.OnNext(new(ServerLaneState.SignedOut, Epoch: 3));
            await WaitUntilAsync(() => vm.Chat.UserTitle == "User", what: "unknown account label");
            await vm.TeardownAsync();
        });
    }

    static WorkContextRead LinkedWork(string id, string title) => WorkContextRead.Of(WorkContextReadKind.Ready) with {
        Primary = new SessionWorkItemAssignmentDto { WorkItemId = id, Label = title, IsPrimary = true },
        Item = new WorkItemDto { WorkItemId = id, Title = title },
    };

    [Test]
    public async Task Remote_work_context_loads_the_linked_item_and_follows_session_changes() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.WorkSource.Enqueue(LinkedWork("w1", "First task"), LinkedWork("w2", "Second task"));
            var plans = new FakePlanSource();
            var prs = new FakePullRequestSource(h.Time);
            var row = Harness.Row() with { RepoPath = "/remote/repository" };
            var vm = h.Build(row, pullRequests: prs, plans: plans);
            await WaitUntilAsync(() => vm.WorkContext.IsReady, what: "remote work item loaded");
            await Assert.That(vm.WorkContext.IsReady).IsTrue();
            await Assert.That(vm.WorkContext.Title).IsEqualTo("First task");
            await Assert.That(vm.WorkContext.Repository).IsEqualTo("repository");
            await Assert.That(vm.WorkContext.WorktreePath).IsNull();
            await Assert.That(vm.WorkContext.Branch).IsEqualTo("—");
            await Assert.That(vm.WorkContext.PullRequests).IsSameReferenceAs(vm.PullRequests);
            await Assert.That(plans.Requested).Contains("s1");

            h.Directory.Rows.AddOrUpdate(row with { SessionId = "s2" });
            await WaitUntilAsync(() => vm.WorkContext.Title == "Second task", what: "replacement session work item loaded");
            await Assert.That(vm.WorkContext.SessionIdText).IsEqualTo("s2");
            await Assert.That(vm.WorkContext.Title).IsEqualTo("Second task");
            await Assert.That(h.WorkSource.Requested).IsEquivalentTo(new[] { "s1", "s2" });
            await Assert.That(plans.Requested).Contains("s2");
            h.Directory.Rows.AddOrUpdate(row with { SessionId = null });
            await WaitUntilAsync(() => !vm.WorkContext.HasSession, what: "session identity cleared");
            await Assert.That(vm.WorkContext.Title).IsEqualTo("");
            await Assert.That(vm.WorkContext.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);
            await vm.TeardownAsync();
            var count = h.WorkSource.Requested.Count;
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.WorkSource.Requested.Count).IsEqualTo(count);
        });
    }

    [Test]
    public async Task Remote_work_context_waits_for_the_session_and_refreshes_the_linked_item() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = Harness.Row(sessionId: null);
            var vm = h.Build(row);
            await Assert.That(vm.WorkContext.HasSession).IsFalse();
            await Assert.That(h.WorkSource.Requested).IsEmpty();
            h.WorkSource.Enqueue(LinkedWork("w1", "Initial title"), LinkedWork("w1", "Updated title"));
            h.Directory.Rows.AddOrUpdate(row with { SessionId = "s1" });
            await WaitUntilAsync(() => vm.WorkContext.IsReady, what: "session identity resolved");
            vm.WorkContext.RefreshCommand.Execute().Subscribe();
            await WaitUntilAsync(() => vm.WorkContext.Title == "Updated title", what: "work context refreshed");
            await Assert.That(h.WorkSource.Requested.Count).IsEqualTo(2);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Remote_work_context_teardown_cancels_an_in_flight_read() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.WorkSource.Gate();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => h.WorkSource.InFlight == 1, what: "work context read started");
            await vm.TeardownAsync();
            await Assert.That(h.WorkSource.InFlight).IsEqualTo(0);
        });
    }

    /// A subagent row opens that subagent's own events in place of the chat, read-only, and Back returns to the chat.
    /// A pane open when the agent moves to another session closes, since its subagent belongs to the old one.
    [Test]
    public async Task A_subagent_row_opens_its_own_transcript_in_place_of_the_chat_until_back() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var requested = new List<(string Session, string Agent)>();
            SubagentEventsReader read = (session, agent, _, _) => {
                requested.Add((session, agent));
                return Task.FromResult(new SubagentEventsFetch(new SubagentEventsDto {
                    Events = [Event(0, CanonicalEventTypes.AssistantTextGenerated, """{"content":"Found the chat surfaces"}""")],
                }));
            };
            var agentRow = Harness.Row(vendor: "claude");
            var vm = h.Build(agentRow, readSubagent: read);
            await h.UntilAsync(vm, () => vm.WorkContext.Runs.Any(r => r.CanOpen), "subagent row named");
            var row = vm.WorkContext.Runs.Single();

            vm.WorkContext.OpenRunCommand.Execute(row).Subscribe();

            await Assert.That(vm.ShowsSubagentPane).IsTrue();
            await Assert.That(vm.ShowsChatPane).IsFalse();
            var pane = vm.Subagent!;
            await Assert.That(pane.Chat.IsReadOnlyParticipant).IsTrue();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!pane.Chat.Items.OfType<AssistantTextItem>().Any() && DateTime.UtcNow < deadline) {
                h.Time.Advance(ChatTabViewModel.PollInterval);
                await (pane.Chat.PendingReadForTesting ?? Task.CompletedTask);
            }
            await Assert.That(pane.Chat.Items.OfType<AssistantTextItem>().Single().Text).IsEqualTo("Found the chat surfaces");
            await Assert.That(requested[0]).IsEqualTo(("s1", "a9f262478e032f427"));
            await Assert.That(vm.WorkContext.Runs.Single()).IsSameReferenceAs(row);

            pane.BackCommand.Execute().Subscribe();

            await Assert.That(vm.Subagent).IsNull();
            await Assert.That(vm.ShowsChatPane).IsTrue();

            vm.WorkContext.OpenRunCommand.Execute(row).Subscribe();
            await Assert.That(vm.Subagent).IsNotNull();
            h.Directory.Rows.AddOrUpdate(agentRow with { SessionId = "s2" });
            await h.UntilAsync(vm, () => vm.Subagent is null, "pane closed on the session change");
            await vm.TeardownAsync();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Missing_identity_or_denied_access_clears_work_prs_plans_artefacts_and_runs(bool denied) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.WorkSource.Default = LinkedWork("w1", "Private task");
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var plans = new FakePlanSource { Default = new(SessionPlansReadKind.Ready, [new SessionPlanDto {
                PlanId = "p1", IsCurrent = true,
                Tasks = [new PlanLedgerTaskDto { TaskId = "t1", Title = "Private step", Ordinal = 0, Status = "pending" }],
            }]) };
            var artefacts = new FakePlanArtifactSource { Default = new(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto {
                Artifacts = [new PlanArtifactDto {
                    ArtifactId = "a1", Kind = "plan", Title = "Private plan", Source = "declared", SessionId = "s1", Path = "docs/p.md",
                    Content = "Private content", ContentState = "ok", IsComplete = true, IsConfirmed = true, ContentHash = "h", Version = 1,
                    DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = "declared", IsPrimary = true,
                }],
            }) };
            var prs = new FakePullRequestSource(h.Time);
            var row = Harness.Row(vendor: "claude");
            var vm = h.Build(row, artefacts, prs, plans);
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready && vm.WorkContext.IsReady
                && vm.WorkContext.Plan.HasPlan && vm.Artefacts.HasAny && vm.PullRequests.HasPullRequest, what: "all context loaded");
            await h.UntilAsync(vm, () => vm.WorkContext.HasRuns, "subagent loaded");
            await WaitUntilAsync(() => !vm.WorkContext.IsReading, what: "initial work read settled");
            h.WorkSource.Gate();
            vm.WorkContext.RefreshCommand.Execute().Subscribe();
            await WaitUntilAsync(() => h.WorkSource.InFlight == 1, what: "refresh pending");
            // Under the immediate scheduler the verdict below clears the sections off the UI thread,
            // racing a settle the refresh dispatched there; settled reads have nothing left to re-apply.
            await (vm.WorkContext.Plan.PendingReadForTesting ?? Task.CompletedTask);
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            if (denied) {
                h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
                h.Lane.SessionAccessChangedSubject.OnNext("s1");
                await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Denied, what: "access denied");
            } else h.Directory.Rows.AddOrUpdate(row with { SessionId = null });
            await WaitUntilAsync(() => !vm.WorkContext.HasSession && h.WorkSource.InFlight == 0, what: "context cleared and read cancelled");
            await Assert.That(vm.ShowsPanes).IsFalse();
            await Assert.That(vm.WorkContext.Title).IsEqualTo("");
            await Assert.That(vm.WorkContext.HasRuns).IsFalse();
            await Assert.That(vm.WorkContext.Plan.HasPlan).IsFalse();
            await Assert.That(vm.Artefacts.HasAny).IsFalse();
            await Assert.That(vm.PullRequests.Choices.Count).IsEqualTo(0);
            await Assert.That(vm.PullRequests.Selected).IsNull();
            await Assert.That(vm.ShowsPullRequestTab).IsFalse();
            var reads = h.WorkSource.Requested.Count;
            var lists = prs.Lists;
            if (denied) h.Directory.Rows.AddOrUpdate(row with { Title = "Updated registry" });
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Task.Yield();
            await Assert.That(h.WorkSource.Requested.Count).IsEqualTo(reads);
            await Assert.That(prs.Lists).IsEqualTo(lists);
            if (denied) {
                h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
                h.Lane.SessionAccessChangedSubject.OnNext("s1");
            } else h.Directory.Rows.AddOrUpdate(row);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready && vm.WorkContext.IsReady
                && vm.PullRequests.HasPullRequest, what: "context restored");
            await vm.TeardownAsync();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Reconnect_refreshes_silently_without_resetting_stopped_pr_retries(bool stopped) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.WorkSource.Default = LinkedWork("w1", "Task");
            var prs = new FakePullRequestSource(h.Time) { ListFailure = stopped ? "retries_stopped" : null };
            var vm = h.Build(Harness.Row(), pullRequests: prs);
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready && vm.WorkContext.IsReady
                && !vm.PullRequests.IsReading, what: "initial reads finished");
            var lists = prs.Lists;
            var reads = h.WorkSource.Requested.Count;
            h.Time.Advance(TimeSpan.FromSeconds(15));
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready && h.WorkSource.Requested.Count > reads
                && !vm.PullRequests.IsReading, what: "reconnect reads finished");
            await Assert.That(prs.Resets).IsEqualTo(0);
            await Assert.That(prs.Lists).IsEqualTo(stopped ? lists : lists + 1);
            await Assert.That(vm.WorkContext.IsRefreshing).IsFalse();
            await Assert.That(vm.PullRequests.ShowsRefreshing).IsFalse();
            if (stopped) await Assert.That(vm.PullRequests.Notice).Contains("Use Retry");
            await vm.TeardownAsync();
        });
    }

    /// The branch is what lets the PR readers find a PR opened since the listing was read.
    [Test]
    public async Task A_listed_sessions_branch_reaches_the_pr_readers() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var prs = new FakePullRequestSource(h.Time);
            var vm = h.Build(Harness.ServerRow("s1", "active") with { Branch = "feature/x" }, pullRequests: prs);
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => prs.Descriptions.Count > 0, what: "session described");
            await Assert.That(prs.Descriptions[^1].Branch).IsEqualTo("feature/x");
            await vm.TeardownAsync();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Initial_pr_loading_keeps_the_tab_visible_and_an_empty_result_returns_to_chat(bool failed) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var source = new FakePullRequestSource(h.Time) { Links = [],
                Capability = failed ? Capacitor.Cli.Core.PullRequests.PullRequestCapabilityKind.Unavailable : Capacitor.Cli.Core.PullRequests.PullRequestCapabilityKind.Supported };
            var vm = h.Build(Harness.Row(), pullRequests: source);
            vm.OpenPullRequest();
            await Assert.That(vm.IsPullRequestActive).IsTrue();
            await Assert.That(vm.ShowsPullRequestTab).IsTrue();
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => failed ? vm.PullRequests.Notice is { } note && note != "Loading pull requests…" : vm.PullRequests.HasListed, what: "first list settled");
            await Assert.That(vm.IsPullRequestActive).IsEqualTo(failed);
            await Assert.That(vm.ShowsPullRequestTab).IsEqualTo(failed);
            await Assert.That(vm.IsChatActive).IsEqualTo(!failed);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_remote_session_opens_the_native_pr_reader_and_keeps_the_picker() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var source = new FakePullRequestSource(h.Time);
            var vm = h.Build(Harness.Row(), pullRequests: source);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            vm.PullRequests!.SetForeground(true);
            vm.OpenPullRequest(Capacitor.Cli.Core.PullRequests.PullRequestWire.Subject(source.Links[0]));
            await WaitUntilAsync(() => vm.PullRequests.CanReveal && !vm.PullRequests.IsReading, what: "PR reader");
            await Assert.That(vm.IsPullRequestActive).IsTrue();
            await Assert.That(vm.ShowsPullRequestPane).IsTrue();
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.ShowsPullRequestTab).IsTrue();
            await Assert.That(vm.PullRequests.Choices).Count().IsEqualTo(2);
            await Assert.That(vm.PullRequests.Selected!.Subject.Number).IsEqualTo(1);
            vm.PullRequests.ShowSectionCommand.Execute("checks").Subscribe();
            await WaitUntilAsync(() => vm.PullRequests.Rows.Count > 0 && !vm.PullRequests.IsReading, what: "checks loaded");
            var rows = vm.PullRequests.Rows.ToArray();
            var pages = source.Pages;
            vm.ShowChatCommand.Execute().Subscribe();
            await Assert.That(vm.ShowsChatPane).IsTrue();
            var lists = source.Lists;
            var overviews = source.Overviews;
            h.Time.Advance(TimeSpan.FromSeconds(4));
            vm.ShowPullRequestCommand.Execute().Subscribe();
            await Assert.That(vm.IsPullRequestActive).IsTrue();
            await Assert.That(vm.PullRequests.Selected!.Subject.Number).IsEqualTo(1);
            await Assert.That(source.Lists).IsEqualTo(lists);
            await Assert.That(source.Overviews).IsEqualTo(overviews);
            await Assert.That(source.Pages).IsEqualTo(pages);
            await Assert.That(vm.PullRequests.Section).IsEqualTo("checks");
            await Assert.That(vm.PullRequests.Rows).IsEquivalentTo(rows);
            await vm.TeardownAsync();
        });
    }

    /// The directory already folded the owner through the account's aliases; the raw owner id is
    /// an older spelling of the same viewer.
    [Test]
    public async Task A_server_row_the_listing_marks_as_the_viewers_reads_as_owned() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow("s1") with { OwnerUserId = "legacy-id", OwnerIsViewer = true });
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            await Assert.That(vm.Chat.UserTitle).IsEqualTo("You");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_session_only_workspace_cannot_stop() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow("s1", "active"));

            await Assert.That(await vm.StopCommand.CanExecute.FirstAsync()).IsFalse();
            await vm.TeardownAsync();
        });
    }

    /// The listing shares a teammate's session at the level the server grants; below full there is no
    /// chat to subscribe to, and the note says so instead of reporting a lost access.
    [Test]
    public async Task A_shared_server_row_below_full_access_opens_without_a_chat_lease() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow("s1") with { AccessLevel = "overview" });

            await Task.Delay(100);
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.Denied);
            await Assert.That(vm.AccessNote).Contains("overview");
            await Assert.That(h.Lane.ChatSubscribes).DoesNotContain("s1");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Open_in_web_on_a_session_only_workspace_opens_the_session_page() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow("s1"));

            vm.OpenInWebCommand.Execute().Subscribe();

            await Assert.That(h.Opener.Opened).Count().IsEqualTo(1);
            await Assert.That(h.Opener.Opened[0]).IsEqualTo("https://a.kcap.ai/sessions/s1");
            await vm.TeardownAsync();
        });
    }

    /// A terminal or ended session has no agent here to answer for it: its server-lane requests
    /// render no card in the read-only chat.
    [Test]
    public async Task A_session_only_workspace_renders_no_permission_cards() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow("s1", "active"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Permissions.Add(PermissionEntries.ServerEntry(sessionId: "s1"));

            await Task.Delay(100);
            await Assert.That(vm.Chat.Cards.HasPendingCards).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Opening_a_remote_row_establishes_access_and_shows_its_server_lane_cards() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(h.Lane.ChatSubscribes).Contains("s1");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(vm.AccessNote).IsEqualTo("");

            var card = PendingPermissionRequest.FromServer(new ServerElicitationRequest("s1", "q1", "Pick", [], false), DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            h.Permissions.Add(card);

            await WaitUntilAsync(() => vm.Chat.Cards.HasPendingCards, what: "the card");
            await Assert.That(vm.RepoLabelText).Contains("work-mac");
            await vm.TeardownAsync();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "released on teardown");
        });
    }

    [Test]
    public async Task A_revocation_hides_the_cards_and_a_reconnect_rechecks_access() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            h.Lane.SessionAccessChangedSubject.OnNext("s1");
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Denied, what: "denied");
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("You no longer have access to this session");

            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready after reconnect");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_row_without_a_session_waits_and_a_removed_row_ends_the_session() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(id: "a2", sessionId: null));
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.NoSession);
            await Assert.That(vm.AccessNote).IsEqualTo("Waiting for the session to start");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(h.Lane.AccessWatches).IsEmpty();

            h.Directory.Rows.Remove("remote:a2");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.AccessNote).IsEqualTo("The agent ended before its session started");
            await vm.TeardownAsync();
        });
    }

    /// An agent already finished when it is opened had a session, so nothing says it is waiting for
    /// one, or for chat.
    [Test]
    public async Task A_finished_agent_with_a_session_neither_waits_nor_claims_it_never_started() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(status: "Completed", vendor: "claude"));
            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await WaitUntilAsync(() => vm.Chat.PhaseNote == "", what: "the ended chat's note cleared");
            await vm.TeardownAsync();
        });
    }

    /// A row that loses its session id, as a daemon re-registering does for a moment, keeps the
    /// session's rows in the chat, so the chat is not shown under the note as if before a session.
    [Test]
    public async Task A_row_that_loses_its_session_id_hides_the_chat_it_had() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = Harness.Row(vendor: "claude");
            var vm = h.Build(row);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(vm.ShowsChatPane).IsTrue();

            h.Directory.Rows.AddOrUpdate(row with { SessionId = null });
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.NoSession, what: "no session");
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("Waiting for the session to start");
            await vm.TeardownAsync();
        });
    }

    /// A startup dialog holds the session back and only the terminal can show it, but a normal start
    /// has no session for a few seconds too: the empty chat offers the terminal rather than switching
    /// to it, opening it asks the server, and the session's start leaves a live terminal as it is.
    [Test]
    public async Task Before_the_session_the_chat_offers_the_terminal_and_it_carries_on_when_the_session_starts() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = Harness.Row(sessionId: null, vendor: "claude");
            var vm = h.Build(row);
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(vm.AccessNote).IsEqualTo("Waiting for the session to start");
            await Assert.That(vm.Chat.ShowsTerminalOffer).IsTrue();
            await Assert.That(await vm.Chat.SendCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(vm.ShowsTerminalPane).IsFalse();
            await Task.Delay(50);
            await Assert.That(h.Lane.TerminalSubscribes).IsEmpty();

            await vm.Chat.ShowTerminalCommand.Execute();
            await Assert.That(vm.ShowsTerminalPane).IsTrue();
            await WaitUntilAsync(() => vm.Terminal!.Phase == RemoteTerminalPhase.Live, what: "the terminal live");
            var surface = vm.Terminal!.Surface;

            h.Directory.Rows.AddOrUpdate(row with { SessionId = "s1" });
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(vm.ShowsTerminalPane).IsTrue();
            await Assert.That(vm.Terminal!.Phase).IsEqualTo(RemoteTerminalPhase.Live);
            await Assert.That(vm.Terminal!.Surface).IsSameReferenceAs(surface);
            await Assert.That(h.Lane.TerminalSubscribes.Count).IsEqualTo(1);
            await vm.TeardownAsync();
        });
    }

    /// A transcript that loads with no rows is still waiting for the agent, so it says so and offers
    /// the terminal.
    [Test]
    public async Task A_transcript_that_loads_empty_waits_and_offers_the_terminal() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Chat.Phase == ChatTabPhase.Reading, what: "the empty transcript read");
            await Assert.That(vm.Chat.Items).IsEmpty();
            await Assert.That(vm.Chat.PhaseNote).IsEqualTo("Waiting for chat…");
            await Assert.That(vm.Chat.ShowsTerminalOffer).IsTrue();
            await vm.TeardownAsync();
        });
    }

    /// The agent's own session id can arrive before anything is recorded under it, so the chat's
    /// note offers the terminal, whatever access says; an agent that ends, or one with no terminal,
    /// offers nothing.
    [Test]
    public async Task An_empty_chat_offers_the_terminal_until_the_agent_ends() {
        await RunOnUiAsync(async () => {
            using var h = new Harness { Detail = new(null, NotFound: true) };
            var row = Harness.Row(vendor: "claude");
            var vm = h.Build(row);
            await WaitUntilAsync(() => vm.Chat.PhaseNote == RemoteSessionViewModel.MissingNote, what: "no transcript yet");
            await Assert.That(vm.Chat.ShowsTerminalOffer).IsTrue();

            h.Directory.Rows.AddOrUpdate(row with { Status = "Completed" });
            await WaitUntilAsync(() => vm.SessionEnded, what: "ended");
            await Assert.That(vm.Chat.ShowsTerminalOffer).IsFalse();
            await vm.TeardownAsync();

            var noTerminal = h.Build(Harness.Row(id: "a2", vendor: "gemini"));
            await Assert.That(noTerminal.Chat.ShowsTerminalOffer).IsFalse();
            await noTerminal.TeardownAsync();
        });
    }

    /// Whether the viewer may see the terminal is the server's answer, so a refusal shows in the
    /// pane rather than being guessed in advance.
    [Test]
    public async Task Before_the_session_a_viewer_the_server_refuses_sees_why_in_the_terminal() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Lane.TerminalSubscribeHandler = _ => Task.FromResult(HubCallOutcome.Denied("not admitted to the terminal"));
            var vm = h.Build(Harness.Row(sessionId: null, vendor: "claude"));

            await vm.ShowTerminalCommand.Execute();
            await WaitUntilAsync(() => vm.Terminal!.Phase == RemoteTerminalPhase.Refused, what: "refused");
            await Assert.That(vm.ShowsTerminalPane).IsTrue();
            await Assert.That(vm.Terminal!.PhaseNote).IsEqualTo(RemoteTerminalViewModel.RefusedNote);
            await vm.TeardownAsync();
        });
    }

    /// A session id that only arrives with a later row revision still gets its lease, a change of
    /// id moves the lease with it, and a terminal status gives it back.
    [Test]
    public async Task The_lease_follows_the_rows_session_id_and_a_terminal_status_releases_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(sessionId: null));
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.NoSession);

            h.Directory.Rows.AddOrUpdate(Harness.Row(sessionId: "s2"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready on the late session id");

            h.Directory.Rows.AddOrUpdate(Harness.Row(sessionId: "s3"));
            await WaitUntilAsync(() => h.Lane.ChatSubscribes.Contains("s3") && vm.Access == RemoteSessionAccess.Ready,
                what: "re-acquired on the new session id");
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s2"), what: "the old session released");

            h.Directory.Rows.AddOrUpdate(Harness.Row(sessionId: "s3", status: "Completed"));
            await Assert.That(vm.SessionEnded).IsTrue();
            // The transcript stays while the lease's last verdict stands: an ended session is still
            // readable, and giving the lease back is what stops new rows arriving.
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s3"), what: "released on the terminal status");
            await vm.TeardownAsync();
        });
    }

    /// A registry snapshot that briefly holds no rows removes this one; the refresh behind it
    /// re-adds the same live session, and the pane has to come back with it.
    [Test]
    public async Task A_row_that_comes_back_reopens_the_session_it_was_showing() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.Remove("remote:a1");
            await Assert.That(vm.SessionEnded).IsTrue();

            h.Directory.Rows.AddOrUpdate(Harness.Row());

            await Assert.That(vm.SessionEnded).IsFalse();
            await WaitUntilAsync(() => h.Lane.ChatSubscribes.Count == 2, what: "the lease re-acquired");
            await WaitUntilAsync(() => vm.ShowsChatPane, what: "the pane back");
            await vm.TeardownAsync();
        });
    }

    /// A transient empty snapshot also removes a live row, so only a removal after the server
    /// accepted this workspace's stop, or a live session turning terminal, reports the end.
    [Test]
    [Arguments(null, "Running", false, false)]
    [Arguments(HubCallResult.Ok, "Running", false, true)]
    [Arguments(HubCallResult.Denied, "Running", false, false)]
    [Arguments(null, "Running", true, true)]
    [Arguments(null, "Completed", true, false)]
    public async Task Only_an_accepted_stop_or_a_live_session_ending_reports_the_end(
            HubCallResult? stop, string openStatus, bool terminal, bool ends) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            if (stop is { } result) h.Lane.StopHandler = _ => Task.FromResult(new HubCallOutcome(result));
            var vm = h.Build(Harness.Row(status: openStatus));
            var ended = false;
            using var sub = vm.Ended.Subscribe(_ => ended = true);

            if (stop is not null) {
                await vm.StopCommand.Execute();
                await WaitUntilAsync(async () => (await h.Actions.StopsInFlight.FirstAsync()).Count == 0, what: "the stop settled");
            }
            if (terminal) h.Directory.Rows.AddOrUpdate(Harness.Row(status: "Completed"));
            else h.Directory.Rows.Remove("remote:a1");

            if (ends) await WaitUntilAsync(() => ended, what: "the end");
            else await Assert.That(ended).IsFalse();
            await vm.TeardownAsync();
        });
    }

    /// The local daemon proving the twin retires the remote row while the agent keeps running:
    /// nothing ended, and the window opens the local workspace for the same id instead.
    [Test]
    public async Task A_row_replaced_by_its_local_twin_reports_the_origin_change_rather_than_an_end() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.ProvenTwins.Add("a1");
            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.OriginChangedToLocal).IsTrue();
            await Assert.That(vm.SessionEnded).IsFalse();
            // Nothing is readable or stoppable through the released lease.
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo(RemoteSessionViewModel.OriginChangedNote);
            await Assert.That(await vm.StopCommand.CanExecute.FirstAsync()).IsFalse();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "the lease released");
            await vm.TeardownAsync();
        });
    }

    /// A proven twin is not enough on its own: the dedup fails open, so an unrelated local agent
    /// can carry this id. Only a matching session id makes it a move; calling anything else an
    /// origin change would point the user at a process that has nothing to do with it.
    [Test]
    public async Task A_same_id_local_row_for_another_session_is_not_an_origin_change() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.ProvenTwins.Add("a1");
            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "a-different-session"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.OriginChangedToLocal).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await vm.TeardownAsync();
        });
    }

    /// A matching session id on a same-id local row is still not proof. The local daemon also has
    /// to be the twin of the daemon this agent is registered on, and the directory is the only
    /// thing that knows it — on another server the same session id names another session, and
    /// calling that an origin change points the user at a process that is not this agent.
    [Test]
    public async Task A_local_row_the_directory_does_not_call_a_twin_is_not_an_origin_change() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.OriginChangedToLocal).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await vm.TeardownAsync();
        });
    }

    /// The server is the winning authority while the local lane is down, and Completed is its
    /// verdict that the session ended. The retained local row reappearing as the remote one retires
    /// is history: reporting a move would point the user at a process that is not running this
    /// session any more.
    [Test]
    public async Task A_terminal_remote_status_ends_the_session_even_for_a_proven_twin() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.ProvenTwins.Add("a1");
            h.Directory.Rows.AddOrUpdate(Harness.Row(status: "Completed"));
            await Assert.That(vm.SessionEnded).IsTrue();

            h.Directory.Rows.AddOrUpdate(AgentRow.FromLocal(
                Agent("a1", "gemini", hasTerminal: true, "/repos/kcap-cli", sessionId: "s1"),
                new RepoIdentity("path:/repos/kcap-cli", "kcap-cli")));
            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.OriginChangedToLocal).IsFalse();
            await Assert.That(vm.AccessNote).IsEqualTo("");
            await vm.TeardownAsync();
        });
    }

    /// A row that leaves the directory takes its hub subscriptions with it, whether or not the
    /// pane is torn down afterwards.
    [Test]
    public async Task A_removed_row_releases_the_lease_it_held() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.Remove("remote:a1");

            await Assert.That(vm.SessionEnded).IsTrue();
            await WaitUntilAsync(() => h.Lane.ChatUnsubscribes.Contains("s1"), what: "released on the removed row");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task The_chat_seeds_from_the_session_detail_and_follows_the_stream() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
                Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.Items.Count == 2, "the seeded rows");
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(vm.Chat.Items[0]).IsTypeOf<UserTurnItem>();
            await Assert.That(vm.Chat.Items[1]).IsTypeOf<AssistantTextItem>();

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
            await h.UntilAsync(vm, () => vm.Chat.Items.Count == 3, "the live row");
            await Assert.That(vm.Chat.Items[2]).IsTypeOf<ToolGroupItem>();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_sent_prompt_waits_in_the_queue_until_the_stream_echoes_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            // Reading alone is not the seed: a poll before the detail fetch lands reads empty and still sets it.
            await h.UntilAsync(vm, () => vm.Chat.HasSeed, "the empty seed");
            await WaitUntilAsync(() => vm.Chat.ShowsComposer && vm.Chat.ComposerHint.StartsWith("Enter sends", StringComparison.Ordinal), what: "the composer");

            vm.Chat.ComposerText = "do it";
            await vm.Chat.SendCommand.Execute();
            await Assert.That(h.Lane.UserInputs).Contains(("a1", "do it"));
            await Assert.That(vm.Chat.QueuedMessages.Count).IsEqualTo(1);
            await Assert.That(vm.Chat.ComposerText).IsEqualTo("");

            h.Lane.PushStreamEvent(Envelope("s1", 0, CanonicalEventTypes.UserMessageReceived, """{"content":"do it"}"""));
            await h.UntilAsync(vm, () => vm.Chat.QueuedMessages.Count == 0, "the echo");
            await Assert.That(vm.Chat.Items.Single()).IsTypeOf<UserTurnItem>();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_hidden_transcript_reads_as_not_available_and_a_lost_lane_keeps_the_rows() {
        await RunOnUiAsync(async () => {
            using var h = new Harness { Detail = new(null, NotFound: true) };
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Missing, "missing");
            await Assert.That(vm.Chat.PhaseNote).IsEqualTo(RemoteSessionViewModel.MissingNote);
            // The stopped run answers every later poll with the same verdict.
            await h.TickAsync(vm);
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Missing);

            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Offline, what: "offline");
            // The banner has its own row above the panes: it reads over the retained rows rather
            // than replacing them, so a dropped lane does not blank the transcript.
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(vm.AccessNote).IsEqualTo("Not connected to the server");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_pty_harness_offers_a_terminal_tab_that_subscribes_when_first_shown_and_a_frame_harness_does_not() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var pty = h.Build(Harness.Row(vendor: "claude"));
            await Assert.That(pty.ShowsTerminalTab).IsTrue();
            await Assert.That(pty.IsChatActive).IsTrue();
            await WaitUntilAsync(() => pty.Access == RemoteSessionAccess.Ready, what: "ready");
            await Assert.That(h.Lane.TerminalSubscribes).IsEmpty();

            await pty.ShowTerminalCommand.Execute();
            await WaitUntilAsync(() => pty.Terminal!.Phase == RemoteTerminalPhase.Live, what: "the terminal live");
            await Assert.That(h.Lane.TerminalSubscribes).Contains("a1");
            await Assert.That(pty.IsTerminalActive).IsTrue();
            await Assert.That(pty.ShowsTerminalPane).IsTrue();
            await Assert.That(pty.ShowsChatPane).IsFalse();
            pty.Terminal!.SetViewport(100, 30);
            await pty.TeardownAsync();
            await Assert.That(h.Lane.ResizeReleases).Contains("a1");

            var frame = h.Build(Harness.Row(id: "a2", sessionId: "s2", vendor: "gemini"));
            await Assert.That(frame.ShowsTerminalTab).IsFalse();
            await Assert.That(frame.Terminal).IsNull();
            await frame.TeardownAsync();
        });
    }

    [Test]
    public async Task A_prompt_queued_by_another_client_shows_in_the_chats_queue() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            h.Lane.PendingInputSubject.OnNext(new("s1", [new QueuedInputItem { DispatchId = Guid.NewGuid(), SenderUserId = "u2", Text = "after this one" }]));
            await WaitUntilAsync(() => vm.Chat.QueuedMessages.Count == 1, what: "the foreign row");
            await Assert.That(vm.Chat.QueuedMessages[0].IsForeign).IsTrue();
            h.Lane.PendingInputSubject.OnNext(new("other-session", []));
            await Assert.That(vm.Chat.QueuedMessages.Count).IsEqualTo(1);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_lapsed_sign_in_says_so_in_place_of_the_transcript() {
        await RunOnUiAsync(async () => {
            using var h = new Harness { Detail = new(null, Unauthorized: true) };
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.Phase == ChatTabPhase.Failed, "the refusal");
            await Assert.That(vm.Chat.PhaseNote).IsEqualTo("The transcript could not be read: not signed in");
            await Assert.That(vm.ShowsChatPane).IsTrue();
            // The stopped run answers every later poll with nothing; the explanation stays.
            await h.TickAsync(vm);
            await h.TickAsync(vm);
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Failed);
            await vm.TeardownAsync();
        });
    }

    /// The detail route answered but the stream refused: the seeded rows stay, and the reason sits
    /// under them instead of leaving a transcript that silently stops growing.
    [Test]
    public async Task A_refused_stream_keeps_the_seeded_rows_and_says_why_beneath_them() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
                Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
            h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
            var vm = h.Build(Harness.Row());
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.ActivityNote.Length > 0, "the reason");
            await Assert.That(vm.Chat.Phase).IsEqualTo(ChatTabPhase.Reading);
            await Assert.That(vm.Chat.Items.Count).IsEqualTo(2);
            await Assert.That(vm.Chat.ActivityNote).IsEqualTo("The transcript could not be read: not authorized to read this session's stream");
            await vm.TeardownAsync();
        });
    }

    const string AgentCall = """{"tool_calls":[{"call_id":"toolu_A","tool_name":"Agent","arguments":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}""";
    const string AgentLaunch = """{"call_id":"toolu_A","result":"Async agent launched successfully.","extensions":{"claude_code":{"tool_use_result":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}}}""";
    const string AgentNotification = """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>"}""";
    const string MetaNotification = """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>","extensions":{"claude_code":{"is_meta":true}}}""";

    [Test]
    public async Task An_already_denied_background_session_releases_its_feed_before_access_returns() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            using var lease = h.Access.Acquire("s2");
            await WaitUntilAsync(async () => await lease.State.FirstAsync() == SessionAccessState.Denied, "denied session");
            var row = Harness.Row(sessionId: null);
            h.Directory.Rows.AddOrUpdate(row);
            var unexpectedRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            BackgroundCommandMonitor? monitor = null;
            var ended = false;
            monitor = new BackgroundCommandMonitor(row.Key, _ => { }, () => {
                ended = true;
                monitor!.Dispose();
            }, new FakeDaemonClientService(), h.Directory, h.Access, (_, _) => {
                unexpectedRead.TrySetResult();
                return Task.FromResult(h.Detail);
            }, h.Lane, h.Time);
            using var lifetime = monitor;

            h.Directory.Rows.AddOrUpdate(row with { SessionId = "s2" });
            await Assert.That(ended).IsTrue();
            h.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Ok);
            h.Lane.SessionAccessChangedSubject.OnNext("s2");
            await WaitUntilAsync(async () => await lease.State.FirstAsync() == SessionAccessState.Established, "restored access");
            var quiet = Task.Delay(TimeSpan.FromSeconds(1));
            await Assert.That(await Task.WhenAny(unexpectedRead.Task, quiet)).IsSameReferenceAs(quiet);
            await Assert.That(h.Lane.Tails).IsEmpty();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_remote_background_command_remains_working_after_navigation_until_it_or_the_session_ends(bool sessionEnds) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var call = AgentCall.Replace("toolu_A", "toolu_S").Replace("\"Agent\"", "\"Bash\"");
            const string launch = """{"call_id":"toolu_S","result":"Command running in background with ID: b1.","extensions":{"claude_code":{"tool_use_result":{"backgroundTaskId":"b1"}}}}""";
            const string finished = """{"content":"<task-notification>\n<task-id>b1</task-id>\n<tool-use-id>toolu_S</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>"}""";
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, call),
                Event(1, CanonicalEventTypes.ToolResultReceived, launch)));
            BackgroundCommandMonitor? monitor = null;
            var backgroundSeed = new TaskCompletionSource<SessionDetailFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var commands = new BackgroundCommandActivity((key, report, ended, _) =>
                monitor = new BackgroundCommandMonitor(key, report, ended, new FakeDaemonClientService(), h.Directory,
                    h.Access, (_, ct) => backgroundSeed.Task.WaitAsync(ct), h.Lane, h.Time));
            IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
            using var subscription = commands.Running.Subscribe(running => latest = running);
            var row = Harness.Row(vendor: "claude") with { AwaitingInput = true };
            var vm = h.Build(row, commands: commands);
            await h.UntilAsync(vm, () => latest.GetValueOrDefault("remote:a1") == 1, "running command");
            await vm.TeardownAsync();
            h.Time.Advance(ChatTabViewModel.PollInterval);
            await Task.Yield();
            await (monitor!.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(latest.GetValueOrDefault("remote:a1")).IsEqualTo(1);
            backgroundSeed.SetResult(h.Detail);
            await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "background monitor tail");
            h.Time.Advance(ChatTabViewModel.PollInterval);
            await Task.Yield();
            await (monitor!.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(latest.GetValueOrDefault("remote:a1")).IsEqualTo(1);

            if (sessionEnds) h.Directory.Rows.AddOrUpdate(row with { Status = "Completed" });
            else h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived, finished));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (latest.ContainsKey("remote:a1")) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Background command did not settle");
                h.Time.Advance(ChatTabViewModel.PollInterval);
                await Task.Delay(10);
            }
            await Assert.That(latest.ContainsKey("remote:a1")).IsFalse();
        });
    }

    /// A refused or missing seed stops the remote feed for good, so the count the workspace handed
    /// over must not outlive it.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_remote_background_monitor_whose_feed_stops_withdraws_the_running_command(bool notFound) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var call = AgentCall.Replace("toolu_A", "toolu_S").Replace("\"Agent\"", "\"Bash\"");
            const string launch = """{"call_id":"toolu_S","result":"Command running in background with ID: b1.","extensions":{"claude_code":{"tool_use_result":{"backgroundTaskId":"b1"}}}}""";
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, call),
                Event(1, CanonicalEventTypes.ToolResultReceived, launch)));
            var refusal = new SessionDetailFetch(null, NotFound: notFound, Unauthorized: !notFound);
            using var commands = new BackgroundCommandActivity((key, report, ended, _) =>
                new BackgroundCommandMonitor(key, report, ended, new FakeDaemonClientService(), h.Directory,
                    h.Access, (_, _) => Task.FromResult(refusal), h.Lane, h.Time));
            IReadOnlyDictionary<string, int> latest = new Dictionary<string, int>();
            using var subscription = commands.Running.Subscribe(running => latest = running);
            var vm = h.Build(Harness.Row(vendor: "claude") with { AwaitingInput = true }, commands: commands);
            await h.UntilAsync(vm, () => latest.GetValueOrDefault("remote:a1") == 1, "running command");
            await vm.TeardownAsync();

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (latest.ContainsKey("remote:a1")) {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Stopped feed kept its running command");
                h.Time.Advance(ChatTabViewModel.PollInterval);
                await Task.Delay(10);
            }
            await Assert.That(latest.ContainsKey("remote:a1")).IsFalse();
        });
    }

    /// Events shaped as the server writes them: tool_use_result on the result, no origin_kind on
    /// the notification.
    [Test]
    public async Task Server_shaped_events_drive_the_strip_through_a_background_launch_and_its_finish() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningRuns, "the strip");
            await Assert.That(vm.Chat.RunningRow!.StateText).StartsWith("running in background · ");
            await Assert.That(vm.Chat.Items.OfType<ToolGroupItem>().Single().Calls.Single().Outcome).IsEqualTo(ToolOutcome.Done);

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived, AgentNotification));
            await h.UntilAsync(vm, () => !vm.Chat.HasRunningRuns, "the finish");
            await Assert.That(vm.Chat.Items.OfType<SystemNoteItem>().Count()).IsEqualTo(1);
            await Assert.That(vm.Chat.Items.OfType<UserTurnItem>().Any()).IsFalse();
            await vm.TeardownAsync();
        });
    }

    /// The not-before-start guard in SessionRuns compares a completion's own time with the
    /// launch it would end; a batch stored late must not let a stale completion win that compare.
    [Test]
    public async Task A_completion_stored_late_but_dated_before_the_resumed_launch_leaves_it_running() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            const string firstCall = """{"tool_calls":[{"call_id":"toolu_A","tool_name":"Agent","arguments":{"description":"d","prompt":"p","subagent_type":"Explore"}}],"timestamp":"2026-09-17T10:00:00Z"}""";
            const string firstLaunch = """{"call_id":"toolu_A","result":"Async agent launched successfully.","timestamp":"2026-09-17T10:00:05Z","extensions":{"claude_code":{"tool_use_result":{"isAsync":true,"status":"async_launched","agentId":"a"}}}}""";
            const string firstFinish = """{"content":"<task-notification>\n<task-id>a</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>","timestamp":"2026-09-17T10:01:30Z"}""";
            const string secondCall = """{"tool_calls":[{"call_id":"toolu_B","tool_name":"Agent","arguments":{"description":"d","prompt":"p","subagent_type":"Explore"}}],"timestamp":"2026-09-17T10:02:00Z"}""";
            const string secondLaunch = """{"call_id":"toolu_B","result":"Async agent launched successfully.","timestamp":"2026-09-17T10:02:05Z","extensions":{"claude_code":{"tool_use_result":{"isAsync":true,"status":"async_launched","agentId":"a"}}}}""";
            const string taskStop = """{"call_id":"toolu_S","result":"ok","timestamp":"2026-09-17T10:01:00Z","extensions":{"claude_code":{"tool_use_result":{"task_id":"a","task_type":"local_agent","message":"Successfully stopped task: a"}}}}""";

            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, firstCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, firstLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningRuns, "the first launch");

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived, firstFinish));
            await h.UntilAsync(vm, () => !vm.Chat.HasRunningRuns, "the first finish");

            h.Lane.PushStreamEvent(Envelope("s1", 3, CanonicalEventTypes.AssistantToolCallsGenerated, secondCall));
            h.Lane.PushStreamEvent(Envelope("s1", 4, CanonicalEventTypes.ToolResultReceived, secondLaunch));
            await h.UntilAsync(vm, () => vm.Chat.HasRunningRuns, "the second launch");

            // A completion that lands after this synchronization marker was folded into the strip
            // before the marker's own row could appear, since the tail applies its stream in order.
            h.Lane.PushStreamEvent(Envelope("s1", 5, CanonicalEventTypes.ToolResultReceived, taskStop));
            h.Lane.PushStreamEvent(Envelope("s1", 6, CanonicalEventTypes.AssistantTextGenerated, """{"content":"__sync__"}"""));
            await h.UntilAsync(vm, () => vm.Chat.Items.OfType<AssistantTextItem>().Any(i => i.Text == "__sync__"), "the sync marker");

            await Assert.That(vm.Chat.HasRunningRuns).IsTrue();
            await Assert.That(vm.Chat.RunningRow!.StateText).StartsWith("running in background · ");
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_meta_notification_finishes_its_subagent_without_a_row() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningRuns, "the strip");
            var rows = vm.Chat.Items.Count;

            h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived, MetaNotification));
            await h.UntilAsync(vm, () => !vm.Chat.HasRunningRuns, "the finish");
            await Assert.That(vm.Chat.Items.Count).IsEqualTo(rows);
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_row_removal_stops_the_strip_and_its_reappearance_restores_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Detail = new(RemoteFixtures.Detail(
                Event(0, CanonicalEventTypes.AssistantToolCallsGenerated, AgentCall),
                Event(1, CanonicalEventTypes.ToolResultReceived, AgentLaunch)));
            var vm = h.Build(Harness.Row(vendor: "claude"));
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await h.UntilAsync(vm, () => vm.Chat.HasRunningRuns, "the strip");

            h.Directory.Rows.Remove("remote:a1");
            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.Chat.HasRunningRuns).IsFalse();

            h.Directory.Rows.AddOrUpdate(Harness.Row(vendor: "claude"));
            await Assert.That(vm.SessionEnded).IsFalse();
            await Assert.That(vm.Chat.HasRunningRuns).IsTrue();
            await Assert.That(vm.Chat.RunningRow!.StateText).StartsWith("running in background · ");
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_session_with_a_document_shows_the_artefacts_tab_without_a_pane() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var source = new FakePlanArtifactSource();
            source.Default = new PlanArtifactsRead(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto {
                Artifacts = [new PlanArtifactDto {
                    ArtifactId = "a", Kind = "plan", Title = "p", Source = "declared", SessionId = "0123456789abcdef0123456789abcdef", Path = "docs/p.md",
                    Content = "# p", ContentState = "ok", IsComplete = true, IsConfirmed = true, ContentHash = "h", Version = 1,
                    DiscoveredAt = DateTimeOffset.UnixEpoch, Confidence = "high", Reason = "declared", IsPrimary = true,
                }],
            });
            var vm = h.Build(Harness.Row(sessionId: "0123456789abcdef0123456789abcdef"), source);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);

            await Assert.That(vm.ShowsArtefactsTab).IsTrue();
            await vm.ShowArtefactsCommand.Execute();
            await Assert.That(vm.IsArtefactsActive).IsTrue();
            await Assert.That(vm.ShowsChatPane).IsFalse();
            await Assert.That(vm.ShowsArtefactsPane).IsTrue();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_artefacts_tab_re_reads_when_access_becomes_ready() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var accessReady = new TaskCompletionSource<HubCallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Lane.AccessWatchHandler = _ => accessReady.Task;
            var source = new FakePlanArtifactSource();
            var vm = h.Build(Harness.Row(sessionId: "0123456789abcdef0123456789abcdef"), source);
            // Settle the initial read before granting access, so the ready edge must start a new read.
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(source.Requested.Count).IsEqualTo(1);

            accessReady.SetResult(HubCallOutcome.Ok);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            await WaitUntilAsync(() => source.Requested.Count == 2, what: "re-read on ready");
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await vm.TeardownAsync();
        });
    }

    /// With no pane to poll it, the remote lane retries an unreachable read on the pane's cadence
    /// and stops once a read is answered.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_artefacts_read_is_retried_on_the_poll_cadence_until_answered() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var source = new FakePlanArtifactSource { Default = new PlanArtifactsRead(SessionPlansReadKind.Unreachable, null) };
            var vm = h.Build(Harness.Row(sessionId: "0123456789abcdef0123456789abcdef"), source);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");
            // The session switch and access-ready refresh can coalesce. Await the read
            // that actually ran rather than requiring a scheduler-dependent startup count.
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await WaitUntilAsync(() => vm.Artefacts.LastReadFailed, what: "unreachable recorded");
            var before = source.Requested.Count;

            h.Time.Advance(WorkContextViewModel.PollInterval);
            await WaitUntilAsync(() => source.Requested.Count > before, what: "retry after the poll interval");
            await (vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            await Assert.That(vm.Artefacts.LastReadFailed).IsTrue();

            source.Default = new PlanArtifactsRead(SessionPlansReadKind.Ready, new PlanArtifactsResponseDto());
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await WaitUntilAsync(() => !vm.Artefacts.LastReadFailed, what: "an answered read clears the failure");
            var answered = source.Requested.Count;

            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(source.Requested.Count).IsEqualTo(answered);
            await vm.TeardownAsync();
        });
    }

    // ── session-only rows ──────────────────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_only_ended_row_acquires_its_lease_and_shows_the_chat() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow());
            var ended = false;
            using var _ = vm.Ended.Subscribe(_ => ended = true);

            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "lease established for an ended session");

            await Assert.That(vm.IsSessionOnly).IsTrue();
            await Assert.That(vm.ShowsStop).IsFalse();
            await Assert.That(vm.Terminal).IsNull();
            await Assert.That(vm.ShowsTerminalTab).IsFalse();
            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.ShowsChatPane).IsTrue();
            await Assert.That(vm.Chat.ShowsComposer).IsFalse();
            await Assert.That(vm.Chat.ReadOnlyNotice).IsEqualTo(RemoteSessionViewModel.EndedNote);
            await Assert.That(vm.RepoLabelText).IsEqualTo("kurrent-io/kcap-cli · Alexey");
            await Assert.That(h.Lane.ChatSubscribes).Contains("s1");
            await Assert.That(ended).IsFalse();
            await vm.TeardownAsync();
        });
    }

    /// The successor's server row, once it ended, still shows what the hand-off carried: the daemon
    /// keeps the outcome by session id, not on the agent that is gone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_outcome_shows_after_the_successor_ended_and_became_a_server_row() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var daemon = new FakeDaemonClientService();
            daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, [Cli.Core.LocalIpc.HandoffWire.Capability]));
            var asked = new List<string>();
            var ops = new ScriptedLocalControlOps {
                ReadHandoffOutcome = id => {
                    asked.Add(id);
                    return Task.FromResult<Cli.Core.Handoff.HandoffOutcomeDto?>(
                        new("s0", "Desktop app RAM growth", "done", null, [new("plan", "Plan A", "carried", null)]));
                },
            };
            var vm = h.Build(Harness.ServerRow(sessionId: "s9"), handoffOutcomes: new HandoffOutcomeReader(daemon, ops, h.Time));

            await WaitUntilAsync(() => vm.Chat.HandoffOutcome is not null, what: "the outcome");

            await Assert.That(asked).Contains("s9");
            await Assert.That(vm.Chat.HandoffOutcome!.Header).IsEqualTo("Continued from Desktop app RAM growth");
            await vm.TeardownAsync();
        });
    }

    /// The composer takes the read-only notice's place for as long as the row stays ended, and goes
    /// with the row's end: a session the server lists live again keeps no composer behind it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ended_server_session_offers_handoff_not_a_read_only_notice() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            using var handoff = new HandoffComposerFixture();
            var vm = h.Build(Harness.ServerRow(), handoff: handoff.Build);

            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            var composer = vm.Chat.Handoff;
            await Assert.That(composer).IsNotNull();
            await Assert.That(handoff.Built).IsEquivalentTo([composer!]);
            await Assert.That(vm.Chat.ReadOnlyNotice).IsEqualTo("");
            await Assert.That(vm.Chat.ShowsReadOnlyBanner).IsFalse();
            await Assert.That(vm.Chat.ShowsComposer).IsFalse();
            await WaitUntilAsync(() => composer!.ProbedStart is not null, what: "the probed start");
            await Assert.That(composer!.StripLabel).IsEqualTo("New session in a fresh worktree on feature/x");

            h.Directory.Rows.AddOrUpdate(Harness.ServerRow(status: "active"));

            await Assert.That(vm.Chat.Handoff).IsNull();
            await Assert.That(composer.IsDisposedForTesting).IsTrue();
            await Assert.That(vm.Chat.ReadOnlyNotice).IsEqualTo(RemoteSessionViewModel.TerminalNote);

            h.Directory.Rows.AddOrUpdate(Harness.ServerRow());
            var again = vm.Chat.Handoff;
            await Assert.That(again).IsNotNull();
            await Assert.That(again).IsNotSameReferenceAs(composer);

            await vm.TeardownAsync();
            await Assert.That(again!.IsDisposedForTesting).IsTrue();
        });
    }

    /// A pinned session enters with an unknown access level, which the composer reads as not full;
    /// the listing's level reaches the same composer, so what the user typed survives the re-check.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ended_row_revision_refreshes_the_composer_without_rebuilding_it() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            using var handoff = new HandoffComposerFixture();
            var vm = h.Build(Harness.ServerRow() with { AccessLevel = "" }, handoff: handoff.Build);
            var composer = vm.Chat.Handoff!;
            await WaitUntilAsync(() => composer.DisabledReason is not null, what: "the first check");
            await Assert.That(composer.DisabledReason).IsEqualTo(HandoffComposerViewModel.NeedsFullAccess);
            await Assert.That(handoff.Ops.Probes).IsEmpty();
            composer.Text = "keep going";

            h.Directory.Rows.AddOrUpdate(Harness.ServerRow());

            await WaitUntilAsync(() => composer.ProbedStart is not null, what: "the probe after the listing's level");
            await Assert.That(composer.DisabledReason).IsNull();
            await Assert.That(handoff.Ops.Probes).Count().IsEqualTo(1);
            await Assert.That(composer.Text).IsEqualTo("keep going");
            await Assert.That(vm.Chat.Handoff).IsSameReferenceAs(composer);

            h.Directory.Rows.AddOrUpdate(Harness.ServerRow() with { LastActivityAt = DateTime.UnixEpoch.AddHours(2) });
            await Task.Delay(50);
            await Assert.That(handoff.Ops.Probes).Count().IsEqualTo(1);
            await Assert.That(handoff.Built).Count().IsEqualTo(1);
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_active_server_session_keeps_the_read_only_path() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            using var handoff = new HandoffComposerFixture();
            var vm = h.Build(Harness.ServerRow(status: "active"), handoff: handoff.Build);

            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            await Assert.That(vm.Chat.Handoff).IsNull();
            await Assert.That(handoff.Built).IsEmpty();
            await Assert.That(vm.Chat.ReadOnlyNotice).IsEqualTo(RemoteSessionViewModel.TerminalNote);
            await Assert.That(vm.Chat.ShowsReadOnlyBanner).IsTrue();
            await Assert.That(vm.Chat.ShowsComposer).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_terminal_row_shows_the_terminal_note_and_no_composer() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow(status: "active"));

            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            await Assert.That(vm.SessionEnded).IsFalse();
            await Assert.That(vm.Chat.ReadOnlyNotice).IsEqualTo(RemoteSessionViewModel.TerminalNote);
            await Assert.That(vm.Chat.ShowsComposer).IsFalse();
            await Assert.That(vm.ShowsStop).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_only_row_ending_keeps_the_lease_and_the_workspace() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.ServerRow(status: "active"));
            var ended = false;
            using var _ = vm.Ended.Subscribe(_ => ended = true);
            await WaitUntilAsync(() => vm.Access == RemoteSessionAccess.Ready, what: "ready");

            h.Directory.Rows.AddOrUpdate(Harness.ServerRow(status: "ended"));

            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.Ready);
            await Assert.That(h.Lane.ChatUnsubscribes).IsEmpty();
            await Assert.That(vm.Chat.ReadOnlyNotice).IsEqualTo(RemoteSessionViewModel.EndedNote);
            await Assert.That(ended).IsFalse();

            // The row leaving the loaded set is not the session ending: the workspace stays bound.
            h.Directory.Rows.RemoveKey("server:s1");
            await Assert.That(vm.Access).IsEqualTo(RemoteSessionAccess.Ready);
            await Assert.That(ended).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_hosted_remote_row_keeps_todays_release_on_terminal_status() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(status: "Completed"));

            await Assert.That(vm.IsSessionOnly).IsFalse();
            await Assert.That(vm.ShowsStop).IsTrue();
            await Assert.That(vm.SessionEnded).IsTrue();
            await Assert.That(h.Lane.AccessWatches).IsEmpty();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task An_owned_row_shows_the_pencil_and_renames_through_the_server() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var calls = new List<(string, string, string)>();
            h.Remote.Rename = (agent, session, title) => {
                calls.Add((agent, session, title));
                return Task.FromResult(new SessionRenameResult(true, "New name", null));
            };
            var vm = h.Build(Harness.Row());

            await Assert.That(vm.TitleEditor.ShowsPencil).IsTrue();
            await vm.TitleEditor.BeginEditCommand.Execute();
            vm.TitleEditor.Draft = "New name";
            await vm.TitleEditor.SaveCommand.Execute();

            await Assert.That(calls).IsEquivalentTo(new[] { ("a1", "s1", "New name") });
            await Assert.That(vm.TitleEditor.Title).IsEqualTo("New name");
            await Assert.That(vm.TitleEditor.IsEditing).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Another_owners_row_shows_no_pencil() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = Harness.Row() with { OwnerUserId = "someone-else" };
            var vm = h.Build(row);

            await Assert.That(vm.TitleEditor.ShowsPencil).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task A_row_without_a_session_shows_no_pencil() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row(sessionId: null));

            await Assert.That(vm.TitleEditor.ShowsPencil).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task An_ended_session_shows_no_pencil() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await Assert.That(vm.TitleEditor.ShowsPencil).IsTrue();

            h.Directory.Rows.AddOrUpdate(Harness.Row(status: "Completed"));
            await WaitUntilAsync(() => vm.SessionEnded, what: "ended");
            await Assert.That(vm.TitleEditor.ShowsPencil).IsFalse();
            await vm.TeardownAsync();
        });
    }

    [Test]
    public async Task Signing_in_as_another_user_hides_the_pencil() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var vm = h.Build(Harness.Row());
            await Assert.That(vm.TitleEditor.ShowsPencil).IsTrue();

            h.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u2", Epoch: 2));
            await WaitUntilAsync(() => !vm.TitleEditor.ShowsPencil, what: "pencil hidden");
            await vm.TeardownAsync();
        });
    }

    /// The listing's own record of the session: renamable by its owner whether or not it ended, and
    /// the applied title reaches the listing at once.
    [Test]
    [Arguments("ended")]
    [Arguments("active")]
    public async Task An_owned_server_row_shows_the_pencil_and_renames_through_the_server(string status) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var calls = new List<(string, string, string)>();
            h.Remote.Rename = (agent, session, title) => {
                calls.Add((agent, session, title));
                return Task.FromResult(new SessionRenameResult(true, "New name", null));
            };
            var vm = h.Build(Harness.ServerRow("s1", status));

            await Assert.That(vm.TitleEditor.ShowsPencil).IsTrue();
            await vm.TitleEditor.BeginEditCommand.Execute();
            vm.TitleEditor.Draft = "New name";
            await vm.TitleEditor.SaveCommand.Execute();

            await Assert.That(calls).IsEquivalentTo(new[] { ("s1", "s1", "New name") });
            await Assert.That(h.Sessions.Renames).IsEquivalentTo(new[] { ("s1", "New name") });
            await Assert.That(vm.TitleEditor.Title).IsEqualTo("New name");
            await vm.TeardownAsync();
        });
    }

    [Test]
    [Arguments("another owner", false)]
    [Arguments("viewer alias", true)]
    public async Task A_server_rows_pencil_follows_the_listings_owner(string owner, bool shows) {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            var row = owner == "viewer alias"
                ? Harness.ServerRow("s1") with { OwnerUserId = "legacy-id", OwnerIsViewer = true }
                : Harness.ServerRow("s1") with { OwnerUserId = "someone-else" };
            var vm = h.Build(row);

            await Assert.That(vm.TitleEditor.ShowsPencil).IsEqualTo(shows);
            await vm.TeardownAsync();
        });
    }
}
