using System.Collections.Frozen;
using System.Reactive.Linq;
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
    const string LaunchedId = "0123456789abcdef0123456789abcdef";
    const string Sha = "abcdef1234567890abcdef1234567890abcdef12";

    static readonly HandoffLaunchStart AttachedStart = new(
        "", Sha, nameof(HandoffCheckout.CheckoutBranch), "feature/x", null, nameof(HandoffSource.Branch),
        nameof(HandoffDetachReason.None), null, false, null, null);

    static HandoffProbeReplyDto Reply(HandoffLaunchStart start, string completeness = "Unconfirmed", IReadOnlyList<string>? missing = null) =>
        new(null, null, start, completeness, missing ?? []);

    sealed class FakeLaunchClient : ILaunchClient {
        public IReadOnlySet<string> Features = FrozenSet.Create("handoff/1");
        public List<LaunchRequest> Requests { get; } = [];
        public LaunchOutcome Next = new(true, LaunchedId, null);
        public Action<LaunchRequest>? During;

        public Task<IReadOnlySet<string>> GetLaunchFeaturesAsync(CancellationToken ct) => Task.FromResult(Features);

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Requests.Add(request);
            During?.Invoke(request);
            return Task.FromResult(Next);
        }
    }

    sealed class FakeProbeOps : ILocalControlOps {
        public List<HandoffProbeRequestDto> Probes { get; } = [];
        public HandoffProbeReplyDto? Next = Reply(AttachedStart);

        public Task<HandoffProbeReplyDto?> ProbeHandoffAsync(HandoffProbeRequestDto request, CancellationToken ct) {
            Probes.Add(request);
            return Task.FromResult(Next);
        }

        public Task<StopAgentResult> StopAgentAsync(string agentId, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ConsentAckDto> PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConsentAckDto> PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct) => throw new NotSupportedException();
        public Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct) => throw new NotSupportedException();
        public Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct) => throw new NotSupportedException();
        public Task<SendTextResult> SendTextAsync(string agentId, string text, CancellationToken ct) => throw new NotSupportedException();
        public Task<SendTextResult> SendTextWithAttachmentsAsync(string agentId, string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) => throw new NotSupportedException();
        public Task<DaemonSettingsAckDto> PutDaemonSettingsAsync(DaemonSettingsPutDto put, CancellationToken ct) => throw new NotSupportedException();
        public Task<SessionRenameAckDto> RenameSessionAsync(SessionRenameDto rename, CancellationToken ct) => throw new NotSupportedException();
        public Task<HandoffOutcomeDto?> ReadHandoffOutcomeAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
    }

    sealed class Rig : IDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeLaunchClient Launch { get; } = new();
        public FakeServerSessionsService Sessions { get; } = new();
        public FakeProbeOps Ops { get; } = new();
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
        public RepoSessionDto Session = new() {
            SessionId = SessionId, Status = "ended", AccessLevel = "full", Vendor = "codex", Cwd = Checkout,
            Branch = "feature/x", StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        };
        readonly List<HandoffComposerViewModel> _built = [];

        public Rig() {
            Daemon.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(supportedVendors: ["claude", "codex"]));
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1", "input/2", HandoffWire.Capability]));
            Sessions.HandoffContext = new HandoffContextDto(SessionId, "acme", "widgets", "feature/x", null, ["c1"]);
            Drafts = new HandoffDrafts(Failures, Directory, Time);
        }

        public HandoffComposerViewModel Build() {
            var vm = new HandoffComposerViewModel(
                Session, Daemon, Launch, Sessions, Ops,
                new LocalCheckoutFinder(new RepoIdentityResolver(root => Origins.GetValueOrDefault(root)), mainRoot: p => p),
                _ => Task.FromResult<IReadOnlyList<string>>([]),
                Drafts, Lane, Uploader, Directory,
                () => Generation, (id, origin, generation) => Opened.Add((id, origin, generation)),
                Closed.Add, Reopened.Add);
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
        await Assert.That(vm.Error).IsEqualTo(HomeViewModel.FriendlyLaunchFailure("handoff_source_unavailable"));
        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(2);
        await Assert.That(rig.Directory.Placeholders).IsEmpty();
        await Assert.That(rig.Opened).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_delayed_failure_reopens_the_source_with_the_draft() => Run(async rig => {
        var vm = await rig.ReadyAsync();
        vm.Text = "carry on";
        vm.Tray.AddAll([Chip("a.png")]);
        vm.Choices.SelectedModel = "gpt-5";
        vm.Choices.SelectedEffort = "high";
        await vm.StartCommand.Execute();
        await Assert.That(rig.Opened.Count).IsEqualTo(1);

        rig.Failures.OnNext(new LaunchFailure(LaunchedId, "handoff_start_changed"));

        await Assert.That(rig.Closed).IsEquivalentTo([LaunchedId]);
        await Assert.That(rig.Reopened).IsEquivalentTo([$"server:{SessionId}"]);
        await Assert.That(rig.Directory.RemovedPlaceholders).IsEquivalentTo([LaunchedId]);

        var reopened = await rig.ReadyAsync();
        await Assert.That(reopened.Text).IsEqualTo("carry on");
        await Assert.That(reopened.Tray.Count).IsEqualTo(1);
        await Assert.That(reopened.Choices.SelectedVendor).IsEqualTo("codex");
        await Assert.That(reopened.Choices.SelectedModel).IsEqualTo("gpt-5");
        await Assert.That(reopened.Choices.SelectedEffort).IsEqualTo("high");
        await Assert.That(reopened.Error).IsEqualTo(HomeViewModel.FriendlyLaunchFailure("handoff_start_changed"));
        await Assert.That(rig.Ops.Probes.Count).IsEqualTo(2);
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
        await Assert.That(vm.Error).IsEqualTo(HomeViewModel.FriendlyLaunchFailure("handoff_start_changed"));
        await Assert.That(rig.Directory.Placeholders).IsEmpty();
        await Assert.That(rig.Opened).IsEmpty();
        await Assert.That(rig.Reopened).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_started_handoff_adds_a_placeholder_and_opens_it() => Run(async rig => {
        rig.Launch.During = _ => rig.Generation++;
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
}
