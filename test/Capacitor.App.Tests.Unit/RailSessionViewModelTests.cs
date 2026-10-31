using System.Collections.Frozen;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// RailSessionViewModel's OpenCommand and IsSelected OAPH both run through
/// RxSchedulers.MainThreadScheduler, which is not immediate in a bare test process — see
/// MainWindowViewModelTests' header comment. Every test here runs inside
/// AvaloniaSession.WithImmediateRxScheduler and carries [NotInParallel("AvaloniaSession")].
public class RailSessionViewModelTests {
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Multiple_prs_open_only_the_selected_current_choice(bool remote) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var items = new[] { new RailPullRequestMenuItem(PullRequestWire.Subject(FakePullRequestSource.Link(1)), "First", "Open"),
                new RailPullRequestMenuItem(PullRequestWire.Subject(FakePullRequestSource.Link(2)), "Second", "Draft") };
            using var summaries = new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                new Dictionary<string, RailPullRequestSummary> { ["s1"] = new(items) });
            var opened = new List<(string, AgentOrigin, PullRequestSubjectDto?)>();
            using var row = new RailSessionViewModel((remote ? RemoteRow("a1") : Row()) with { SessionId = "s1" },
                new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System,
                pullRequests: summaries, openPullRequest: (id, origin, subject) => opened.Add((id, origin, subject)));
            row.OpenPullRequestCommand.Execute().Subscribe();
            await Assert.That(opened).IsEmpty();
            row.OpenPullRequestChoiceCommand.Execute(items[0]).Subscribe();
            await Assert.That(opened).IsEquivalentTo(new[] { ("a1", remote ? AgentOrigin.Remote : AgentOrigin.Local, (PullRequestSubjectDto?)items[0].Subject) });
            summaries.OnNext(new Dictionary<string, RailPullRequestSummary>());
            row.OpenPullRequestChoiceCommand.Execute(items[1]).Subscribe();
            await Assert.That(opened).Count().IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pr_metadata_updates_and_its_action_is_separate_from_session_opening() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var summaries = new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                new Dictionary<string, RailPullRequestSummary>());
            using var localReadable = new BehaviorSubject<bool>(true);
            var openedSessions = new List<string>();
            var openedPrs = new List<(string, AgentOrigin, PullRequestSubjectDto?)>();
            using var row = new RailSessionViewModel(Row() with { SessionId = "s1" },
                new BehaviorSubject<string?>(null), NoPending, NotStale, openedSessions.Add, _ => { }, TimeProvider.System,
                pullRequests: summaries, localOnAppServer: localReadable,
                openPullRequest: (id, origin, subject) => openedPrs.Add((id, origin, subject)));
            var changes = new List<string?>();
            row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            await Assert.That(row.HasPullRequest).IsFalse();
            summaries.OnNext(new Dictionary<string, RailPullRequestSummary> {
                ["s1"] = new([new(PullRequestWire.Subject(FakePullRequestSource.Link(142)), null, "Draft")]),
            });
            await Assert.That(row.PullRequestLabel).IsEqualTo("PR #142");
            await Assert.That(row.PullRequestStatus!.Text).IsEqualTo("Draft");
            await Assert.That(row.PullRequestTooltip).Contains("Linked to this session: Fix the flaky test");
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            await Assert.That(changes).Contains(nameof(RailSessionViewModel.PullRequestLabel));
            row.OpenPullRequestCommand.Execute().Subscribe();
            await Assert.That(openedPrs).IsEquivalentTo(new[] { ("a1", AgentOrigin.Local, row.PullRequest!.Subject) });
            await Assert.That(openedSessions).IsEmpty();
            summaries.OnNext(new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([
                    new(PullRequestWire.Subject(FakePullRequestSource.Link(1)), null, "Open"),
                    new(PullRequestWire.Subject(FakePullRequestSource.Link(2)), null, "Open"),
                    new(PullRequestWire.Subject(FakePullRequestSource.Link(3)), null, "Open"),
                ]) });
            await Assert.That(row.PullRequestLabel).IsEqualTo("3 PRs");
            await Assert.That(row.HasPullRequestLifecycle).IsFalse();
            localReadable.OnNext(false);
            await Assert.That(row.HasPullRequest).IsFalse();
            row.OpenPullRequestCommand.Execute().Subscribe();
            await Assert.That(openedPrs).Count().IsEqualTo(1);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_pr_opens_on_the_remote_origin_even_when_local_reads_are_disabled() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            AgentOrigin? opened = null;
            PullRequestSubjectDto? openedSubject = null;
            using var row = new RailSessionViewModel(RemoteRow("a1") with { SessionId = "s1" },
                new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System,
                pullRequests: new BehaviorSubject<IReadOnlyDictionary<string, RailPullRequestSummary>>(
                    new Dictionary<string, RailPullRequestSummary> { ["s1"] = new([new(PullRequestWire.Subject(FakePullRequestSource.Link(142)), null, "Unknown")]) }),
                localOnAppServer: new BehaviorSubject<bool>(false), openPullRequest: (_, origin, subject) => { opened = origin; openedSubject = subject; });
            row.OpenPullRequestCommand.Execute().Subscribe();
            await Assert.That(opened).IsEqualTo(AgentOrigin.Remote);
            await Assert.That(openedSubject).IsEqualTo(row.PullRequest!.Subject);
        });
    }

    static readonly IObservable<IReadOnlySet<string>> NoPending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
    static readonly IObservable<bool> NotStale = new BehaviorSubject<bool>(false);
    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    static AgentRow Row(
            string id = "a1", string kind = "agent", string vendor = "claude", string status = "Running",
            string? model = "Opus 5", string? title = "Fix the flaky test", bool? awaitingInput = null, int? liveSubagents = null) =>
        AgentRow.FromLocal(
            new(id, kind, vendor, "/repo", status, null, null, null, DateTime.UtcNow, model, null,
                Title: title, AwaitingInput: awaitingInput, LiveSubagents: liveSubagents),
            Repo);

    static AgentRow LocalRow(string id) => Row(id: id);

    static AgentRow RemoteRow(string id) => AgentRow.FromRemote(new AgentInstanceDto {
        AgentId = id, Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
        Vendor = "claude", RepoOwner = "o", RepoName = "r",
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_row_reads_its_launch_stage_and_opens_locally() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = AgentRow.FromPending(
                new PendingLaunchDto("p1", "claude", "/repo", "Fix the flaky test", DateTime.UtcNow, "session_created"), Repo);
            var opened = new List<string>();
            using var row = new RailSessionViewModel(pending, new BehaviorSubject<string?>(null), NoPending, NotStale, opened.Add, _ => throw new InvalidOperationException("remote"), TimeProvider.System);

            await Assert.That(row.IsStarting).IsTrue();
            await Assert.That(row.Meta).IsEqualTo("Session created");
            await Assert.That(row.Tooltip).Contains("Starting");
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEquivalentTo(new[] { "p1" });
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_published_row_is_not_starting() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(status: "Starting"), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.IsStarting).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Title_is_primary_and_the_tip_names_the_harness_and_model() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Primary).IsEqualTo("Fix the flaky test");
            await Assert.That(row.HasTitle).IsTrue();
            await Assert.That(row.Vendor).IsEqualTo("claude");
            await Assert.That(row.HasVendor).IsTrue();
            await Assert.That(row.Model).IsEqualTo("Opus 5");
            await Assert.That(row.Tooltip).Contains("Claude Code\nHarness");
            await Assert.That(row.Tooltip).Contains("Opus 5\nModel");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_title_leaves_the_chips_line_to_carry_the_row() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(title: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.HasTitle).IsFalse();
            await Assert.That(row.Vendor).IsEqualTo("claude");
            await Assert.That(row.Model).IsEqualTo("Opus 5");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Non_agent_kind_goes_to_the_meta_line_not_the_vendor_chip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(kind: "review", title: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Vendor).IsEqualTo("claude");
            await Assert.That(row.Meta).StartsWith("review · ");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Borrowed_work_location_marks_the_meta_line_and_the_tooltip_names_the_checkout() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = new AgentStatusDto("a1", "review-flow", "codex", "/repo", "Running", null, null, null, DateTime.UtcNow, "Opus 5", null, Title: "Fix the flaky test") with {
                WorktreePath = "/repo/.capacitor/worktrees/agent-1", WorkLocation = "borrowed",
                BorrowedFrom = "/repo/.capacitor/worktrees/agent-1" };
            using var row = new RailSessionViewModel(AgentRow.FromLocal(dto, Repo), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Vendor).IsEqualTo("codex");
            await Assert.That(row.Model).IsEqualTo("Opus 5");
            await Assert.That(row.Meta).StartsWith("review-flow · borrowed · ");
            await Assert.That(row.Tooltip).Contains("/repo/.capacitor/worktrees/agent-1");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_model_leaves_the_model_off_the_tip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(model: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Model).IsNull();
            await Assert.That(row.Tooltip).Contains("Claude Code\nHarness");
            await Assert.That(row.Tooltip).DoesNotContain("\nModel");
        });
    }

    /// A finished turn is Idle; a live turn is Working; an unknown turn keeps the daemon's word.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Awaiting_input_is_idle_and_a_live_turn_is_working() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var waiting = new RailSessionViewModel(Row(awaitingInput: true), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var working = new RailSessionViewModel(Row(awaitingInput: false), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var older   = new RailSessionViewModel(Row(awaitingInput: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(waiting.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
            await Assert.That(waiting.Status.Label).IsEqualTo("Idle");
            await Assert.That(waiting.Status.AccessibleName).IsEqualTo("Idle");
            await Assert.That(waiting.Tooltip).DoesNotContain("Waiting for input");
            await Assert.That(working.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(working.Status.Label).IsEqualTo("Working");
            await Assert.That(working.Tooltip).DoesNotContain("Waiting for input");
            await Assert.That(older.Status.Kind).IsEqualTo(AgentStatusKind.Other);
            await Assert.That(older.Status.Label).IsEqualTo("Running");
            await Assert.That(older.Status.ShowsRing).IsTrue();
            await Assert.That(older.Status.ShowsDash).IsFalse();
            await Assert.That(older.Status.Pulses).IsFalse();
        });
    }

    /// A flow participant between rounds waits on the flow, not on the user, who cannot message
    /// it anyway.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Awaiting_input_on_a_flow_participant_does_not_set_the_pip() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(Row(kind: "review-flow", awaitingInput: true), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Status.Kind).IsNotEqualTo(AgentStatusKind.Idle);
            await Assert.That(row.Tooltip).DoesNotContain("Waiting for input");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task IsSelected_tracks_the_selection_observable() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var selected = new BehaviorSubject<string?>(null);
            using var row = new RailSessionViewModel(Row(id: "a1"), selected, NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.IsSelected).IsFalse();
            selected.OnNext("local:a1");
            await Assert.That(row.IsSelected).IsTrue();
            selected.OnNext("remote:a1");
            await Assert.That(row.IsSelected).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenCommand_invokes_the_local_callback_with_the_id() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            string? opened = null;
            using var row = new RailSessionViewModel(Row(id: "a9"), new BehaviorSubject<string?>(null), NoPending, NotStale, id => opened = id, _ => { }, TimeProvider.System);
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(opened).IsEqualTo("a9");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task OpenCommand_invokes_the_remote_callback_and_the_row_carries_its_machine_badge() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var dto = new AgentInstanceDto {
                AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
                Vendor = "claude", RepoOwner = "o", RepoName = "r",
            };
            string? openedRemote = null;
            string? openedLocal = null;
            using var row = new RailSessionViewModel(
                AgentRow.FromRemote(dto), new BehaviorSubject<string?>(null), NoPending, NotStale, id => openedLocal = id, id => openedRemote = id, TimeProvider.System);
            await Assert.That(row.IsRemote).IsTrue();
            await Assert.That(row.MachineBadge).IsEqualTo("work-mac");
            row.OpenCommand.Execute().Subscribe();
            await Assert.That(openedRemote).IsEqualTo("b1");
            await Assert.That(openedLocal).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Needs_you_follows_the_pending_set_and_the_status() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var row = new RailSessionViewModel(Row(status: "Running"), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Other);
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            await Assert.That(row.Tooltip).Contains("Pending response");
            pending.OnNext(new HashSet<string>());
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Other);

            using var failed = new RailSessionViewModel(Row(status: "Failed"), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(failed.Status.Kind).IsEqualTo(AgentStatusKind.Failed);
            await Assert.That(failed.Status.Label).IsEqualTo("Failed");

            using var idle = new RailSessionViewModel(Row(awaitingInput: true), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            pending.OnNext(new HashSet<string>());
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.Idle);

            var asked = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string> { "a1" });
            using var question = new RailSessionViewModel(
                Row(awaitingInput: true), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { },
                TimeProvider.System, asked);
            await Assert.That(question.Status.Kind).IsEqualTo(AgentStatusKind.Answer);
            await Assert.That(question.Status.Label).IsEqualTo("Answer");
            await Assert.That(question.Status.AccessibleName).IsEqualTo("An answer is expected");
            await Assert.That(question.Tooltip).DoesNotContain("Pending response");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_greys_out_while_the_lane_is_stale_and_a_local_row_never_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var stale = new BehaviorSubject<bool>(true);
            using var remote = new RailSessionViewModel(RemoteRow("r1"), new BehaviorSubject<string?>(null), NoPending, stale, _ => { }, _ => { }, TimeProvider.System);
            using var local = new RailSessionViewModel(LocalRow("a1"), new BehaviorSubject<string?>(null), NoPending, stale, _ => { }, _ => { }, TimeProvider.System);
            await Assert.That(remote.IsStale).IsTrue();
            await Assert.That(local.IsStale).IsFalse();

            stale.OnNext(false);
            await Assert.That(remote.IsStale).IsFalse();
        });
    }

    /// A background command keeps a finished turn Working, as a live subagent does, and the row
    /// reads Idle again once the command ends. The count is keyed by lane: an unproven twin on the
    /// other lane is a different session under the same id.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_running_background_command_keeps_a_waiting_row_working() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var commands = new BehaviorSubject<IReadOnlyDictionary<string, int>>(FrozenDictionary<string, int>.Empty);
            using var row = new RailSessionViewModel(Row(awaitingInput: true), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, TimeProvider.System, agentsRunningCommands: commands);
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Idle);

            commands.OnNext(new Dictionary<string, int> { ["remote:a1"] = 2 });
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Idle);

            commands.OnNext(new Dictionary<string, int> { ["local:a1"] = 2 });
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(row.Status.Pulses).IsTrue();
            await Assert.That(row.Tooltip).Contains("2 commands running");

            commands.OnNext(FrozenDictionary<string, int>.Empty);
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
        });
    }

    /// The daemon's count keeps the row visibly busy while only subagents run.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_subagents_pulse_the_dot_and_name_the_count() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var two   = new RailSessionViewModel(Row(liveSubagents: 2), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var one   = new RailSessionViewModel(Row(liveSubagents: 1), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var none  = new RailSessionViewModel(Row(liveSubagents: 0), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var older = new RailSessionViewModel(Row(liveSubagents: null), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);

            await Assert.That(two.Status.Pulses).IsTrue();
            await Assert.That(two.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(two.Meta).EndsWith(" · 2 subagents");
            await Assert.That(two.Tooltip).Contains("2 subagents running");
            await Assert.That(one.Status.Pulses).IsTrue();
            await Assert.That(one.Meta).EndsWith(" · 1 subagent");
            await Assert.That(one.Tooltip).Contains("1 subagent running");
            await Assert.That(none.Status.Pulses).IsFalse();
            await Assert.That(none.Meta).DoesNotContain("subagent");
            await Assert.That(none.Tooltip).DoesNotContain("subagent");
            await Assert.That(older.Status.Pulses).IsFalse();
            await Assert.That(older.Meta).DoesNotContain("subagent");
            await Assert.That(older.Tooltip).DoesNotContain("subagent");
        });
    }

    /// One verdict: live subagents are Working, and the tip still says the turn is waiting.
    /// A pending response outranks that.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_subagents_are_working_until_a_pending_response_outranks_them() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var waiting = new RailSessionViewModel(Row(id: "a1", awaitingInput: true, liveSubagents: 2), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var busy    = new RailSessionViewModel(Row(id: "a2", awaitingInput: false, liveSubagents: 2), new BehaviorSubject<string?>(null), pending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            using var idle    = new RailSessionViewModel(Row(id: "a3", awaitingInput: true, liveSubagents: 0), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);

            await Assert.That(waiting.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(waiting.Status.Pulses).IsTrue();
            await Assert.That(waiting.Tooltip).Contains("Waiting for input");
            await Assert.That(waiting.Tooltip).Contains("2 subagents running");
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(waiting.Status.Kind).IsEqualTo(AgentStatusKind.NeedsYou);
            await Assert.That(waiting.Status.Pulses).IsFalse();

            await Assert.That(busy.Status.Kind).IsEqualTo(AgentStatusKind.Working);
            await Assert.That(busy.Status.Pulses).IsTrue();
            await Assert.That(idle.Status.Kind).IsEqualTo(AgentStatusKind.Idle);
            await Assert.That(idle.Status.Pulses).IsFalse();
        });
    }

    /// A remote row carries no count, and a pending row pulses for its start.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remote_row_is_unchanged_and_a_pending_row_still_pulses() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var remote = new RailSessionViewModel(RemoteRow("r1"), new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);
            var pendingRow = AgentRow.FromPending(new PendingLaunchDto("p1", "claude", "/repo", "t", DateTime.UtcNow, "spawned"), Repo);
            using var pending = new RailSessionViewModel(pendingRow, new BehaviorSubject<string?>(null), NoPending, NotStale, _ => { }, _ => { }, TimeProvider.System);

            await Assert.That(remote.Status.Pulses).IsFalse();
            await Assert.That(remote.Status.Label).IsEqualTo("Running");
            await Assert.That(remote.Meta).DoesNotContain("subagent");
            await Assert.That(remote.Tooltip).DoesNotContain("subagent");
            await Assert.That(pending.Status.Kind).IsEqualTo(AgentStatusKind.Starting);
            await Assert.That(pending.Status.Pulses).IsTrue();
            await Assert.That(pending.Tooltip).Contains("Process started");
        });
    }

    // ── server rows ────────────────────────────────────────────────────────────────────────────

    static readonly DateTime Started = new(2026, 10, 22, 13, 4, 24, DateTimeKind.Utc);
    static readonly DateTime Ended = new(2026, 10, 22, 16, 56, 41, DateTimeKind.Utc);

    static AgentRow ServerRow(string status = "ended", string ownerName = "Tony Young", bool stale = false, string? repoName = "kcap-cli", bool hosted = false) =>
        AgentRow.FromServer(new RepoSessionDto {
            SessionId = "s1", Status = status, AccessLevel = "full", Vendor = "claude", Title = "Desktop app RAM growth",
            Owner = new() { UserId = "u2", DisplayName = ownerName, AvatarUrl = "https://avatars.example/u2" },
            StartedAt = new DateTimeOffset(Started), LastActivityAt = new DateTimeOffset(status == "ended" ? Ended : Started),
            EndedAt = status == "ended" ? new DateTimeOffset(Ended) : null, Stale = stale,
            Repo = repoName is null ? null : new() { Hash = "h", Owner = "kurrent-io", Name = repoName },
        }, hostedPresentation: hosted);

    static Microsoft.Extensions.Time.Testing.FakeTimeProvider At(DateTime utc) => new(new DateTimeOffset(utc));

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ended_server_row_shows_ended_meta_and_an_ended_status() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(ServerRow(), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended.AddHours(2)));

            await Assert.That(row.IsServer).IsTrue();
            await Assert.That(row.IsEnded).IsTrue();
            await Assert.That(row.IsTerminalSession).IsFalse();
            await Assert.That(row.Meta).IsEqualTo("2h ago · 3h 52m");
            await Assert.That(row.Status.Ended).IsTrue();
            await Assert.That(row.Status.Kind).IsEqualTo(AgentStatusKind.Done);
        });
    }

    /// A stamp that arrives as local time is the same instant as its UTC form, so the age does not
    /// move by the machine's UTC offset.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_time_stamp_ages_from_the_same_instant_as_its_utc_form() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var local = ServerRow() with { CreatedAt = Started.ToLocalTime(), EndedAt = Ended.ToLocalTime() };
            using var row = new RailSessionViewModel(local, new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended.AddDays(3).AddMinutes(30)));

            await Assert.That(row.Meta).IsEqualTo("3d ago · 3h 52m");
        });
    }

    /// Ended on the 22nd's afternoon and read on the 25th's morning: only 2d 16h, but three dates
    /// back, where the day groups file it, so the row reads "3d ago" rather than "2d ago".
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ended_row_ages_in_calendar_days_like_the_day_groups() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var row = new RailSessionViewModel(ServerRow(), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(new DateTime(2026, 10, 25, 9, 0, 0, DateTimeKind.Utc)));

            await Assert.That(row.Meta).IsEqualTo("3d ago · 3h 52m");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_live_terminal_row_carries_the_chip_and_ages() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var time = At(Started.AddMinutes(25));
            using var row = new RailSessionViewModel(ServerRow(status: "active"), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, time);

            await Assert.That(row.IsTerminalSession).IsTrue();
            await Assert.That(row.IsEnded).IsFalse();
            await Assert.That(row.Meta).IsEqualTo("25m");
            await Assert.That(RailSessionViewModel.TerminalTooltip).IsEqualTo("Started in a terminal, not hosted by a daemon");

            using var hosted = new RailSessionViewModel(ServerRow(status: "active", hosted: true), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, time);
            await Assert.That(hosted.IsTerminalSession).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Owner_initials_show_for_another_users_row_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var theirs = new RailSessionViewModel(ServerRow() with { OwnerIsViewer = false }, new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended));
            await Assert.That(theirs.OwnerInitials).IsEqualTo("TY");
            await Assert.That(theirs.ShowsOwner).IsTrue();
            await Assert.That(theirs.OwnerTooltip).IsEqualTo("Tony Young\nOwner");
            await Assert.That(theirs.OwnerAvatarUrl).IsEqualTo("https://avatars.example/u2");

            using var mine = new RailSessionViewModel(ServerRow() with { OwnerIsViewer = true }, new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended));
            await Assert.That(mine.ShowsOwner).IsFalse();
            await Assert.That(mine.OwnerAvatarUrl).IsNull();

            using var nameless = new RailSessionViewModel(ServerRow() with { OwnerName = null, OwnerIsViewer = false }, new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended));
            await Assert.That(nameless.ShowsOwner).IsTrue();
            await Assert.That(nameless.OwnerAvatarUrl).IsEqualTo("https://avatars.example/u2");

            using var unknown = new RailSessionViewModel(ServerRow(), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended));
            await Assert.That(unknown.ShowsOwner).IsTrue();
            await Assert.That(unknown.OwnerInitials).IsEqualTo("TY");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Repository_label_shows_only_when_not_grouped_by_repository() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var grouping = new BehaviorSubject<RailGrouping>(RailGrouping.Repository);
            using var row = new RailSessionViewModel(ServerRow(), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended), grouping: grouping);

            await Assert.That(row.RepoLabel).IsEqualTo("kurrent-io/kcap-cli");
            await Assert.That(row.ShowsRepo).IsFalse();
            grouping.OnNext(RailGrouping.Date);
            await Assert.That(row.ShowsRepo).IsTrue();

            using var noRepo = new RailSessionViewModel(ServerRow(repoName: null), new BehaviorSubject<string?>(null), NoPending, NotStale,
                _ => { }, _ => { }, At(Ended), grouping: grouping);
            await Assert.That(noRepo.ShowsRepo).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_routes_a_server_row_to_the_server_opener() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var local = new List<string>();
            var remote = new List<string>();
            var server = new List<string>();
            using var row = new RailSessionViewModel(ServerRow(), new BehaviorSubject<string?>(null), NoPending, NotStale,
                local.Add, remote.Add, At(Ended), openServer: server.Add);

            row.OpenCommand.Execute().Subscribe();

            await Assert.That(server).IsEquivalentTo(["s1"]);
            await Assert.That(local).IsEmpty();
            await Assert.That(remote).IsEmpty();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_server_row_greys_out_like_a_stale_remote_one() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var laneStale = new BehaviorSubject<bool>(false);
            using var fresh = new RailSessionViewModel(ServerRow(status: "active"), new BehaviorSubject<string?>(null), NoPending, laneStale,
                _ => { }, _ => { }, At(Started));
            using var idle = new RailSessionViewModel(ServerRow(status: "active", stale: true), new BehaviorSubject<string?>(null), NoPending, laneStale,
                _ => { }, _ => { }, At(Started));

            await Assert.That(fresh.IsStale).IsFalse();
            await Assert.That(idle.IsStale).IsTrue();
            laneStale.OnNext(true);
            await Assert.That(fresh.IsStale).IsTrue();
        });
    }
}
