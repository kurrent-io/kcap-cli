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
using DynamicData;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public enum ChatTabPhase { Waiting, Reading, Missing, Unavailable }

/// The Chat tab: the session's transcript, tailed and projected into chat rows, plus the composer
/// that sends through whatever channel the session offers. Ctor-scoped; TeardownAsync is the one exit.
///
/// Path identity is part of the read generation: a distinct transcript_path clears the rows and
/// installs a fresh tail in one UI-thread step, and any read still in flight for the old file
/// completes under a stale generation and is discarded.
public sealed class ChatTabViewModel : ReactiveObject {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// First retry gap after a failed withdraw; each further one doubles it.
    internal static readonly TimeSpan WithdrawRetryDelay = TimeSpan.FromSeconds(2);
    internal const int MaxWithdrawRetries = 3;

    readonly string _agentId;
    readonly ChatInput _input;
    readonly IChatTranscriptProjection? _projection;
    readonly string? _unavailableNote;
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

    /// The tail, the generation it belongs to, and the projection context and line count that
    /// live exactly as long as the file the tail is reading. Taken as one reference: reading them
    /// separately lets a switch land between them and tag a read of the old file with the new
    /// generation, which Apply's guard would then wave through onto the freshly cleared list.
    sealed class TailLease(JsonlTail tail, int generation) {
        public JsonlTail Tail { get; } = tail;
        public int Generation { get; } = generation;
        TranscriptContext? _context;
        int _linesRead;

        // The app has no session id and persists nothing, so the agent id stands in; only attachment ids would read it.
        public TranscriptContext ContextFor(IChatTranscriptProjection projection, string agentId) =>
            _context ??= projection.CreateContext(agentId, null);

        public void Reset() { _context = null; _linesRead = 0; }

        // Counts the lines the tail yields, which skips blank lines, so it is not the file's physical line number.
        public int NextLine() => ++_linesRead;
    }

    int _generation;
    int _inputGeneration;
    int _readInFlight;
    string? _path;
    string? _root;
    volatile TailLease? _lease;
    ITimer? _timer;
    volatile Task? _pendingRead;
    readonly BehaviorSubject<string?> _rootSubject = new(null);

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
        ChatTabPhase.Missing     => "The transcript file is missing",
        ChatTabPhase.Unavailable => _unavailableNote ?? "No chat view for this harness",
        _                        => "",
    };

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
    /// Replaces the composer text with the next older sent prompt; false when nothing changed.
    public bool RecallOlder() => Recall(_history.Older(ComposerText));
    /// Replaces the composer text with the next newer sent prompt, or the draft past the newest.
    public bool RecallNewer() => Recall(_history.Newer(ComposerText));

    bool Recall(string? text) {
        if (text is null) return false;
        ComposerText = text;
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
        ActivityNote = _status == "Starting"
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
            IObservable<bool>? localDaemonOnAppServer = null) {
        _agentId = agentId;
        _input = input;
        _disposables.Add(input);
        _projection = projection;
        _unavailableNote = unavailableNote;
        _opener = opener;
        _time = time;
        _permissions = permissions;
        _lifetimeToken = _lifetime.Token;
        _phase = projection is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        Cards = new PendingCardsViewModel(
            agentId, AgentOrigin.Local, sessionId ?? Observable.Return<string?>(null), permissions, _rootSubject,
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

        daemon.Agents.Connect()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnAgentsChanged)
            .DisposeWith(_disposables);

        _timer = time.CreateTimer(_ => OnTick(), null, PollInterval, PollInterval);

        daemon.Snapshots
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot => {
                _options = HostedHarnessCatalog.Build(snapshot.Daemon.SupportedVendors);
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
            var queued = new QueuedChatMessage(snapshot, edits, _inputGeneration, TranscriptLength(_path));
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
            if (outcome == ChatSendOutcome.Rejected || (outcome == ChatSendOutcome.Accepted && _projection is null))
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
    }

    void OnAgentsChanged(IChangeSet<AgentStatusDto, string> changes) {
        foreach (var change in changes) {
            if (change.Key == _agentId && change.Reason == ChangeReason.Remove) {
                _status = "Completed";
                foreach (var queued in _queuedMessages) queued.MarkUnconfirmed();
                RefreshActivityNote();
                RefreshQueue();
            }
            if (change.Key != _agentId || change.Reason is not (ChangeReason.Add or ChangeReason.Update)) continue;
            OnDto(change.Current);
        }
    }

    void OnDto(AgentStatusDto dto) {
        var notice = ParticipantNotice(dto);
        ReadOnlyNotice = notice;
        IsReadOnlyParticipant = notice.Length > 0;
        _vendor = dto.Vendor;
        // Tool paths are relative to the checkout the agent runs in. An older daemon sends only
        // RepoPath: the repository for a primary, whose worktree beneath it ToolDetail strips, or
        // the borrowed checkout for a reviewer.
        var root = dto.WorktreePath ?? dto.RepoPath;
        _root = root;
        _rootSubject.OnNext(root);
        VendorLabel = HostedHarnessCatalog.LabelFor(_options, dto.Vendor);
        ModelLabel = HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "");
        StatusText = SessionStatusDots.Label(dto);
        StatusDot = SessionStatusDots.For(dto.Status);
        _status = dto.Status;
        if (SessionStatusDots.IsTerminal(_status))
            foreach (var queued in _queuedMessages) queued.MarkUnconfirmed();
        _awaitingInput = dto.AwaitingInput;
        if (_projection is not null && dto.TranscriptPath is { } path && path != _path) SwitchPath(path);
        RefreshActivityNote();
        RefreshQueue();
    }

    void SwitchPath(string path) {
        _items.Clear();
        _pendingTools.Clear();
        _settledTools.Clear();
        _openGroup = null;
        _marked.Clear();
        _path = path;
        RebaseQueuedMessages(TranscriptLength(path));
        _lease = new TailLease(new JsonlTail(path), Interlocked.Increment(ref _generation));
        var wasWaiting = _phase == ChatTabPhase.Waiting;
        Phase = ChatTabPhase.Waiting;
        // The rows are gone, so the view has to re-read what stands in for them even when the phase
        // is unchanged — and only then, since the setter itself raises the note on a real change.
        if (wasWaiting) this.RaisePropertyChanged(nameof(PhaseNote));
        OnTick();
    }

    static long? TranscriptLength(string? path) {
        if (path is null) return null;
        try { return new FileInfo(path).Length; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    void RebaseQueuedMessages(long? offset) {
        _inputGeneration++;
        foreach (var queued in _queuedMessages) queued.Rebase(_inputGeneration, offset);
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
        if (_lease is not { } lease || _projection is not { } projection) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        _pendingRead = ReadAndApplyAsync(lease, projection);
    }

    async Task ReadAndApplyAsync(TailLease lease, IChatTranscriptProjection projection) {
        try {
            var (read, envelopes) = await Task.Run(() => {
                var result = lease.Tail.ReadAppended();
                if (result.Status == TailStatus.Reset) lease.Reset();
                var list = new List<(ChatProjectionResult Projection, long Offset)>();
                if (result.Lines.Count > 0) {
                    var context = lease.ContextFor(projection, _agentId);
                    context.BeginBatch();
                    var receivedAt = _time.GetUtcNow();
                    for (var index = 0; index < result.Lines.Count; index++) {
                        var line = result.Lines[index];
                        var lineNumber = lease.NextLine();
                        try {
                            list.Add((projection.ProjectWithInputs(line, lineNumber, receivedAt, context), result.LineStartOffsets[index]));
                        }
                        catch (Exception ex) { LogOnce($"projection: {ex.Message}"); }
                    }
                }
                return (result, list);
            }).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => Apply(lease.Generation, read, envelopes));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
        }
    }

    void Apply(int generation, TailRead read, List<(ChatProjectionResult Projection, long Offset)> lines) {
        if (generation != Volatile.Read(ref _generation)) return;

        switch (read.Status) {
            case TailStatus.Missing:
                Phase = ChatTabPhase.Missing;
                return;
            case TailStatus.Failed:
                LogOnce(read.Failure ?? "read failed");
                return;
            case TailStatus.Reset:
                // Skip everything already present in the new file, including appends that landed
                // while this read was being projected. They may be replayed history, not receipts.
                RebaseQueuedMessages(Math.Max(read.SnapshotLength ?? 0, TranscriptLength(_path) ?? 0));
                _items.Clear();
                _pendingTools.Clear();
                _settledTools.Clear();
                _openGroup = null;
                _marked.Clear();
                break;
        }

        Phase = ChatTabPhase.Reading;
        // A send made before the transcript existed has no safe baseline. Its first successful
        // read establishes one; that initial history cannot acknowledge the send.
        foreach (var queued in _queuedMessages.Where(q => !q.HasBaseline))
            queued.Rebase(_inputGeneration, Math.Max(read.SnapshotLength ?? 0, TranscriptLength(_path) ?? 0));
        RefreshQueue();
        if (lines.Count == 0) {
            RefreshActivityNote();
            return;
        }

        var fresh = new List<ChatItemViewModel>();
        foreach (var (projected, offset) in lines) {
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
        _lease = null;
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
