using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum ChatTabPhase { Waiting, Reading, Missing, Unavailable, Failed }

/// The Chat tab: the session's transcript, drained from a feed into chat rows, plus the composer
/// that sends through whatever channel the session offers. Ctor-scoped; TeardownAsync is the one exit.
///
/// Feed identity is part of the read generation: a distinct feed key clears the rows and installs a
/// fresh feed in one UI-thread step, and any read still in flight for the old source completes
/// under a stale generation and is discarded.
public sealed class ChatTabViewModel : ReactiveObject {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// First retry gap after a failed withdraw; each further one doubles it.
    internal static readonly TimeSpan WithdrawRetryDelay = TimeSpan.FromSeconds(2);
    internal const int MaxWithdrawRetries = 3;

    readonly ChatInput _input;
    readonly Func<string, IChatTranscriptFeed>? _openFeed;
    readonly string? _unavailableNote;
    readonly string? _missingNote;
    /// The last read's refusal, cleared by the next read that is not one. It stands in for the
    /// rows while there are none and sits under them otherwise.
    string? _failureNote;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly IPermissionService _permissions;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    // Read once: the source is disposed at teardown, and a retry waking after that still needs a
    // token it can ask without an ObjectDisposedException.
    readonly CancellationToken _lifetimeToken;
    readonly AvaloniaList<ChatItemViewModel> _items = new();
    readonly AvaloniaList<QueuedChatMessage> _queuedMessages = new();
    QueuedChatMessage? _lastSent;
    readonly Dictionary<string, ToolCallItem> _pendingTools = new(StringComparer.Ordinal);
    // Every tool id with a result, not only the running ones: a replayed request can arrive after
    // the transcript's initial load, and then only this set can tell that its tool is done.
    readonly HashSet<string> _settledTools = new(StringComparer.Ordinal);
    readonly HashSet<string> _withdrawing = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _withdrawFailures = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> _loggedFailures = new(StringComparer.Ordinal);
    readonly Dictionary<string, PendingPermissionRequest> _requests = new(StringComparer.Ordinal);
    readonly HashSet<ToolCallItem> _marked = new(ReferenceEqualityComparer.Instance);
    ToolGroupItem? _openGroup;

    /// The feed and the generation it belongs to, taken as one reference: reading them separately
    /// lets a switch land between them and tag a read of the old source with the new generation,
    /// which Apply's guard would then wave through onto the freshly cleared list.
    sealed class FeedLease(IChatTranscriptFeed feed, int generation) {
        public IChatTranscriptFeed Feed { get; } = feed;
        public int Generation { get; } = generation;
    }

    int _generation;
    int _inputGeneration;
    int _readInFlight;
    string? _feedKey;
    /// The session the listed foreign rows belong to.
    string? _queueKey;
    string? _root;
    volatile FeedLease? _lease;
    ITimer? _timer;
    volatile Task? _pendingRead;
    readonly BehaviorSubject<string?> _rootSubject = new(null);

    long? CurrentOffset => _lease?.Feed.CurrentOffset;

    public IAvaloniaReadOnlyList<ChatItemViewModel> Items => _items;
    public IAvaloniaReadOnlyList<QueuedChatMessage> QueuedMessages => _queuedMessages;
    public bool HasQueuedMessages => _queuedMessages.Count > 0;
    public string QueueSummary {
        get {
            var unconfirmed = _queuedMessages.Count(q => q.IsUnconfirmed);
            var queued = _queuedMessages.Count - unconfirmed;
            return unconfirmed == 0 ? $"{MessageCount(queued)} queued"
                : queued == 0 ? $"{MessageCount(unconfirmed)} unconfirmed"
                : $"{MessageCount(queued)} queued · {unconfirmed} unconfirmed";
        }
    }

    static string MessageCount(int count) => $"{count} message{(count == 1 ? "" : "s")}";

    void RefreshQueue() {
        this.RaisePropertyChanged(nameof(HasQueuedMessages));
        this.RaisePropertyChanged(nameof(QueueSummary));
    }

    public PendingCardsViewModel Cards { get; }
    public ReadOnlyObservableCollection<PendingCardViewModel> PendingCards => Cards.PendingCards;
    public IObservable<string?> Root => _rootSubject;

    readonly ObservableAsPropertyHelper<bool> _hasPendingCards;
    public bool HasPendingCards => _hasPendingCards.Value;

    ChatTabPhase _phase;
    public ChatTabPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            RefreshActivityNote();
        }
    }

    public string PhaseNote => Phase switch {
        ChatTabPhase.Waiting     => "Waiting for the transcript…",
        ChatTabPhase.Missing     => _missingNote ?? "The transcript file is missing",
        ChatTabPhase.Unavailable => _unavailableNote ?? "No chat view for this harness",
        ChatTabPhase.Failed      => FailureNote(_failureNote),
        _                        => "",
    };

    static string FailureNote(string? reason) =>
        reason is null ? "The transcript could not be read" : $"The transcript could not be read: {reason}";

    string _composerText = "";
    int _composerEdits;
    public string ComposerText {
        get => _composerText;
        set {
            if (string.Equals(_composerText, value, StringComparison.Ordinal)) return;
            _composerEdits++;
            this.RaiseAndSetIfChanged(ref _composerText, value);
        }
    }

    readonly ComposerHistory _history = new();
    int _recallEdits = -1;
    /// Replaces the composer text with the next older sent prompt; false when nothing changed.
    public bool RecallOlder() => Recall(_history.Older(Recallable()));
    /// Replaces the composer text with the next newer sent prompt, or the draft past the newest.
    public bool RecallNewer() => Recall(_history.Newer(Recallable()));

    /// Text equality cannot prove a recall was left alone — an edit undone by hand lands on the
    /// same string — so the edit count decides, as it does for the sent draft.
    string Recallable() {
        if (_composerEdits != _recallEdits) _history.EndNavigation();
        return ComposerText;
    }

    bool Recall(string? text) {
        if (text is null) return false;
        ComposerText = text;
        _recallEdits = _composerEdits;
        return true;
    }

    public ReactiveCommand<Unit, Unit> SendCommand { get; }
    public ReactiveCommand<Unit, Unit> InterruptCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }

    readonly ObservableAsPropertyHelper<string> _composerHint;
    public string ComposerHint => _composerHint.Value;

    readonly ObservableAsPropertyHelper<bool> _showsComposer;
    /// Input + Send stay in the tree only while messaging is still possible. An ended session
    /// hides them and leaves the hint — a greyed empty box is the wrong affordance.
    public bool ShowsComposer => _showsComposer.Value;

    string _vendor = "";
    IReadOnlyList<HarnessOption> _options = HostedHarnessCatalog.Build(null);

    string _vendorLabel = "";
    public string VendorLabel { get => _vendorLabel; private set => this.RaiseAndSetIfChanged(ref _vendorLabel, value); }

    string _modelLabel = "default";
    public string ModelLabel { get => _modelLabel; private set => this.RaiseAndSetIfChanged(ref _modelLabel, value); }

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    string _activityNote = "";
    /// Live elapsed time throughout a busy turn, including while output is streaming.
    public string ActivityNote { get => _activityNote; private set => this.RaiseAndSetIfChanged(ref _activityNote, value); }

    string _status = "";
    bool? _awaitingInput;
    long? _workingSince;
    TimeSpan _worked;

    void RefreshActivityNote() {
        var inTurn = _status == "Running" && _awaitingInput == false;
        var working = inTurn && !HasPendingCards;
        if (!inTurn) {
            _workingSince = null;
            _worked = TimeSpan.Zero;
        } else if (working) {
            _workingSince ??= _time.GetTimestamp();
        } else if (_workingSince is { } pausedAt) {
            _worked += _time.GetElapsedTime(pausedAt);
            _workingSince = null;
        }
        ActivityNote = _failureNote is { } failure && Phase != ChatTabPhase.Failed
            ? FailureNote(failure)
            : _status == "Starting"
                ? VendorLabel.Length > 0 ? $"Starting {VendorLabel}…" : "Starting…"
                : working && _workingSince is { } since
                    ? WorkingNote(_worked + _time.GetElapsedTime(since)) : "";
    }

    static string WorkingNote(TimeSpan elapsed) {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return $"Working for {seconds / 60}m {seconds % 60}s";
    }

    bool _isReadOnlyParticipant;
    /// True for a flow participant (any kind other than "agent"): the view swaps the composer
    /// input for the read-only banner, and the send gate refuses regardless of the terminal
    /// attach — the kind, not the handshake, is what makes the session unmessageable.
    public bool IsReadOnlyParticipant {
        get => _isReadOnlyParticipant;
        private set => this.RaiseAndSetIfChanged(ref _isReadOnlyParticipant, value);
    }

    string _readOnlyNotice = "";
    public string ReadOnlyNotice { get => _readOnlyNotice; private set => this.RaiseAndSetIfChanged(ref _readOnlyNotice, value); }

    /// Why this session cannot be messaged, or "" for an ordinary agent. Mirrors the wording of
    /// the daemon's attach-time ProtectionReason so the chat banner and the terminal banner name
    /// the same block identically; an unrecognised kind fails safe as protected, like
    /// AgentActionService.IsProtectedKind.
    internal static string ParticipantNotice(AgentStatusDto dto) {
        if (!AgentActionService.IsProtectedKind(dto.Kind)) return "";
        var role = string.IsNullOrEmpty(dto.FlowRole) ? "" : $", role {dto.FlowRole}";
        var flow = string.IsNullOrEmpty(dto.FlowRunId) ? "" : $" (flow {dto.FlowRunId}{role})";
        return $"{dto.Kind} agent{flow}";
    }

    /// The daemon cache as session facts: an add or update is the dto, a removal keeps the last
    /// facts under a Completed status. Identical revisions are not republished.
    static IObservable<ChatSessionInfo> LocalSession(string agentId, IDaemonClientService daemon) =>
        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Scan((ChatSessionInfo?)null, (last, changes) => {
                var info = last;
                foreach (var change in changes) {
                    if (change.Key != agentId) continue;
                    if (change.Reason == ChangeReason.Remove)
                        info = (info ?? ChatSessionInfo.Gone) with { Status = "Completed", StatusLabel = "Completed", Ended = true };
                    else if (change.Reason is ChangeReason.Add or ChangeReason.Update)
                        info = ChatSessionInfo.FromLocal(change.Current, ended: false);
                }
                return info;
            })
            .Where(info => info is not null)
            .Select(info => info!)
            .DistinctUntilChanged();

    /// The dedup is per pane, not per feed: a projection failure that survives a transcript switch
    /// would otherwise be logged again by every feed the pane opens.
    static Func<string, IChatTranscriptFeed> LocalFeed(string agentId, IChatTranscriptProjection projection, TimeProvider time) {
        var logged = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        return path => new LocalTranscriptFeed(path, projection, agentId, time,
            reason => { if (logged.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: chat transcript: {reason}"); });
    }

    IBrush _statusDot = SessionStatusDots.For("");
    public IBrush StatusDot { get => _statusDot; private set => this.RaiseAndSetIfChanged(ref _statusDot, value); }

    /// Test-only seam: the read in flight, or the last one started. A switch that loses the
    /// in-flight CAS starts no read of its own, so this still points at the previous file's read —
    /// await that, advance one tick, then await again to see the new path's first rows.
    internal Task? PendingReadForTesting => _pendingRead;

    /// Test-only seam: withdraws sent and not yet acknowledged, or failed.
    internal int WithdrawsInFlightForTesting => _withdrawing.Count;

    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, ChatInput input,
            IChatTranscriptProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions,
            string? unavailableNote = null, IObservable<string?>? sessionId = null,
            IObservable<bool>? localDaemonOnAppServer = null)
        : this(agentId, AgentOrigin.Local, LocalSession(agentId, daemon), daemon.Snapshots.Select(s => s.Daemon.SupportedVendors),
               input, projection is null ? null : LocalFeed(agentId, projection, time), opener, time, permissions,
               unavailableNote, null, sessionId, localDaemonOnAppServer) { }

    public ChatTabViewModel(
            string agentId, AgentOrigin origin, IObservable<ChatSessionInfo> session, IObservable<string[]?> supportedVendors,
            ChatInput input, Func<string, IChatTranscriptFeed>? openFeed, IUrlOpener opener, TimeProvider time,
            IPermissionService permissions, string? unavailableNote = null, string? missingNote = null,
            IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null,
            IObservable<IReadOnlyList<QueuedInputItem>>? serverQueue = null) {
        _input = input;
        _disposables.Add(input);
        _openFeed = openFeed;
        _unavailableNote = unavailableNote;
        _missingNote = missingNote;
        _opener = opener;
        _time = time;
        _permissions = permissions;
        _lifetimeToken = _lifetime.Token;
        _phase = openFeed is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        Cards = new PendingCardsViewModel(
            agentId, origin, sessionId ?? Observable.Return<string?>(null), permissions, _rootSubject,
            localDaemonOnAppServer);
        _hasPendingCards = Cards.WhenAnyValue(c => c.HasPendingCards)
            .ToProperty(this, x => x.HasPendingCards, initialValue: Cards.HasPendingCards)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.HasPendingCards)
            .Subscribe(_ => RefreshActivityNote())
            .DisposeWith(_disposables);

        Cards.Requests
            .Subscribe(changes => {
                foreach (var change in changes) {
                    switch (change.Reason) {
                        case ChangeReason.Add or ChangeReason.Update: _requests[change.Key] = change.Current; break;
                        case ChangeReason.Remove:
                            _requests.Remove(change.Key);
                            _withdrawing.Remove(change.Key);
                            _withdrawFailures.Remove(change.Key);
                            break;
                    }
                }
                Reconcile();
            })
            .DisposeWith(_disposables);

        session
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSession)
            .DisposeWith(_disposables);

        _timer = time.CreateTimer(_ => OnTick(), null, PollInterval, PollInterval);

        supportedVendors
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(vendors => {
                _options = HostedHarnessCatalog.Build(vendors);
                VendorLabel = HostedHarnessCatalog.LabelFor(_options, _vendor);
                RefreshActivityNote();
            })
            .DisposeWith(_disposables);

        // The banner carries the read-only explanation for a flow participant, so the hint
        // goes blank there instead of offering a reply that can never be sent.
        _composerHint = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.Hint),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                (hint, readOnly) => readOnly ? "" : hint)
            .ToProperty(this, x => x.ComposerHint, initialValue: IsReadOnlyParticipant ? "" : _input.Hint)
            .DisposeWith(_disposables);

        _showsComposer = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.Availability),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                (availability, readOnly) => !readOnly && availability != SendAvailability.Ended)
            .ToProperty(this, x => x.ShowsComposer,
                initialValue: !IsReadOnlyParticipant && _input.Availability != SendAvailability.Ended)
            .DisposeWith(_disposables);

        var canSend = Observable.CombineLatest(
            this.WhenAnyValue(x => x.ComposerText),
            _input.WhenAnyValue(i => i.CanAcceptText),
            this.WhenAnyValue(x => x.IsReadOnlyParticipant),
            (text, can, readOnly) => can && !readOnly && !string.IsNullOrWhiteSpace(text));
        // The composer keeps whatever the user typed while the channel was deciding: only the
        // snapshot that was actually sent is cleared, and only once the channel commits it. The
        // edit count is what the text alone cannot say — an edit that lands back on the sent text
        // is still the user's own draft, not the snapshot.
        SendCommand = ReactiveCommand.CreateFromTask(async () => {
            var snapshot = ComposerText;
            var edits = _composerEdits;
            var queued = new QueuedChatMessage(snapshot, edits, _inputGeneration, CurrentOffset);
            _lastSent = queued;
            _queuedMessages.Add(queued);
            RefreshQueue();
            ChatSendOutcome outcome;
            try { outcome = await _input.SendAsync(snapshot, _lifetimeToken); }
            catch (OperationCanceledException) { outcome = ChatSendOutcome.Unconfirmed; }
            catch (Exception ex) {
                LogOnce($"send: {ex.Message}");
                outcome = ChatSendOutcome.Unconfirmed;
            }
            if (_lifetimeToken.IsCancellationRequested) return;
            if (outcome != ChatSendOutcome.Rejected) _history.Record(snapshot);
            if (outcome == ChatSendOutcome.Rejected || (outcome == ChatSendOutcome.Accepted && _openFeed is null))
                _queuedMessages.Remove(queued);
            else if (outcome == ChatSendOutcome.Unconfirmed)
                queued.MarkUnconfirmed();
            RefreshQueue();
            if (outcome == ChatSendOutcome.Accepted) ClearSentDraft(queued);
            if (queued.Acknowledged) ConfirmDelivery(queued);
        }, canSend);
        _disposables.Add(SendCommand);

        var canInterrupt = Observable.CombineLatest(
            _input.WhenAnyValue(i => i.CanInterrupt),
            this.WhenAnyValue(x => x.IsReadOnlyParticipant),
            (can, readOnly) => can && !readOnly);
        InterruptCommand = ReactiveCommand.CreateFromTask(() => _input.InterruptAsync(_lifetimeToken), canInterrupt);
        _disposables.Add(InterruptCommand);

        OpenLinkCommand = ReactiveCommand.Create<string>(url => LinkPolicy.Open(_opener, url));
        _disposables.Add(OpenLinkCommand);

        serverQueue?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(ApplyServerQueue).DisposeWith(_disposables);
    }

    /// The server's queue for this session, whoever queued it. An own send it lists is queued
    /// for certain; a prompt of nobody's here is another client's; a prompt it no longer lists
    /// was delivered or withdrawn. Own sends still leave with the transcript's echo.
    void ApplyServerQueue(IReadOnlyList<QueuedInputItem> items) {
        var listed = new HashSet<Guid>();
        foreach (var item in items) {
            // An item with no id is unkeyed, not identified as nobody's: keying it would collide
            // with every other such item and could disqualify an own send from its real match.
            if (item.DispatchId == Guid.Empty || !listed.Add(item.DispatchId)) continue;
            if (_queuedMessages.Any(q => q.DispatchId == item.DispatchId)) continue;
            var own = _queuedMessages.FirstOrDefault(q => q.DispatchId is null && !q.Acknowledged && q.MatchesText(item.Text));
            if (own is not null) own.MarkQueued(item.DispatchId);
            else _queuedMessages.Add(QueuedChatMessage.FromServer(item));
        }
        foreach (var gone in _queuedMessages.Where(q => q.IsForeign && q.DispatchId is { } id && !listed.Contains(id)).ToList())
            _queuedMessages.Remove(gone);
        RefreshQueue();
    }

    void OnSession(ChatSessionInfo info) {
        ReadOnlyNotice = info.ReadOnlyNotice;
        IsReadOnlyParticipant = info.ReadOnlyNotice.Length > 0;
        _vendor = info.Vendor;
        _root = info.Root;
        _rootSubject.OnNext(info.Root);
        VendorLabel = HostedHarnessCatalog.LabelFor(_options, info.Vendor);
        ModelLabel = HostedHarnessCatalog.ModelLabelFor(info.Vendor, info.Model ?? "");
        StatusText = info.StatusLabel;
        StatusDot = SessionStatusDots.For(info.Status);
        _status = info.Status;
        if (info.Ended)
            foreach (var queued in _queuedMessages.Where(q => !q.IsForeign)) queued.MarkUnconfirmed();
        _awaitingInput = info.AwaitingInput;
        // A foreign row is the server's answer for one session. Moving to another — or to none,
        // where no snapshot can ever arrive to retire it — leaves nothing to keep it honest.
        if (info.FeedKey != _queueKey) {
            _queueKey = info.FeedKey;
            foreach (var foreign in _queuedMessages.Where(q => q.IsForeign).ToList()) _queuedMessages.Remove(foreign);
            RefreshQueue();
        }
        if (_openFeed is { } open && info.FeedKey is { } key && key != _feedKey) SwitchFeed(key, open);
        RefreshActivityNote();
        RefreshQueue();
    }

    void SwitchFeed(string key, Func<string, IChatTranscriptFeed> open) {
        _items.Clear();
        _pendingTools.Clear();
        _settledTools.Clear();
        _openGroup = null;
        _marked.Clear();
        _feedKey = key;
        var previous = _lease;
        _lease = new FeedLease(open(key), Interlocked.Increment(ref _generation));
        previous?.Feed.Dispose();
        RebaseQueuedMessages(CurrentOffset);
        _failureNote = null;
        var wasWaiting = _phase == ChatTabPhase.Waiting;
        Phase = ChatTabPhase.Waiting;
        // The rows are gone, so the view has to re-read what stands in for them even when the phase
        // is unchanged — and only then, since the setter itself raises the note on a real change.
        if (wasWaiting) this.RaisePropertyChanged(nameof(PhaseNote));
        OnTick();
    }

    void RebaseQueuedMessages(long? offset) {
        _inputGeneration++;
        foreach (var queued in _queuedMessages.Where(q => !q.IsForeign)) queued.Rebase(_inputGeneration, offset);
        RefreshQueue();
    }

    void ClearSentDraft(QueuedChatMessage queued) {
        if (_composerEdits == queued.ComposerEdits && ComposerText == queued.Text) ComposerText = "";
    }

    void ConfirmDelivery(QueuedChatMessage queued) {
        if (ReferenceEquals(queued, _lastSent)) _input.ConfirmLastSend();
        ClearSentDraft(queued);
    }

    void OnTick() {
        if (!Dispatcher.UIThread.CheckAccess()) {
            Dispatcher.UIThread.Post(OnTick);
            return;
        }
        if (_lifetimeToken.IsCancellationRequested) return;
        RefreshActivityNote();
        if (_lease is not { } lease) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        _pendingRead = ReadAndApplyAsync(lease);
    }

    async Task ReadAndApplyAsync(FeedLease lease) {
        try {
            var read = await Task.Run(lease.Feed.ReadAppended).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Apply(lease.Generation, read));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }

    void Apply(int generation, FeedRead read) {
        if (generation != Volatile.Read(ref _generation)) return;

        switch (read.Status) {
            case FeedStatus.Missing:
                Phase = ChatTabPhase.Missing;
                return;
            case FeedStatus.Failed:
                var reason = read.Failure ?? "read failed";
                LogOnce(reason);
                var changed = !string.Equals(_failureNote, reason, StringComparison.Ordinal);
                _failureNote = reason;
                // No rows on screen: the reason stands in for them. Rows already shown stay, with
                // the reason beneath them.
                if (_items.Count == 0) Phase = ChatTabPhase.Failed;
                if (changed) {
                    this.RaisePropertyChanged(nameof(PhaseNote));
                    RefreshActivityNote();
                }
                return;
            case FeedStatus.Reset:
                // Skip everything the new source replays: it may be history, not receipts. The feed
                // names where that history ends — anything past it arrived after and can acknowledge.
                RebaseQueuedMessages(read.SnapshotOffset ?? CurrentOffset ?? 0);
                _items.Clear();
                _pendingTools.Clear();
                _settledTools.Clear();
                _openGroup = null;
                _marked.Clear();
                break;
        }

        // Any read that is not a refusal clears one: how long a refusal lasts is the feed's to
        // say, and a feed whose refusal stands keeps answering with it.
        if (_failureNote is not null) {
            _failureNote = null;
            RefreshActivityNote();
        }
        Phase = ChatTabPhase.Reading;
        // A send made before the transcript existed has no safe baseline. Its first successful
        // read establishes one; that initial history cannot acknowledge the send.
        foreach (var queued in _queuedMessages.Where(q => !q.HasBaseline && !q.IsForeign))
            queued.Rebase(_inputGeneration, read.SnapshotOffset ?? CurrentOffset ?? 0);
        RefreshQueue();
        if (read.Lines.Count == 0) {
            RefreshActivityNote();
            return;
        }

        var fresh = new List<ChatItemViewModel>();
        foreach (var (projected, offset) in read.Lines) {
            foreach (var text in projected.SubmittedInputs) {
                var acknowledged = _queuedMessages.FirstOrDefault(q => q.Matches(text, _inputGeneration, offset));
                if (acknowledged is null) continue;
                acknowledged.Acknowledged = true;
                _queuedMessages.Remove(acknowledged);
                ConfirmDelivery(acknowledged);
            }
            foreach (var e in projected.Envelopes) {
                switch (e.Kind) {
                    case AcpEventKind.UserMessage:
                        _openGroup = null;
                        fresh.Add(new UserTurnItem(e.Text ?? ""));
                        break;
                    case AcpEventKind.AssistantText:
                        _openGroup = null;
                        fresh.Add(new AssistantTextItem(e.Text ?? ""));
                        break;
                    case AcpEventKind.SystemNote:
                        _openGroup = null;
                        fresh.Add(new SystemNoteItem(e.Text ?? ""));
                        break;
                    case AcpEventKind.ToolCall: {
                        var name = e.ToolName ?? "tool";
                        var item = new ToolCallItem(name, ToolDetail.From(e.ToolInputJson, _root), ToolSummary.Categorize(name, e.ToolInputJson));
                        if (e.ToolCallId is { } id) _pendingTools[id] = item;
                        if (_openGroup is null) {
                            _openGroup = new ToolGroupItem();
                            fresh.Add(_openGroup);
                        }
                        _openGroup.Add(item);
                        break;
                    }
                    case AcpEventKind.ToolResult:
                        if (e.ToolCallId is not { } resultId) break;
                        _settledTools.Add(resultId);
                        if (_pendingTools.Remove(resultId, out var call))
                            call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                        break;
                }
            }
        }
        if (fresh.Count > 0) _items.AddRange(fresh);
        RefreshQueue();
        Reconcile();
        RefreshActivityNote();
    }

    /// A row is marked iff some pending request targets it: by tool-use id when the request has
    /// one, else the sole running call. Recomputed whole on every change to either set and diffed
    /// against the last marks, because a settled call has already left _pendingTools by the time
    /// its outcome flips, so the running set alone could never reach it to clear it.
    void Reconcile() {
        var targets = new HashSet<ToolCallItem>(ReferenceEqualityComparer.Instance);
        var sole = _pendingTools.Count == 1 ? _pendingTools.Values.First() : null;
        foreach (var request in _requests.Values) {
            if (request.ToolUseId is { } id) {
                if (_pendingTools.TryGetValue(id, out var call)) targets.Add(call);
            } else if (sole is not null) {
                targets.Add(sole);
            }
        }
        foreach (var call in _marked) if (!targets.Contains(call)) call.IsAwaitingPermission = false;
        foreach (var call in targets) call.IsAwaitingPermission = true;
        _marked.Clear();
        _marked.UnionWith(targets);
        WithdrawSettled();
    }

    /// A pending request whose tool already has a result was answered where the daemon cannot see
    /// (the vendor's own terminal prompt), so this tab is the one party that can retire it. Sent
    /// once per request; a failed send reopens it and retries on a bounded backoff.
    void WithdrawSettled() {
        if (_lifetimeToken.IsCancellationRequested) return;
        foreach (var request in _requests.Values) {
            if (request.ToolUseId is not { } id || !_settledTools.Contains(id) || !_withdrawing.Add(request.Key)) continue;
            _ = WithdrawAsync(request);
        }
    }

    async Task WithdrawAsync(PendingPermissionRequest request) {
        var id = request.Key;
        try {
            var outcome = await _permissions.WithdrawAsync(request, _lifetimeToken);
            if (outcome.Kind != PermissionResolveKind.TransportFailure) { _withdrawFailures.Remove(id); return; }
        } catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            LogOnce($"withdraw: {ex.Message}");
        }

        // The resolve rides a one-shot socket that can fail while the subscription stays healthy,
        // and an empty poll never reconciles, so the retry has to be this method's own. Past the
        // cap, the next resubscribe or permission/transcript change is what tries again.
        _withdrawing.Remove(id);
        var failures = _withdrawFailures.GetValueOrDefault(id) + 1;
        _withdrawFailures[id] = failures;
        if (failures > MaxWithdrawRetries) return;
        try { await Task.Delay(WithdrawRetryDelay * (1 << (failures - 1)), _time, _lifetimeToken); }
        catch (OperationCanceledException) { return; }
        WithdrawSettled();
    }

    void LogOnce(string reason) {
        if (_loggedFailures.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: chat transcript: {reason}");
    }

    public Task TeardownAsync() {
        Interlocked.Increment(ref _generation);
        var lease = _lease;
        _lease = null;
        lease?.Feed.Dispose();
        _timer?.Dispose();
        _timer = null;
        // Ahead of the disposables: the input is one of them, and an in-flight send has to see
        // the cancellation before the channel it is sending through goes away.
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _disposables.Dispose();
        Cards.Dispose();
        _rootSubject.Dispose();
        _lifetime.Dispose();
        return Task.CompletedTask;
    }
}
