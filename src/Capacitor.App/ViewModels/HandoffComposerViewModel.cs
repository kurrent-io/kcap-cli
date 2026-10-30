using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The composer of an ended server session: whether it can be handed off from this machine, where
/// the new session would start, and the launch. A hand-off never degrades into an ordinary launch,
/// so Start needs the server's and the daemon's handoff/1 and a probed start, and what launches is
/// exactly the start the strip shows.
public sealed class HandoffComposerViewModel : ReactiveObject, IDisposable, IAttachmentSink {
    public const string NeedsFullAccess = "You need full access to this session to hand it off";
    public const string ServerTooOld = "Your kcap server doesn't support hand-off yet";
    public const string DaemonTooOld = "Your local kcap daemon doesn't support hand-off yet. Update kcap to use it.";
    public const string ServerUnreachable = "Couldn't reach your kcap server";
    public const string DaemonNotRunning = "Your local kcap daemon isn't running. Start it to hand off.";
    public const string ContextUnavailable = "Couldn't load this session's details from your kcap server";
    public const string NoRepository = "This session isn't linked to a GitHub repository, so it can't be handed off";
    public const string ProbeFailed = "Your local kcap daemon didn't answer, so the starting point couldn't be checked";
    public const string InvalidTarget = "There's no branch or commit to start this hand-off from";
    public const string SignInToHandOff = "Sign in to your kcap server to hand off";
    const string StripPrefix = "New session in a fresh worktree";
    const string FullAccess = "full";

    public static string NoCheckout(string owner, string name) => $"There's no checkout of {owner}/{name} on this machine";
    public static string OriginUnreachable(string detail) => $"Couldn't reach origin: {detail}";
    static string NotAllowed(string path) => $"Your local kcap daemon isn't allowed to work in {path}";

    /// What the server's context names as the hand-off target, mapped for the probe.
    internal sealed record Target(string? Branch, int? PullRequestNumber, bool HeadIsFork, string? HeadOwner, string? HeadRepo);

    sealed record Evaluation(
        string? Reason, HandoffLaunchStart? Start = null, string? Checkout = null, string? Label = null,
        string? CompletenessNote = null, bool Waiting = false);

    RepoSessionDto _session;
    readonly IDaemonClientService _daemon;
    readonly ILaunchClient _launch;
    readonly IServerSessionsService _sessions;
    readonly ILocalControlOps _ops;
    readonly LocalCheckoutFinder _finder;
    readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _candidateRoots;
    readonly HandoffDrafts _drafts;
    readonly IAttachmentUploader _uploader;
    readonly IAgentDirectory? _directory;
    readonly Func<int> _navigationGeneration;
    readonly Action<string, AgentOrigin, int> _openSessionIfCurrent;
    readonly Action<string> _closeFailedLaunch;
    readonly Action<string> _reopenSource;
    readonly CancellationToken _shutdown;
    readonly HandoffChoicesViewModel _choices;
    readonly CompositeDisposable _disposables = new();

    volatile AttachStatus? _attach;
    volatile bool _signedIn;
    volatile bool _serverConnected;
    bool _initialized;
    bool _disposed;
    bool _sourceChangedWhileLaunching;
    IReadOnlySet<string>? _features;
    HandoffContextDto? _context;
    string? _checkout;
    CancellationTokenSource? _evaluation;

    /// <param name="candidateRoots">Repositories this machine knows besides the session's own
    /// checkout: the launcher's and the daemon agents' roots.</param>
    /// <param name="navigationGeneration">Read before the launch call; an open under an older
    /// generation is refused (MainWindowViewModel.OpenSessionIfCurrent).</param>
    /// <param name="closeFailedLaunch">MainWindowViewModel.CloseFailedLaunch.</param>
    /// <param name="reopenSource">Opens a row by key; given <c>server:{sessionId}</c> after a
    /// delayed failure, whose composer then takes the draft back.</param>
    public HandoffComposerViewModel(
            RepoSessionDto session, IDaemonClientService daemon, ILaunchClient launch, IServerSessionsService sessions,
            ILocalControlOps ops, LocalCheckoutFinder finder, Func<CancellationToken, Task<IReadOnlyList<string>>> candidateRoots,
            HandoffDrafts drafts, IObservable<ServerLaneStatus> laneStatus, IAttachmentUploader uploader, IAgentDirectory? directory,
            Func<int> navigationGeneration, Action<string, AgentOrigin, int> openSessionIfCurrent,
            Action<string> closeFailedLaunch, Action<string> reopenSource,
            IObservable<IReadOnlyDictionary<string, IReadOnlyList<ModelChoice>>>? modelCatalog = null,
            CancellationToken shutdown = default) {
        _session = session;
        _daemon = daemon;
        _launch = launch;
        _sessions = sessions;
        _ops = ops;
        _finder = finder;
        _candidateRoots = candidateRoots;
        _drafts = drafts;
        _uploader = uploader;
        _directory = directory;
        _navigationGeneration = navigationGeneration;
        _openSessionIfCurrent = openSessionIfCurrent;
        _closeFailedLaunch = closeFailedLaunch;
        _reopenSource = reopenSource;
        _shutdown = shutdown;
        _choices = new HandoffChoicesViewModel(daemon, session.Vendor, modelCatalog).DisposeWith(_disposables);

        daemon.Status.Subscribe(s => _attach = s).DisposeWith(_disposables);
        var connected = laneStatus.Select(s => s.State == ServerLaneState.Connected);
        connected.Subscribe(c => _signedIn = _serverConnected = c).DisposeWith(_disposables);
        // An empty feature list read while disconnected says nothing about the server.
        connected
            .DistinctUntilChanged()
            .Skip(1)
            .Where(c => c)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(change => { if (_initialized && !IsLaunching) _ = ReevaluateAsync(CancellationToken.None); })
            .DisposeWith(_disposables);
        // HandoffDrafts publishes on the UI thread.
        drafts.FailedFor
            .Where(id => id == session.SessionId)
            .Subscribe(_ => TakeBackFailedDraft())
            .DisposeWith(_disposables);
        // A daemon that connects, restarts or updates under an open composer answers again.
        daemon.Status
            .Select(s => (s.State, Handoff: HasHandoff(s)))
            .DistinctUntilChanged()
            .Skip(1)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(change => { if (_initialized && !IsLaunching) _ = ReevaluateAsync(CancellationToken.None); })
            .DisposeWith(_disposables);

        // The upload gate Start re-runs, published on the UI thread for the attach button.
        var canAttach = connected
            .CombineLatest(daemon.Status.Select(s => s.Capabilities),
                (signedIn, caps) => HomeViewModel.CanAttachTo(signedIn, caps, [], null, daemon.DaemonName, remote: false))
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler);
        _canAttach = canAttach.ToProperty(this, x => x.CanAttach, initialValue: false).DisposeWith(_disposables);
        _attachHint = connected
            .CombineLatest(canAttach, HomeViewModel.AttachHintFor)
            .DistinctUntilChanged()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.AttachHint, initialValue: (string?)null)
            .DisposeWith(_disposables);

        var canStart = this.WhenAnyValue(x => x.ProbedStart, x => x.DisabledReason, x => x.IsLaunching,
            (start, reason, launching) => start is not null && reason is null && !launching);
        StartCommand = ReactiveCommand.CreateFromTask(StartAsync, canStart);
        // An unobserved command fault takes ReactiveUI's crash path, and a launch can fault after
        // Dispose, so the observer lives as long as the command rather than in _disposables.
        StartCommand.ThrownExceptions
            .Subscribe(ex => Console.Error.WriteLine($"kcap: hand-off start: {ex.Message}"));
    }

    public AttachmentTray Tray { get; } = new();

    readonly ObservableAsPropertyHelper<bool> _canAttach;
    public bool CanAttach => _canAttach.Value;
    readonly ObservableAsPropertyHelper<string?> _attachHint;
    public string? AttachHint => _attachHint.Value;
    public int FreeSlots => Tray.FreeSlots;
    string? _intakeNotice;

    /// The tray takes what it can; the rest becomes the bottom row's error until the next intake.
    public void Accept(IntakeResult result) {
        if (_intakeNotice is not null && Error == _intakeNotice) Error = null;
        _intakeNotice = null;
        var refused = new List<IntakeRefusal>(result.Refused);
        refused.AddRange(Tray.AddAll(result.Accepted));
        if (RefusalNotice.Render(refused) is not { } notice) return;
        _intakeNotice = notice;
        Error = notice;
    }

    /// The bottom row's line: what went wrong, else why Start is unavailable.
    public string? Notice => Error ?? DisabledReason;
    public ILaunchChoices Choices => _choices;
    public ReactiveCommand<Unit, Unit> StartCommand { get; }

    string _text = "";
    public string Text {
        get => _text;
        set => this.RaiseAndSetIfChanged(ref _text, value);
    }

    string _stripLabel = "";
    public string StripLabel {
        get => _stripLabel;
        private set => this.RaiseAndSetIfChanged(ref _stripLabel, value);
    }

    string? _completenessNote;
    public string? CompletenessNote {
        get => _completenessNote;
        private set {
            this.RaiseAndSetIfChanged(ref _completenessNote, value);
            this.RaisePropertyChanged(nameof(WorkMissingOrUnconfirmed));
        }
    }

    /// Only Complete may read as all there, and this slice never reaches it.
    public bool WorkMissingOrUnconfirmed => CompletenessNote is not null;

    string? _disabledReason;
    public string? DisabledReason {
        get => _disabledReason;
        private set {
            this.RaiseAndSetIfChanged(ref _disabledReason, value);
            this.RaisePropertyChanged(nameof(Notice));
        }
    }

    bool _isProbing;
    public bool IsProbing {
        get => _isProbing;
        private set => this.RaiseAndSetIfChanged(ref _isProbing, value);
    }

    string? _error;
    public string? Error {
        get => _error;
        private set {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(Notice));
        }
    }

    HandoffLaunchStart? _probedStart;
    /// The start the strip shows, which a launch sends unchanged.
    public HandoffLaunchStart? ProbedStart {
        get => _probedStart;
        private set => this.RaiseAndSetIfChanged(ref _probedStart, value);
    }

    bool _isLaunching;
    public bool IsLaunching {
        get => _isLaunching;
        private set => this.RaiseAndSetIfChanged(ref _isLaunching, value);
    }

    /// Takes back a draft a failed launch left for this session before anything is read.
    public Task InitializeAsync(CancellationToken ct) {
        if (_drafts.TakeFailed(_session.SessionId) is { } failed) Apply(failed);
        _initialized = true;
        return ReevaluateAsync(ct);
    }

    /// A later revision of the session's row: the composer keeps its text, tray and chips and checks
    /// again against the new source. The chips' vendor was only a seed, so it stays the user's.
    public void UpdateSource(RepoSessionDto session) {
        if (_disposed || session == _session) return;
        _session = session;
        _context = null;
        if (!_initialized) return;
        if (IsLaunching) { _sourceChangedWhileLaunching = true; return; }
        _ = ReevaluateAsync(CancellationToken.None);
    }

    internal Task PendingEvaluationForTesting { get; private set; } = Task.CompletedTask;

    Task ReevaluateAsync(CancellationToken ct) => PendingEvaluationForTesting = RunEvaluationAsync(ct);

    async Task RunEvaluationAsync(CancellationToken ct) {
        if (_disposed) return;
        _evaluation?.Cancel();
        _evaluation?.Dispose();
        var cts = _evaluation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown, ct);
        ProbedStart = null;
        StripLabel = "";
        CompletenessNote = null;
        DisabledReason = null;
        IsProbing = true;
        Evaluation result;
        try {
            result = await EvaluateAsync(cts.Token);
        } catch (OperationCanceledException) when (cts.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: hand-off check: {ex.Message}");
            result = new(ProbeFailed);
        }
        if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(cts, _evaluation)) return;
        IsProbing = result.Waiting;
        DisabledReason = result.Reason;
        _checkout = result.Checkout;
        StripLabel = result.Label ?? "";
        CompletenessNote = result.CompletenessNote;
        ProbedStart = result.Reason is null ? result.Start : null;
    }

    async Task<Evaluation> EvaluateAsync(CancellationToken ct) {
        // A pinned row carries no level until a listing holds it, so the context read decides.
        var accessUnknown = string.IsNullOrEmpty(_session.AccessLevel);
        if (!accessUnknown && !string.Equals(_session.AccessLevel, FullAccess, StringComparison.OrdinalIgnoreCase))
            return new(NeedsFullAccess);

        if (!_serverConnected) return new(ServerUnreachable);
        if (_features is not { Count: > 0 }) {
            // Null is no answer at all, which says nothing about the server's version.
            if (await _launch.GetLaunchFeaturesAsync(ct) is not { } read) return new(ServerUnreachable);
            _features = read;
        }
        if (!_features.Contains(HandoffWire.Capability)) return new(ServerTooOld);

        switch (_attach) {
            case null or { State: AttachState.Connecting }: return new(null, Waiting: true);
            case { State: AttachState.Unreachable }: return new(DaemonNotRunning);
            case { } status when !HasHandoff(status): return new(DaemonTooOld);
        }

        if (_context is null) {
            var read = await _sessions.ReadHandoffContextAsync(_session.SessionId, ct);
            if (read is not { Outcome: ServerReadOutcome.Ok, Context: { } fetched })
                return new(accessUnknown && read.Outcome == ServerReadOutcome.Unavailable ? NeedsFullAccess : ContextUnavailable);
            _context = fetched;
        }
        if (_context is not { RepoOwner: { Length: > 0 } owner, RepoName: { Length: > 0 } name } context)
            return new(NoRepository);

        var candidates = await Task.Run(() => _candidateRoots(ct), ct);
        if (await _finder.FindAsync(owner, name, _session.Cwd, candidates, ct) is not { } checkout)
            return new(NoCheckout(owner, name));

        var target = TargetFor(context, context.Branch ?? _session.Branch);
        var request = new HandoffProbeRequestDto(
            checkout, owner, name, target.Branch, target.PullRequestNumber, target.HeadIsFork, target.HeadOwner,
            target.HeadRepo, context.SessionCommits);
        HandoffProbeReplyDto? reply;
        try {
            reply = await Task.Run(() => _ops.ProbeHandoffAsync(request, ct), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            reply = null;
        }

        return reply switch {
            { Refusal: HandoffRefusals.RepoMismatch } => new(NoCheckout(owner, name)),
            { Refusal: HandoffRefusals.RepoNotAllowed } => new(NotAllowed(checkout)),
            { Refusal: { } } => new(InvalidTarget),
            { FetchError: { } detail } => new(OriginUnreachable(detail)),
            { Start: { } start, Completeness: var completeness, MissingCommits: var missing } =>
                new(null, start, checkout, StripLabelFor(start, target.Branch), CompletenessNoteFor(completeness, missing)),
            _ => new(ProbeFailed),
        };
    }

    async Task StartAsync() {
        // Execute() does not gate on CanExecute.
        if (ProbedStart is not { } start || DisabledReason is not null || IsLaunching || _checkout is not { } checkout) return;
        IsLaunching = true;
        try {
            var draft = _choices.Capture(Text, Tray.Snapshot());
            IReadOnlyList<string>? attachmentIds = null;
            if (draft.Attachments.Count > 0) {
                if (!HomeViewModel.CanAttachTo(_signedIn, _attach?.Capabilities, [], null, _daemon.DaemonName, remote: false)) {
                    Error = HomeViewModel.AttachHintFor(_signedIn, false);
                    return;
                }
                UploadOutcome upload;
                try { upload = await _uploader.UploadAsync(draft.Attachments, _shutdown); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                var uploadError = upload.Kind switch {
                    UploadKind.Uploaded => null,
                    UploadKind.Unauthorized => HomeViewModel.SignInToAttach,
                    _ => upload.Reason ?? "the upload failed",
                };
                if (_disposed) {
                    _drafts.HoldFailed(_session.SessionId, draft with { Error = uploadError });
                    return;
                }
                if (uploadError is not null) { Error = uploadError; return; }
                attachmentIds = upload.Ids;
            }

            var request = new LaunchRequest(
                _daemon.DaemonName, checkout, draft.Vendor, draft.Text, draft.Model, draft.Effort,
                HomeViewModel.PermissionModeFor(draft.Vendor, draft.PermissionMode!), attachmentIds,
                HomeViewModel.CodexPostureFor(draft.Vendor, draft.CodexPosture!.Sandbox, draft.CodexPosture.Approval),
                Handoff: start with { SourceSessionId = _session.SessionId });
            // Captured before the call: a navigation while it is in flight must keep the launch from opening.
            var generation = _navigationGeneration();
            var outcome = await _launch.StartAsync(request, _shutdown);
            // A composer torn down during the call keeps nothing: the source's next composer takes
            // whatever comes back.
            if (!outcome.Started) {
                var refusal = outcome.Unauthorized ? SignInToHandOff : HandoffFailureText.For(outcome.Error);
                if (_disposed) {
                    _drafts.HoldFailed(_session.SessionId, draft with { Error = refusal });
                    return;
                }
                Error = refusal;
                await ReevaluateAsync(CancellationToken.None);
                return;
            }
            if (HomeViewModel.NormalizeAgentId(outcome.AgentId) is not { } agentId) {
                if (!_disposed) Error = HomeViewModel.UnusableIdMessage;
                return;
            }

            var sourceKey = $"server:{_session.SessionId}";
            var closeFailedLaunch = _closeFailedLaunch;
            var reopenSource = _reopenSource;
            var navigationGeneration = _navigationGeneration;
            // The generation the placeholder's open left; null when the open was refused.
            int? openedUnder = null;
            var hold = _drafts.Hold(_session.SessionId, agentId, draft, () => {
                // Read before the close, which is itself a navigation. Only a user still on the
                // placeholder is taken back; anyone else finds the draft on the source's next open.
                var stillThere = openedUnder is { } opened && navigationGeneration() == opened;
                closeFailedLaunch(agentId);
                // A close that left the generation alone closed nothing: the workspace holds another session.
                if (stillThere && navigationGeneration() != openedUnder) reopenSource(sourceKey);
            });
            if (hold == HandoffHold.AlreadyFailed) {
                if (_disposed) return;
                if (_drafts.TakeFailed(_session.SessionId) is { } failed) Apply(failed);
                await ReevaluateAsync(CancellationToken.None);
                return;
            }

            if (!_disposed) {
                Error = null;
                Text = "";
                Tray.Clear();
            }
            if (hold == HandoffHold.Pending)
                _directory?.AddPlaceholder(agentId, draft.Vendor, checkout, AgentRow.TitleFromPrompt(draft.Text), draft.Model);
            var opens = _navigationGeneration() == generation;
            _openSessionIfCurrent(agentId, AgentOrigin.Local, generation);
            if (opens) openedUnder = _navigationGeneration();
        } finally {
            IsLaunching = false;
            if (_sourceChangedWhileLaunching && !_disposed) {
                _sourceChangedWhileLaunching = false;
                _ = ReevaluateAsync(CancellationToken.None);
            }
        }
    }

    void TakeBackFailedDraft() {
        if (_disposed || IsLaunching || _drafts.TakeFailed(_session.SessionId) is not { } failed) return;
        Apply(failed);
        _ = ReevaluateAsync(CancellationToken.None);
    }

    /// Text typed since the launch stays; the sent text comes back only into an empty box.
    void Apply(HandoffDraft draft) {
        if (Text.Length == 0) Text = draft.Text;
        if (Tray.Count == 0) Tray.Restore(draft.Attachments);
        else Tray.AddAll([.. draft.Attachments.Where(f => Tray.Items.All(staged => staged.Id != f.Id))]);
        _choices.Restore(draft);
        Error = draft.Error;
    }

    /// The listing row a session-only workspace was built from, as the composer reads it.
    public static RepoSessionDto SourceOf(AgentRow row) => new() {
        SessionId = row.SessionId ?? row.Id, Status = row.IsEndedSession ? "ended" : "active",
        AccessLevel = row.AccessLevel ?? "", Vendor = row.Vendor, Title = row.Title, Cwd = row.RepoPath, Branch = row.Branch,
        StartedAt = row.CreatedAt, LastActivityAt = row.LastActivityAt ?? row.CreatedAt, EndedAt = row.EndedAt,
    };

    internal bool IsDisposedForTesting => _disposed;

    static bool HasHandoff(AttachStatus status) =>
        status.State == AttachState.Connected && status.Capabilities?.Contains(HandoffWire.Capability) == true;

    /// A verified fork's head, or a head in the session's own repository; anything else, including a
    /// head the server could not classify, falls back to the recorded branch.
    internal static Target TargetFor(HandoffContextDto context, string? recordedBranch) {
        if (context.PullRequest is { HeadRef: { Length: > 0 } headRef } pr) {
            if (pr is { HeadIsFork: true, HeadOwner: { Length: > 0 } forkOwner, HeadRepo: { Length: > 0 } forkRepo })
                return new(headRef, pr.Number, true, forkOwner, forkRepo);
            if (pr.HeadIsFork == false && SameRepository(pr, context))
                return new(headRef, pr.Number, false, null, null);
        }
        return new(recordedBranch, null, false, null, null);
    }

    static bool SameRepository(HandoffPullRequestDto pr, HandoffContextDto context) =>
        (pr.HeadOwner is null || string.Equals(pr.HeadOwner, context.RepoOwner, StringComparison.OrdinalIgnoreCase))
     && (pr.HeadRepo is null || string.Equals(pr.HeadRepo, context.RepoName, StringComparison.OrdinalIgnoreCase));

    /// requestedBranch is the target the probe asked for; a start on origin's default branch names it
    /// as the one origin lacks.
    internal static string StripLabelFor(HandoffLaunchStart start, string? requestedBranch) {
        var sha7 = start.StartSha.Length > 7 ? start.StartSha[..7] : start.StartSha;
        var branch = start.Branch ?? sha7;
        if (start.Checkout != nameof(HandoffCheckout.Detached)) return $"{StripPrefix} on {branch}";
        var detached = $"{StripPrefix}, detached at {sha7} of ";
        return start.DetachReason switch {
            nameof(HandoffDetachReason.CheckedOutElsewhere) => $"{detached}{branch} (checked out in another worktree)",
            nameof(HandoffDetachReason.LocalBranchDiverged) => $"{detached}{branch} (your local branch has diverged)",
            nameof(HandoffDetachReason.ForkHead) => $"{detached}{start.HeadOwner}:{branch} (a fork)",
            nameof(HandoffDetachReason.BranchAbsent) when requestedBranch is { Length: > 0 } =>
                $"{detached}{branch} ({requestedBranch} isn't on origin)",
            _ => detached + branch,
        };
    }

    internal static string? CompletenessNoteFor(string? completeness, IReadOnlyList<string>? missingCommits) => completeness switch {
        nameof(HandoffCompleteness.PartlyMissing) => $"Part of the session's work is missing: {missingCommits?.Count ?? 0} commits",
        nameof(HandoffCompleteness.Unconfirmed) => "The session may have left work that isn't pushed",
        _ => null,
    };

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _evaluation?.Cancel();
        _evaluation?.Dispose();
        _evaluation = null;
        _disposables.Dispose();
        StartCommand.Dispose();
    }
}
