using System.Collections.Frozen;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// Every test runs inside AvaloniaSession.WithImmediateRxScheduler, the process-global scheduler
/// HomeViewModelTests pins, so each carries [NotInParallel("AvaloniaSession")].
public class HandoffComposerViewModelTests {
    const string SessionId = "s1";
    const string Checkout = "/w/widgets";
    const string LaunchedId = FakeHandoffLaunchClient.LaunchedId;
    const string Sha = FakeHandoffProbeOps.Sha;

    static readonly HandoffLaunchStart AttachedStart = FakeHandoffProbeOps.AttachedStart;

    static HandoffProbeReplyDto Reply(HandoffLaunchStart start, string completeness = "Unconfirmed", IReadOnlyList<string>? missing = null) =>
        FakeHandoffProbeOps.Reply(start, completeness, missing);

    const string StartChanged = "The branch changed after it was checked, so nothing was started. Check the new starting point and start again.";
    const string SourceUnavailable = "You can no longer hand off this session. It may have been deleted, or your access to it changed.";

    sealed class Rig : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeHandoffLaunchClient Launch { get; } = new();
        public FakeServerSessionsService Sessions { get; } = new();
        public FakeHandoffProbeOps Ops { get; } = new();
        public ScriptedUploader Uploader { get; } = new();
        public Subject<LaunchFailure> Failures { get; } = new();
        public BehaviorSubject<ServerLaneStatus> Lane { get; } = new(new ServerLaneStatus(ServerLaneState.Connected));
        public FakeAgentDirectory Directory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public Dictionary<string, string> Origins { get; } = new() { [Checkout] = "https://github.com/acme/widgets.git" };
        public HandoffDrafts Drafts { get; }
        public int Generation = 7;
        public List<(string Id, AgentOrigin Origin, int Generation)> Opened { get; } = [];
        public List<string> Closed { get; } = [];
        public List<string> Reopened { get; } = [];
        public RecordingNotifier Notifier { get; } = new();
        /// What ReopenServerSessionAsync answers: false is a pin that failed or a navigation while it read.
        public bool ReopenOpens = true;
        /// The repositories this machine knows besides the session's own checkout.
        public List<string> Candidates { get; } = [];
        /// The app's server, which FakeDaemonClientService.Snap's daemon is connected to by default.
        public string? AppServerUrl = "http://localhost:9999";
        /// The epochs ParkSignedOut was asked to park, which signs the lane out as the real one does.
        public List<int> Parked { get; } = [];

        void Park(int epoch) {
            Parked.Add(epoch);
            Lane.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut, Epoch: epoch + 1));
        }
        public RepoSessionDto Session = new() {
            SessionId = SessionId, Status = "ended", AccessLevel = "full", Vendor = "codex", Cwd = Checkout, Title = "Fix the widgets",
            Branch = "feature/x", StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        };
        readonly List<HandoffComposerViewModel> _built = [];

        public Rig() {
            Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", "input/2", HandoffWire.Capability]));
            Sessions.HandoffContext = new HandoffContextDto(SessionId, "acme", "widgets", "feature/x", null, ["c1"]);
            Drafts = new HandoffDrafts(Failures, Directory, Time);
        }

        /// MainWindowViewModel.OpenSessionIfCurrent: refused under a stale generation, and an open moves it on.
        void Open(string id, AgentOrigin origin, int generation) {
            Opened.Add((id, origin, generation));
            if (generation != Generation) return;
            Generation++;
            Current = id;
        }

        /// What the window shows, as MainWindowViewModel tracks it.
        public string? Current;

        /// MainWindowViewModel.CloseFailedLaunch: closes only the failed launch's own workspace, and
        /// the close is a navigation.
        public bool CloseClosesNothing;

        void Close(string id) {
            Closed.Add(id);
            if (CloseClosesNothing || Current != id) return;
            Current = null;
            Generation++;
        }

        Task<bool> Reopen(string sessionId, PinPresentation presentation) {
            Reopened.Add(sessionId);
            if (!ReopenOpens) return Task.FromResult(false);
            Current = sessionId;
            Generation++;
            return Task.FromResult(true);
        }

        public void Navigate() {
            Current = "elsewhere";
            Generation++;
        }

        public HandoffComposerViewModel Build() {
            var vm = new HandoffComposerViewModel(
                Session, Daemon, Launch, Sessions, Ops,
                new LocalCheckoutFinder(new RepoIdentityResolver(root => Origins.GetValueOrDefault(root)), mainRoot: p => p),
                _ => Task.FromResult<IReadOnlyList<string>>(Candidates),
                Drafts, Lane, Uploader, Directory,
                () => Generation, Open,
                Close, Reopen, Notifier, AppServerUrl, onUnauthorized: Park);
            _built.Add(vm);
            return vm;
        }

        public async Task<HandoffComposerViewModel> ReadyAsync() {
            var vm = Build();
            await vm.InitializeAsync(CancellationToken.None);
            return vm;
        }

        public void Dispose() {
            foreach (var vm in _built) vm.Dispose();
            Drafts.Dispose();
            Directory.Dispose();
            Sessions.Dispose();
            Failures.Dispose();
            Lane.Dispose();
        }
    }

    static StagedAttachment Chip(string name) => new(name, "image/png", new byte[] { 1, 2, 3 });

    static AgentRow Row(string id) =>
        AgentRow.FromLocal(
            new AgentStatusDto(id, "agent", "codex", Checkout, "Running",
                FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: null,
                RequesterDisplay: null),
            new RepoIdentity("repo:acme/widgets", "acme/widgets"));

    static Task Run(Func<Rig, Task> body) => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var rig = new Rig();
        await body(rig);
    });

    // ---- eligibility ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Below_full_access_is_disabled_with_the_reason() => Run(async rig => {
        rig.Session = rig.Session with { AccessLevel = "activity" };
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("You need full access to this session to hand it off");
        await Assert.That(rig.Ops.Probes).IsEmpty();
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    /// A pinned row carries no access level until a listing holds it; the context read decides.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unknown_access_level_reaches_a_probed_start() => Run(async rig => {
        rig.Session = rig.Session with { AccessLevel = "" };
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsNull();
        await Assert.That(vm.ProbedStart).IsEqualTo(AttachedStart);
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unknown_access_level_the_server_hides_needs_full_access() => Run(async rig => {
        rig.Session = rig.Session with { AccessLevel = "" };
        rig.Sessions.HandoffContextOutcome = ServerReadOutcome.Unavailable;
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("You need full access to this session to hand it off");
        await Assert.That(rig.Ops.Probes).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failed_context_read_is_unavailable_whatever_the_access_level() => Run(async rig => {
        rig.Sessions.HandoffContextOutcome = ServerReadOutcome.Failed;
        rig.Session = rig.Session with { AccessLevel = "" };
        await Assert.That((await rig.ReadyAsync()).DisabledReason).IsEqualTo("Couldn't load this session's details from your kcap server");
        rig.Session = rig.Session with { AccessLevel = "full" };
        rig.Sessions.HandoffContextOutcome = ServerReadOutcome.Unavailable;
        await Assert.That((await rig.ReadyAsync()).DisabledReason).IsEqualTo("Couldn't load this session's details from your kcap server");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_old_server_is_disabled_with_the_reason() => Run(async rig => {
        rig.Launch.Features = FrozenSet<string>.Empty;
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("Your kcap server doesn't support hand-off yet");
        await Assert.That(rig.Ops.Probes).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_old_daemon_is_disabled_with_the_reason() => Run(async rig => {
        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", "input/2"]));
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("Your local kcap daemon doesn't support hand-off yet. Update kcap to use it.");
        await Assert.That(rig.Ops.Probes).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task No_local_checkout_is_disabled_with_the_reason() => Run(async rig => {
        rig.Origins.Clear();
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("There's no checkout of acme/widgets on this machine");
        await Assert.That(rig.Ops.Probes).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task The_reasons_are_checked_access_then_server_then_daemon_then_checkout() => Run(async rig => {
        rig.Session = rig.Session with { AccessLevel = "overview" };
        rig.Launch.Features = FrozenSet<string>.Empty;
        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/2"]));
        rig.Origins.Clear();

        await Assert.That((await rig.ReadyAsync()).DisabledReason).IsEqualTo("You need full access to this session to hand it off");
        rig.Session = rig.Session with { AccessLevel = "full" };
        await Assert.That((await rig.ReadyAsync()).DisabledReason).IsEqualTo("Your kcap server doesn't support hand-off yet");
        rig.Launch.Features = FrozenSet.Create("handoff/1");
        await Assert.That((await rig.ReadyAsync()).DisabledReason).IsEqualTo("Your local kcap daemon doesn't support hand-off yet. Update kcap to use it.");
        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, [HandoffWire.Capability]));
        await Assert.That((await rig.ReadyAsync()).DisabledReason).IsEqualTo("There's no checkout of acme/widgets on this machine");
    });

    // ---- probe ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_probe_start_sets_the_strip_label() => Run(async rig => {
        const string prefix = "New session in a fresh worktree";
        (HandoffLaunchStart Start, string Label)[] cases = [
            (AttachedStart, $"{prefix} on feature/x"),
            (Detached(nameof(HandoffSource.Branch), "feature/x", nameof(HandoffDetachReason.CheckedOutElsewhere)),
                $"{prefix}, detached at abcdef1 of feature/x (checked out in another worktree)"),
            (Detached(nameof(HandoffSource.Branch), "feature/x", nameof(HandoffDetachReason.LocalBranchDiverged)),
                $"{prefix}, detached at abcdef1 of feature/x (your local branch has diverged)"),
            (Detached(nameof(HandoffSource.ForkPullRequest), "patch-1", nameof(HandoffDetachReason.ForkHead)) with { HeadIsFork = true, HeadOwner = "octo", HeadRepo = "widgets" },
                $"{prefix}, detached at abcdef1 of octo:patch-1 (a fork)"),
            (Detached(nameof(HandoffSource.DefaultBranch), "main", nameof(HandoffDetachReason.BranchAbsent)),
                $"{prefix}, detached at abcdef1 of main (feature/x isn't on origin)"),
        ];

        foreach (var (start, label) in cases) {
            rig.Ops.Next = Reply(start);
            var vm = await rig.ReadyAsync();
            await Assert.That(vm.StripLabel).IsEqualTo(label);
            await Assert.That(vm.DisabledReason).IsNull();
            await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
        }

        static HandoffLaunchStart Detached(string source, string branch, string reason) =>
            AttachedStart with { Checkout = nameof(HandoffCheckout.Detached), Source = source, Branch = branch, DetachReason = reason };
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_fork_target_uses_the_head_ref_and_carries_the_head_repository() => Run(async rig => {
        rig.Sessions.HandoffContext = rig.Sessions.HandoffContext! with {
            PullRequest = new HandoffPullRequestDto(42, "t", "open", "patch-1", "deadbeef", "octo", "widgets-fork", true),
        };
        await rig.ReadyAsync();

        var probe = rig.Ops.Probes.Single();
        await Assert.That(probe.Branch).IsEqualTo("patch-1");
        await Assert.That(probe.PullRequestNumber).IsEqualTo(42);
        await Assert.That(probe.HeadIsFork).IsTrue();
        await Assert.That(probe.HeadOwner).IsEqualTo("octo");
        await Assert.That(probe.HeadRepo).IsEqualTo("widgets-fork");
        await Assert.That(probe.RepoPath).IsEqualTo(Checkout);
        await Assert.That(probe.RepoOwner).IsEqualTo("acme");
        await Assert.That(probe.RepoName).IsEqualTo("widgets");
        await Assert.That(probe.SessionCommits!).IsEquivalentTo(["c1"]);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_same_repository_pr_uses_the_head_ref_without_a_fork() => Run(async rig => {
        rig.Sessions.HandoffContext = rig.Sessions.HandoffContext! with {
            PullRequest = new HandoffPullRequestDto(42, "t", "open", "feature/pr", "deadbeef", "acme", "widgets", false),
        };
        await rig.ReadyAsync();

        var probe = rig.Ops.Probes.Single();
        await Assert.That(probe.Branch).IsEqualTo("feature/pr");
        await Assert.That(probe.PullRequestNumber).IsEqualTo(42);
        await Assert.That(probe.HeadIsFork).IsFalse();
        await Assert.That(probe.HeadOwner).IsNull();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_pr_head_in_an_unrelated_repository_falls_back_to_the_recorded_branch() => Run(async rig => {
        rig.Sessions.HandoffContext = rig.Sessions.HandoffContext! with {
            PullRequest = new HandoffPullRequestDto(42, "t", "open", "elsewhere", "deadbeef", "stranger", "other", false),
        };
        await rig.ReadyAsync();

        var probe = rig.Ops.Probes.Single();
        await Assert.That(probe.Branch).IsEqualTo("feature/x");
        await Assert.That(probe.PullRequestNumber).IsNull();
        await Assert.That(probe.HeadIsFork).IsFalse();
        await Assert.That(probe.HeadOwner).IsNull();
        await Assert.That(probe.HeadRepo).IsNull();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_fetch_error_disables_start() => Run(async rig => {
        rig.Ops.Next = new HandoffProbeReplyDto(null, "Authentication failed", null, null, null);
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("Couldn't reach origin: Authentication failed");
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    /// The refusal keeps its own line, so the re-probe's reason is not hidden behind it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_reprobe_after_a_refusal_shows_its_own_reason_beside_the_refusal() => Run(async rig => {
        rig.Launch.Next = new LaunchOutcome(false, null, "handoff_start_changed: feature/x moved");
        var vm = await rig.ReadyAsync();
        rig.Ops.Next = new HandoffProbeReplyDto(null, "Authentication failed", null, null, null);

        await vm.StartCommand.Execute();
        await vm.PendingEvaluationForTesting;

        await Assert.That(vm.Error).IsEqualTo(StartChanged);
        await Assert.That(vm.DisabledReason).IsEqualTo("Couldn't reach origin: Authentication failed");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_probe_in_flight_says_it_is_checking() => Run(async rig => {
        var gate = new TaskCompletionSource<HandoffProbeReplyDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Ops.Gate = gate;
        var vm = rig.Build();

        var evaluating = vm.InitializeAsync(CancellationToken.None);
        await Assert.That(vm.Progress).IsEqualTo("Checking where the new session would start…");
        await Assert.That(vm.DisabledReason).IsNull();
        gate.SetResult(Reply(AttachedStart));
        await evaluating;

        await Assert.That(vm.Progress).IsNull();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_connecting_daemon_says_it_is_waiting() => Run(async rig => {
        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.Progress).IsEqualTo("Waiting for your local kcap daemon…");
        await Assert.That(vm.DisabledReason).IsNull();
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Missing_commits_set_the_completeness_note() => Run(async rig => {
        rig.Ops.Next = Reply(AttachedStart, nameof(HandoffCompleteness.PartlyMissing), ["c1", "c2"]);
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.CompletenessNote).IsEqualTo("Part of the session's work is missing: 2 commits");
        await Assert.That(vm.WorkMissingOrUnconfirmed).IsTrue();

        rig.Ops.Next = Reply(AttachedStart, nameof(HandoffCompleteness.Unconfirmed));
        var unconfirmed = await rig.ReadyAsync();
        await Assert.That(unconfirmed.CompletenessNote).IsEqualTo("The session may have left work that isn't pushed");
        await Assert.That(unconfirmed.WorkMissingOrUnconfirmed).IsTrue();
    });

    // ---- launch ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Start_sends_the_probed_start_and_source() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();

        var request = rig.Launch.Requests.Single();
        await Assert.That(request.Handoff).IsEqualTo(AttachedStart with { SourceSessionId = SessionId });
        await Assert.That(request.Handoff!.StartSha).IsEqualTo(Sha);
        await Assert.That(request.RepoPath).IsEqualTo(Checkout);
        await Assert.That(request.DaemonName).IsEqualTo(rig.Daemon.DaemonName);
        await Assert.That(request.Vendor).IsEqualTo("codex");
        await Assert.That(request.Prompt).IsEqualTo("carry on");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Attachments_are_uploaded_and_sent() => Run(async rig => {
        rig.Uploader.Next = new UploadOutcome(UploadKind.Uploaded, ["u1"], null);
        var vm = await rig.ReadyAsync();
        var chip = Chip("a.png");
        vm.Tray.AddAll([chip]);
        await vm.StartCommand.Execute();

        await Assert.That(rig.Uploader.Calls.Single().Single()).IsSameReferenceAs(chip);
        await Assert.That(rig.Launch.Requests.Single().AttachmentIds!).IsEquivalentTo(["u1"]);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_immediate_refusal_keeps_text_and_attachments_and_reprobes() => Run(async rig => {
        rig.Launch.Next = new LaunchOutcome(false, null, "handoff_source_unavailable");
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("a.png")]);
        await vm.StartCommand.Execute();

        await Assert.That(vm.Text).IsEqualTo("carry on");
        await Assert.That(vm.Tray.Count).IsEqualTo(1);
        await Assert.That(vm.Error).IsEqualTo(SourceUnavailable);
        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(2);
        await Assert.That(rig.Directory.Placeholders).IsEmpty();
        await Assert.That(rig.Opened).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_delayed_failure_reopens_the_source_with_the_draft() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        var chip = Chip("a.png");
        await vm.Choices.ChooseHarnessAsync("claude");
        vm.Choices.SelectedModel = "claude-opus-5";
        vm.Choices.SelectedEffort = "high";
        vm.Tray.Restore([chip]);
        await vm.StartCommand.Execute();
        await Assert.That(rig.Opened.Count).IsEqualTo(1);
        // Opening the new session tears the source's workspace down.
        vm.Dispose();

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed"));

        await Assert.That(rig.Closed).IsEquivalentTo([LaunchedId]);
        await Assert.That(rig.Reopened).IsEquivalentTo([SessionId]);
        await Assert.That(rig.Directory.RemovedPlaceholders).IsEquivalentTo([LaunchedId]);

        var reopened = await rig.ReadyAsync();
        await Assert.That(reopened.Text).IsEqualTo("carry on");
        await Assert.That(reopened.Tray.Items.Single()).IsSameReferenceAs(chip);
        await Assert.That(reopened.Choices.SelectedVendor).IsEqualTo("claude");
        await Assert.That(reopened.Choices.SelectedModel).IsEqualTo("claude-opus-5");
        await Assert.That(reopened.Choices.SelectedEffort).IsEqualTo("high");
        await Assert.That(reopened.Error).IsEqualTo(StartChanged);
        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(2);
    });

    const string StartChangedNotice =
        "The hand-off of Fix the widgets didn't start: The branch changed after it was checked, so nothing was started. "
      + "Check the new starting point and start again. Your message is back in that session.";

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_reopened_source_posts_no_notice() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();
        vm.Dispose();

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed"));

        await Assert.That(rig.Reopened).IsEquivalentTo([SessionId]);
        await Assert.That(rig.Notifier.Notified).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_source_that_cannot_be_reopened_posts_a_notice() => Run(async rig => {
        rig.ReopenOpens = false;
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();
        vm.Dispose();

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed"));

        await Assert.That(rig.Reopened).IsEquivalentTo([SessionId]);
        await Assert.That(rig.Notifier.Notified).IsEquivalentTo([StartChangedNotice]);
        await Assert.That((await rig.ReadyAsync()).Text).IsEqualTo("carry on");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_delayed_failure_after_navigating_away_posts_a_notice() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();
        vm.Dispose();
        rig.Navigate();

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: feature/x moved"));

        await Assert.That(rig.Notifier.Notified).IsEquivalentTo([StartChangedNotice]);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_untitled_source_is_named_by_its_id_prefix() => Run(async rig => {
        rig.Session = rig.Session with { SessionId = "0123456789abcdef", Title = null };
        rig.Sessions.HandoffContext = rig.Sessions.HandoffContext! with { SessionId = "0123456789abcdef" };
        var vm = await rig.ReadyAsync();
        await vm.StartCommand.Execute();
        vm.Dispose();
        rig.Navigate();

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_source_unavailable"));

        await Assert.That(rig.Notifier.Notified.Single()).StartsWith("The hand-off of 01234567 didn't start: You can no longer hand off this session.");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failure_before_StartAsync_returns_is_still_matched() => Run(async rig => {
        rig.Launch.During = _ => rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed"));
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("a.png")]);
        await vm.StartCommand.Execute();

        await Assert.That(vm.Text).IsEqualTo("carry on");
        await Assert.That(vm.Tray.Count).IsEqualTo(1);
        await Assert.That(vm.Error).IsEqualTo(StartChanged);
        await Assert.That(rig.Directory.Placeholders).IsEmpty();
        await Assert.That(rig.Opened).IsEmpty();
        await Assert.That(rig.Reopened).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_started_handoff_adds_a_placeholder_and_opens_it() => Run(async rig => {
        rig.Launch.During = _ => rig.Navigate();
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();

        var placeholder = rig.Directory.Placeholders.Single();
        await Assert.That(placeholder.Id).IsEqualTo(LaunchedId);
        await Assert.That(placeholder.Vendor).IsEqualTo("codex");
        await Assert.That(placeholder.RepoPath).IsEqualTo(Checkout);
        await Assert.That(rig.Opened).IsEquivalentTo([(LaunchedId, AgentOrigin.Local, 7)]);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_confirmed_launch_drops_the_draft() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();

        rig.Directory.Rows.AddOrUpdate(Row(LaunchedId));
        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "too late"));

        await Assert.That(rig.Reopened).IsEmpty();
        await Assert.That(rig.Closed).IsEmpty();
        await Assert.That(rig.Drafts.TakeFailed(SessionId)).IsNull();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_delayed_failure_after_navigating_away_keeps_the_draft_without_moving_the_user() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();
        vm.Dispose();
        rig.Navigate();

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: feature/x moved"));

        await Assert.That(rig.Closed).IsEquivalentTo([LaunchedId]);
        await Assert.That(rig.Reopened).IsEmpty();
        var later = await rig.ReadyAsync();
        await Assert.That(later.Text).IsEqualTo("carry on");
        await Assert.That(later.Error).IsEqualTo(StartChanged);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_live_source_composer_takes_its_failed_draft_back_once() => Run(async rig => {
        // The user navigates while the launch is in flight, so the open is refused and the composer stays.
        rig.Launch.During = _ => rig.Navigate();
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        var chip = Chip("a.png");
        vm.Tray.Restore([chip]);
        await vm.Choices.ChooseHarnessAsync("claude");
        vm.Choices.SelectedEffort = "low";
        await vm.StartCommand.Execute();
        await Assert.That(vm.Text).IsEqualTo("");

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: feature/x moved"));

        await Assert.That(vm.Text).IsEqualTo("carry on");
        await Assert.That(vm.Tray.Items.Single()).IsSameReferenceAs(chip);
        await Assert.That(vm.Choices.SelectedVendor).IsEqualTo("claude");
        await Assert.That(vm.Choices.SelectedEffort).IsEqualTo("low");
        await Assert.That(vm.Error).IsEqualTo(StartChanged);
        await vm.PendingEvaluationForTesting;
        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(2);
        await Assert.That(rig.Reopened).IsEmpty();
        await Assert.That((await rig.ReadyAsync()).Text).IsEqualTo("");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_disconnected_server_reads_as_unreachable_and_rechecks_on_connect() => Run(async rig => {
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        rig.Launch.Features = FrozenSet<string>.Empty;
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("Couldn't reach your kcap server");
        await Assert.That(rig.Ops.Probes).IsEmpty();

        rig.Launch.Features = FrozenSet.Create("handoff/1");
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
        await vm.PendingEvaluationForTesting;

        await Assert.That(vm.DisabledReason).IsNull();
        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(1);
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_signed_out_lane_asks_for_a_sign_in() => Run(async rig => {
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("Sign in to your kcap server to hand off");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Refusal_codes_are_worded_without_the_code() => Run(async rig => {
        (string Reason, string? Worded)[] cases = [
            ("handoff_start_changed: feature/x is at 1a2b, not 3c4d", StartChanged),
            ("handoff_source_unavailable: The session to continue cannot be read.", SourceUnavailable),
            ("invalid_handoff: the block is incomplete",
                "Your local kcap daemon couldn't start this hand-off, so nothing was started. Update kcap and try again."),
            ("repo_mismatch", "The checkout on this machine isn't a clone of this session's repository, so nothing was started."),
            ("repo_not_allowed", "Your local kcap daemon isn't allowed to work in that checkout, so nothing was started."),
            ("some_new_code: the thing broke", null),
            ("some_new_code", null),
        ];
        foreach (var (reason, worded) in cases) {
            rig.Launch.Next = new LaunchOutcome(false, null, reason);
            var vm = await rig.ReadyAsync();
            await vm.StartCommand.Execute();
            var code = reason.Split(':')[0];
            await Assert.That(vm.Error).IsNotNull();
            await Assert.That(vm.Error!).DoesNotContain(code);
            if (worded is not null) await Assert.That(vm.Error).IsEqualTo(worded);
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_composer_disposed_during_a_refused_launch_holds_the_draft_for_the_source() => Run(async rig => {
        rig.Launch.Next = new LaunchOutcome(false, null, "handoff_source_unavailable: gone");
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        var chip = Chip("a.png");
        vm.Tray.Restore([chip]);
        rig.Launch.During = _ => { rig.Navigate(); vm.Dispose(); };

        await vm.StartCommand.Execute();

        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(1);
        var next = await rig.ReadyAsync();
        await Assert.That(next.Text).IsEqualTo("carry on");
        await Assert.That(next.Tray.Items.Single()).IsSameReferenceAs(chip);
        await Assert.That(next.Error).IsEqualTo(SourceUnavailable);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_composer_disposed_during_its_launch_leaves_a_delayed_failure_for_the_source() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        var chip = Chip("a.png");
        vm.Tray.Restore([chip]);
        rig.Launch.During = _ => { rig.Navigate(); vm.Dispose(); };

        await vm.StartCommand.Execute();
        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));

        await Assert.That(vm.Text).IsEqualTo("carry on");
        await Assert.That(rig.Reopened).IsEmpty();
        var next = await rig.ReadyAsync();
        await Assert.That(next.Text).IsEqualTo("carry on");
        await Assert.That(next.Tray.Items.Single()).IsSameReferenceAs(chip);
        await Assert.That(next.Error).IsEqualTo(StartChanged);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_composer_disposed_during_its_launch_leaves_an_early_failure_for_the_source() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        rig.Launch.During = _ => {
            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));
            vm.Dispose();
        };

        await vm.StartCommand.Execute();

        var next = await rig.ReadyAsync();
        await Assert.That(next.Text).IsEqualTo("carry on");
        await Assert.That(next.Error).IsEqualTo(StartChanged);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failed_features_read_reads_as_unreachable_not_too_old() => Run(async rig => {
        rig.Launch.Features = null;
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo("Couldn't reach your kcap server");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_restored_draft_keeps_text_typed_since_and_the_sent_prompt() => Run(async rig => {
        rig.Launch.During = _ => rig.Navigate();
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        var chip = Chip("a.png");
        vm.Tray.Restore([chip]);
        await vm.Choices.ChooseHarnessAsync("claude");
        await vm.StartCommand.Execute();
        vm.Text = "a new idea";

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));

        await Assert.That(vm.Text).IsEqualTo("a new idea\n\ncarry on");
        await Assert.That(vm.Tray.Items.Single()).IsSameReferenceAs(chip);
        await Assert.That(vm.Choices.SelectedVendor).IsEqualTo("claude");
        await Assert.That(vm.Error).IsEqualTo(StartChanged);
    });

    [Test]
    [NotInParallel]
    public Task A_fault_after_dispose_is_logged_not_raised() => Run(async rig => {
        using var capture = ConsoleOutput.StartErrorCapture();
        var vm = await rig.ReadyAsync();
        rig.Launch.During = _ => vm.Dispose();
        rig.Launch.Throw = new InvalidOperationException("hub went away");

        try { await vm.StartCommand.Execute(); } catch (InvalidOperationException) { }

        await Assert.That(capture.GetCapturedError()).Contains("kcap: hand-off start: hub went away");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_trailing_failure_does_not_move_a_user_whose_workspace_filled() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();
        vm.Dispose();
        // The agent's own row filled the open workspace, so the failed-launch close closes nothing.
        rig.CloseClosesNothing = true;

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));

        await Assert.That(rig.Closed).IsEquivalentTo([LaunchedId]);
        await Assert.That(rig.Reopened).IsEmpty();
        await Assert.That((await rig.ReadyAsync()).Text).IsEqualTo("carry on");
    });

    // ---- review follow-ups ----

    const string SignIn = "Sign in to your kcap server to hand off";
    const string OtherCheckout = "/w/widgets-2";

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_reconnect_reads_the_servers_features_again() => Run(async rig => {
        rig.Launch.Features = FrozenSet.Create("launch/1");
        var vm = await rig.ReadyAsync();
        await Assert.That(vm.DisabledReason).IsEqualTo("Your kcap server doesn't support hand-off yet");

        rig.Launch.Features = FrozenSet.Create("handoff/1");
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected));
        await vm.PendingEvaluationForTesting;

        await Assert.That(vm.DisabledReason).IsNull();
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsTrue();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Losing_the_server_disables_a_ready_start() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.SignedOut));
        await vm.PendingEvaluationForTesting;

        await Assert.That(vm.DisabledReason).IsEqualTo(SignIn);
        await Assert.That(vm.ProbedStart).IsNull();
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unauthorized_context_read_asks_for_a_sign_in() => Run(async rig => {
        rig.Sessions.HandoffContextOutcome = ServerReadOutcome.Unauthorized;
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo(SignIn);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unknown_completeness_reads_as_unconfirmed() => Run(async rig => {
        rig.Ops.Next = Reply(AttachedStart, "SomethingNewer");
        var vm = await rig.ReadyAsync();
        await Assert.That(vm.WorkMissingOrUnconfirmed).IsTrue();

        rig.Ops.Next = Reply(AttachedStart, nameof(HandoffCompleteness.Complete));
        await Assert.That((await rig.ReadyAsync()).WorkMissingOrUnconfirmed).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Unsent_text_and_attachments_come_back_after_navigating_away() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "not yet";
        var chip = Chip("a.png");
        vm.Tray.AddAll([chip]);
        vm.Dispose();

        var next = await rig.ReadyAsync();

        await Assert.That(next.Text).IsEqualTo("not yet");
        await Assert.That(next.Tray.Items.Single()).IsSameReferenceAs(chip);
        await Assert.That(next.Error).IsNull();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_daemon_on_another_server_is_disabled_with_the_reason() => Run(async rig => {
        rig.AppServerUrl = "https://kcap.example.com";
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo(HandoffComposerViewModel.DaemonOnAnotherServer);
        await Assert.That(rig.Ops.Probes).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_daemon_hosting_no_agent_is_disabled_with_the_reason() => Run(async rig => {
        rig.Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Edits_made_while_the_launch_is_in_flight_stay() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("sent.png")]);
        var later = Chip("later.png");
        rig.Launch.During = _ => {
            rig.Navigate();
            vm.Text = "and then this";
            vm.Tray.AddAll([later]);
        };

        await vm.StartCommand.Execute();

        await Assert.That(vm.Text).IsEqualTo("and then this");
        await Assert.That(vm.Tray.Items.Single()).IsSameReferenceAs(later);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unauthorized_upload_signs_the_lane_out() => Run(async rig => {
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Epoch: 4));
        rig.Uploader.Next = UploadOutcome.Unauthorized("401");
        var vm = await rig.ReadyAsync();
        vm.Tray.AddAll([Chip("a.png")]);

        await vm.StartCommand.Execute();
        await vm.PendingEvaluationForTesting;

        await Assert.That(rig.Parked).IsEquivalentTo([4]);
        await Assert.That(vm.DisabledReason).IsEqualTo(SignIn);
        await Assert.That(rig.Launch.Requests).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_unauthorized_launch_signs_the_lane_out() => Run(async rig => {
        rig.Lane.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Epoch: 4));
        rig.Launch.Next = new LaunchOutcome(false, null, null, Unauthorized: true);
        var vm = await rig.ReadyAsync();

        await vm.StartCommand.Execute();
        await vm.PendingEvaluationForTesting;

        await Assert.That(rig.Parked).IsEquivalentTo([4]);
        await Assert.That(vm.DisabledReason).IsEqualTo(SignIn);
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_accepted_launch_with_an_unusable_id_spends_what_it_sent() => Run(async rig => {
        rig.Launch.Next = new LaunchOutcome(true, " ", null);
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("a.png")]);

        await vm.StartCommand.Execute();

        await Assert.That(vm.Text).IsEqualTo("");
        await Assert.That(vm.Tray.Count).IsEqualTo(0);
        await Assert.That(vm.Error).IsEqualTo(HomeViewModel.UnusableIdMessage);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_source_refusal_reads_the_context_again() => Run(async rig => {
        rig.Launch.Next = new LaunchOutcome(false, null, "handoff_source_unavailable");
        rig.Launch.During = _ => rig.Sessions.HandoffContextOutcome = ServerReadOutcome.Failed;
        var vm = await rig.ReadyAsync();

        await vm.StartCommand.Execute();

        await Assert.That(vm.DisabledReason).IsEqualTo("Couldn't load this session's details from your kcap server");
        await Assert.That(await vm.StartCommand.CanExecute.FirstAsync()).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Attachments_withdrawn_during_the_upload_stop_the_launch() => Run(async rig => {
        var upload = new TaskCompletionSource<UploadOutcome>();
        rig.Uploader.Pending = upload;
        var vm = await rig.ReadyAsync();
        vm.Tray.AddAll([Chip("a.png")]);

        var starting = vm.StartCommand.Execute().ToTask();
        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", HandoffWire.Capability]));
        upload.SetResult(new UploadOutcome(UploadKind.Uploaded, ["u1"], null));
        await starting;

        await Assert.That(rig.Launch.Requests).IsEmpty();
        await Assert.That(vm.Error).IsEqualTo(HomeViewModel.AttachHintFor(true, false));
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failure_arriving_during_another_launch_is_taken_back_after_it() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "first";
        rig.Launch.During = _ => rig.Navigate();
        await vm.StartCommand.Execute();

        const string second = "fedcba9876543210fedcba9876543210";
        rig.Launch.Next = new LaunchOutcome(true, second, null);
        rig.Launch.During = _ => {
            rig.Navigate();
            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));
        };
        vm.Text = "second";
        await vm.StartCommand.Execute();

        await Assert.That(vm.Text).IsEqualTo("first");
        await Assert.That(vm.Error).IsEqualTo(StartChanged);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_reopened_source_takes_back_a_failure_held_after_its_old_composer_went() => Run(async rig => {
        var upload = new TaskCompletionSource<UploadOutcome>();
        rig.Uploader.Pending = upload;
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("a.png")]);
        var starting = vm.StartCommand.Execute().ToTask();
        vm.Dispose();
        var next = await rig.ReadyAsync();

        upload.SetResult(new UploadOutcome(UploadKind.Unreachable, [], "the upload failed"));
        await starting;

        await Assert.That(next.Text).IsEqualTo("carry on");
        await Assert.That(next.Error).IsEqualTo("the upload failed");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Another_checkout_is_probed_after_one_the_daemon_refuses() => Run(async rig => {
        rig.Origins[OtherCheckout] = "https://github.com/acme/widgets.git";
        rig.Candidates.Add(OtherCheckout);
        rig.Ops.Answer = request => request.RepoPath == Checkout
            ? new HandoffProbeReplyDto(HandoffRefusals.RepoNotAllowed, null, null, null, [])
            : Reply(AttachedStart);
        var vm = await rig.ReadyAsync();

        await Assert.That(vm.DisabledReason).IsNull();
        await vm.StartCommand.Execute();
        await Assert.That(rig.Launch.Requests.Single().RepoPath).IsEqualTo(OtherCheckout);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_early_failure_waits_for_a_slow_launch_call() => Run(async rig => {
        rig.Launch.During = _ => {
            rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed"));
            rig.Time.Advance(TimeSpan.FromMinutes(2));
        };
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        await vm.StartCommand.Execute();

        await Assert.That(vm.Text).IsEqualTo("carry on");
        await Assert.That(vm.Error).IsEqualTo(StartChanged);
        await Assert.That(rig.Directory.Placeholders).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Two_failed_launches_from_one_source_both_come_back() => Run(async rig => {
        const string second = "fedcba9876543210fedcba9876543210";
        var a = Chip("a.png");
        var b = Chip("b.png");
        rig.Drafts.Hold(SessionId, LaunchedId, new HandoffDraft("first", [a], "codex", "", null, null, null, null));
        rig.Drafts.Hold(SessionId, second, new HandoffDraft("second", [b], "claude", "", null, null, null, null));
        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));
        rig.Failures.OnNext(new LaunchFailure(second, "handoff_start_changed: moved"));

        var vm = await rig.ReadyAsync();

        await Assert.That(vm.Text).IsEqualTo("first\n\nsecond");
        await Assert.That(vm.Tray.Items.ToArray()).IsEquivalentTo([a, b], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(vm.Choices.SelectedVendor).IsEqualTo("claude");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_daemon_change_during_the_upload_stops_the_launch_and_rechecks() => Run(async rig => {
        var upload = new TaskCompletionSource<UploadOutcome>();
        rig.Uploader.Pending = upload;
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("a.png")]);

        var starting = vm.StartCommand.Execute().ToTask();
        rig.Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: []));
        upload.SetResult(new UploadOutcome(UploadKind.Uploaded, ["u1"], null));
        await starting;
        await vm.PendingEvaluationForTesting;

        await Assert.That(rig.Launch.Requests).IsEmpty();
        await Assert.That(vm.Text).IsEqualTo("carry on");
        await Assert.That(vm.Error).IsEqualTo(HandoffComposerViewModel.ChangedDuringUpload);
        await Assert.That(vm.DisabledReason).IsEqualTo(HomeViewModel.NoHarnessesNotice(null));
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Failed_files_past_the_tray_limit_come_back_after_the_next_launch() => Run(async rig => {
        const string second = "fedcba9876543210fedcba9876543210";
        var first = Enumerable.Range(0, 7).Select(i => Chip($"a{i}.png")).ToArray();
        var later = Enumerable.Range(0, 7).Select(i => Chip($"b{i}.png")).ToArray();
        rig.Drafts.Hold(SessionId, LaunchedId, new HandoffDraft("first", first, "codex", "", null, null, null, null));
        rig.Drafts.Hold(SessionId, second, new HandoffDraft("second", later, "codex", "", null, null, null, null));
        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed: moved"));
        rig.Failures.OnNext(new LaunchFailure(second, "handoff_start_changed: moved"));

        var vm = await rig.ReadyAsync();

        await Assert.That(vm.Tray.Count).IsEqualTo(InputWire.MaxAttachmentsPerPrompt);
        await Assert.That(vm.Error).Contains("4 more files");

        rig.Uploader.Next = new UploadOutcome(UploadKind.Uploaded, ["u1"], null);
        rig.Launch.During = _ => rig.Navigate();
        await vm.StartCommand.Execute();

        await Assert.That(rig.Uploader.Calls.Single().Count).IsEqualTo(InputWire.MaxAttachmentsPerPrompt);
        await Assert.That(vm.Tray.Items.Select(f => f.FileName).ToArray())
            .IsEquivalentTo(later.Skip(3).Select(f => f.FileName), TUnit.Assertions.Enums.CollectionOrdering.Matching);
    });
}
