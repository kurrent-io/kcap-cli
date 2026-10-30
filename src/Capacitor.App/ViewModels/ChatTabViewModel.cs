using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Collections;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
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
public sealed class ChatTabViewModel : ReactiveObject, IAttachmentSink {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    /// First retry gap after a failed withdraw; each further one doubles it.
    internal static readonly TimeSpan WithdrawRetryDelay = TimeSpan.FromSeconds(2);
    internal const int MaxWithdrawRetries = 3;

    readonly ChatInput _input;
    readonly IAttachmentUploader _uploader;
    readonly Func<string, IChatTranscriptFeed>? _openFeed;
    readonly string? _unavailableNote;
    readonly string? _missingNote;
    /// The last read's refusal, cleared by the next read that is not one. It stands in for the
    /// rows while there are none and sits under them otherwise.
    string? _failureNote;
    readonly IUrlOpener _opener;
    readonly TimeProvider _time;
    readonly IPermissionService _permissions;
    readonly SessionRuns _runs;
    readonly PlanActivity? _planActivity;
    readonly Action<ToolCard>? _openCard;
    readonly MessageSenderLookup? _senders;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    // Read once: the source is disposed at teardown, and a retry waking after that still needs a
    // token it can ask without an ObjectDisposedException.
    readonly CancellationToken _lifetimeToken;
    readonly AvaloniaList<ChatItemViewModel> _items = new();
    readonly AvaloniaList<QueuedChatMessage> _queuedMessages = new();
    // Keep retired sends eligible for late echoes without letting them consume a later send's receipt.
    readonly List<(QueuedChatMessage Old, QueuedChatMessage Replacement)> _superseded = new();
    int _nextSendOrder;
    /// The send the channel has not answered yet. It is tracked like a queued message but kept out
    /// of the strip: the composer still holds its text, and one prompt must not show in both.
    QueuedChatMessage? _sending;
    QueuedChatMessage? _lastSent;
    bool _sendCommandExecuting;
    // Row sends bypass SendCommand, so its IsExecuting cannot gate concurrent composer sends.
    readonly BehaviorSubject<bool> _queuedSendBusy = new(false);

    IEnumerable<QueuedChatMessage> Tracked => _sending is { } sending ? _queuedMessages.Append(sending) : _queuedMessages;
    readonly Dictionary<string, ToolCallItem> _pendingTools = new(StringComparer.Ordinal);
    // Every tool id with a result, not only the running ones: a replayed request can arrive after
    // the transcript's initial load, and then only this set can tell that its tool is done.
    readonly HashSet<string> _settledTools = new(StringComparer.Ordinal);
    // AskUserQuestion's permission hook carries no tool-use id, so a finished ask is recognized
    // by question text. The latest overlapping ask is the one a card belongs to.
    readonly List<AskedQuestion> _questionAsks = [];
    readonly HashSet<string> _withdrawing = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _withdrawFailures = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, byte> _loggedFailures = new(StringComparer.Ordinal);
    readonly Dictionary<string, PendingPermissionRequest> _requests = new(StringComparer.Ordinal);
    readonly HashSet<ToolCallItem> _marked = new(ReferenceEqualityComparer.Instance);
    ToolGroupItem? _openGroup;
    /// The bang command waiting for the output record that follows it.
    ShellCommandItem? _openShell;

    /// The feed and its generation travel together so a switched-out read cannot be tagged as current.
    sealed class FeedLease : IDisposable {
        public IChatTranscriptFeed Feed { get; }
        public int Generation { get; }
        public int Pending = 1;
        public int WakePosted;
        readonly Action _changed;

        public FeedLease(IChatTranscriptFeed feed, int generation, Action<FeedLease> changed) {
            Feed = feed;
            Generation = generation;
            _changed = () => changed(this);
            if (feed is RemoteTranscriptFeed remote) remote.Changed += _changed;
        }

        public void Dispose() {
            if (Feed is RemoteTranscriptFeed remote) remote.Changed -= _changed;
            Feed.Dispose();
        }
    }

    int _generation;
    int _inputGeneration;
    int _baselineVersion;
    int _readInFlight;
    string? _feedKey;
    bool _feedReleased;
    /// The session the listed foreign rows belong to.
    string? _queueKey;
    // Dispatch ids are unique across sessions, and a snapshot can arrive late, so a discarded id
    // stays retired for the life of the pane.
    readonly HashSet<Guid> _discardedDispatches = [];
    sealed record ReceiptCandidates(string? QueueKey, QueuedChatMessage[] Attempts);
    readonly Dictionary<SenderLabel, ReceiptCandidates> _observedReceipts = [];
    readonly Dictionary<Guid, ReceiptCandidates> _unboundReceipts = [];
    const string DiscardFailedNotice = "Could not discard this attempt. Try again.";
    bool _discardBusy;
    string? _root;

    volatile FeedLease? _lease;
    ITimer? _timer;
    volatile Task? _pendingRead;
    readonly BehaviorSubject<string?> _rootSubject = new(null);

    long? CurrentOffset => _lease?.Feed.CurrentOffset;

    public IAvaloniaReadOnlyList<ChatItemViewModel> Items => _items;
    public IAvaloniaReadOnlyList<QueuedChatMessage> QueuedMessages => _queuedMessages;
    public bool HasQueuedMessages => _queuedMessages.Count > 0;
    /// Unmatched server entries have no proven client identity.
    public string QueueSummary {
        get {
            var foreign     = _queuedMessages.Count(q => q.IsForeign);
            var unconfirmed = _queuedMessages.Count(q => q.IsUnconfirmed);
            (int Count, string Word)[] segments =
                [(_queuedMessages.Count - unconfirmed - foreign, "queued"), (unconfirmed + foreign, "unconfirmed")];

            return string.Join(" · ", segments.Where(s => s.Count > 0)
                .Select((s, i) => i == 0 ? $"{MessageCount(s.Count)} {s.Word}" : $"{s.Count} {s.Word}"));
        }
    }

    /// A foreign MCP call: the usual detail keys first, then whatever string it was given.
    string FirstOrAny(string? inputJson, ToolCategory category) {
        var known = ToolDetail.From(inputJson, _root, category);
        return known.Length > 0 ? known : ToolDetail.FirstString(inputJson);
    }

    Action? CardOpener(ToolCard card) {
        if (card.Url is null && card.DocumentPath is null) return null;
        if (_openCard is { } open) return () => open(card);
        if (card.Url is { } url) return () => LinkPolicy.Open(_opener, url);
        return null;
    }

    static string MessageCount(int count) => $"{count} message{(count == 1 ? "" : "s")}";

    void RefreshQueue() {
        RefreshQueuedSend();
        this.RaisePropertyChanged(nameof(HasQueuedMessages));
        this.RaisePropertyChanged(nameof(QueueSummary));
    }

    /// While a turn is live a foreground launch is already a Task row in the transcript, so the
    /// strip keeps only the runs that outlive the turn.
    IEnumerable<RunRow> StripRows =>
        ActivityNote.Length == 0 ? _runs.Running : _runs.Running.Where(static r => r.IsBackground);

    public bool HasRunningRuns => StripRows.Any();

    /// The one strip row when there is exactly one: the view reads its name and state line, which
    /// the row itself keeps current, so a tick changes nothing here.
    public RunRow? RunningRow {
        get {
            using var rows = StripRows.GetEnumerator();
            if (!rows.MoveNext()) return null;
            var only = rows.Current;
            return rows.MoveNext() ? null : only;
        }
    }

    public string RunSummary {
        get {
            var running = StripRows.ToList();
            if (running.Count < 2) return "";
            var shells = running.Count(r => r.IsShell);
            var agents = running.Count - shells;
            var counts = (agents, shells) switch {
                (_, 0) => $"{agents} subagents",
                (0, _) => $"{shells} commands",
                _      => $"{Plural(agents, "subagent")}, {Plural(shells, "command")}",
            };
            return running.All(r => r.IsBackground) ? $"{counts} running in background" : $"{counts} running";
        }
    }

    static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    void RefreshRuns() {
        this.RaisePropertyChanged(nameof(HasRunningRuns));
        this.RaisePropertyChanged(nameof(RunningRow));
        this.RaisePropertyChanged(nameof(RunSummary));
        RefreshAgentStatus();
    }

    public PendingCardsViewModel Cards { get; }
    public ReadOnlyObservableCollection<PendingCardViewModel> PendingCards => Cards.PendingCards;

    readonly ObservableAsPropertyHelper<bool> _hasPendingCards;
    public bool HasPendingCards => _hasPendingCards.Value;

    bool _hasSeed;
    /// Whether the current feed has delivered its first Reset. A remote send made before it has no
    /// baseline, and its echo can land inside the history that Reset replays, where nothing acknowledges it.
    public bool HasSeed {
        get => _hasSeed;
        private set {
            if (_hasSeed == value) return;
            this.RaiseAndSetIfChanged(ref _hasSeed, value);
            RefreshQueuedSend();
        }
    }

    ChatTabPhase _phase;
    public ChatTabPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            RaisePhaseNote();
            RefreshActivityNote();
        }
    }

    /// A working agent's chat is never empty for long, so an empty one is still waiting, read or not,
    /// until the session ends.
    public string PhaseNote => _items.Count > 0 ? "" : Phase switch {
        ChatTabPhase.Waiting or ChatTabPhase.Reading when !_ended => "Waiting for chat…",
        ChatTabPhase.Missing     => _missingNote ?? "The transcript file is missing",
        ChatTabPhase.Unavailable => _unavailableNote ?? "No chat view for this harness",
        ChatTabPhase.Failed      => FailureNote(_failureNote),
        _                        => "",
    };

    /// Nothing in the chat yet can mean the agent is held at a prompt only its terminal shows, so
    /// the empty chat's note points there, unless the terminal-menu card already does.
    public bool ShowsTerminalOffer => ShowTerminal is not null && PhaseNote.Length > 0 && !_ended && !ShowsTerminalMenu;

    void RaisePhaseNote() {
        this.RaisePropertyChanged(nameof(PhaseNote));
        this.RaisePropertyChanged(nameof(ShowsTerminalOffer));
    }

    /// The chips staged for the next prompt. A send clears the ones it carried, never the tray.
    public AttachmentTray Tray { get; } = new();

    int _uploadingFiles;
    /// How many chips the upload in flight carries — the snapshot the send took, not the tray,
    /// which the user may go on staging into. Zero when nothing is uploading.
    public int UploadingFiles {
        get => _uploadingFiles;
        private set {
            if (_uploadingFiles == value) return;
            var was = Uploading;
            this.RaiseAndSetIfChanged(ref _uploadingFiles, value);
            if (was != Uploading) this.RaisePropertyChanged(nameof(Uploading));
        }
    }

    /// The staged bytes are on their way to the server; the prompt itself has not left yet.
    public bool Uploading => UploadingFiles > 0;

    readonly BehaviorSubject<string?> _intakeNotice = new(null);

    void Notice(string? text) {
        if (!_lifetimeToken.IsCancellationRequested) _intakeNotice.OnNext(text);
    }

    static string FailureNote(string? reason) =>
        reason is null ? "The transcript could not be read" : $"The transcript could not be read: {reason}";

    string _composerText = "";
    int _composerEdits;
    public string ComposerText {
        get => _composerText;
        set {
            if (string.Equals(_composerText, value, StringComparison.Ordinal)) return;
            _composerEdits++;
            Notice(null);
            this.RaiseAndSetIfChanged(ref _composerText, value);
            _drafts?.Set(_origin, _agentId, value);
        }
    }

    readonly ComposerDrafts? _drafts;
    readonly AgentOrigin _origin;
    readonly string _agentId;

    // Counted as an edit, so a send settling in this tab cannot clear text it did not type.
    void OnStoredDraftChanged(string text) {
        if (string.Equals(_composerText, text, StringComparison.Ordinal)) return;
        _composerEdits++;
        this.RaiseAndSetIfChanged(ref _composerText, text, nameof(ComposerText));
    }

    readonly ComposerHistory _history = new();
    // Own sends, until the transcript echoes them into the history. The queue's acknowledgement
    // decides the echo, so a replay or a reworded receipt neither settles nor doubles one.
    readonly List<QueuedChatMessage> _unechoed = new();
    // Keep confirmed literal receipts for replay in this feed. Display cleanup must not hide a
    // user turn again after its queue row has retired. A different feed drops this evidence.
    readonly HashSet<(long Offset, string Text)> _literalInputHistory = [];
    int _recallEdits = -1;
    /// Replaces the composer text with the next older prompt; false when nothing changed.
    public bool RecallOlder() => Recall(_history.Older(Recallable(), Unechoed()));
    /// Replaces the composer text with the next newer prompt, or the draft past the newest.
    public bool RecallNewer() => Recall(_history.Newer(Recallable(), Unechoed()));

    List<string> Unechoed() {
        _unechoed.RemoveAll(q => q.Acknowledged);
        return _unechoed.Select(q => q.Text).ToList();
    }

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
    public ReactiveCommand<QueuedChatMessage, Unit> SendQueuedCommand { get; }
    public ReactiveCommand<QueuedChatMessage, Unit> DiscardQueuedCommand { get; }
    public ReactiveCommand<Unit, Unit> InterruptCommand { get; }
    public ReactiveCommand<string, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<UserAttachment, Unit> OpenAttachmentCommand { get; }
    public ReactiveCommand<string, Unit> RunCodeCommand { get; }

    // The opener launches whatever it is handed, so only a resolved image path reaches it: a
    // transcript line naming an executable must not run it.
    void OpenAttachment(UserAttachment attachment) {
        if (attachment.PreviewPath is not { } path) return;
        try { _opener.Open(path); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: open attachment failed: {ex.Message}"); }
    }

    readonly ObservableAsPropertyHelper<string> _composerHint;
    public string ComposerHint => _composerHint.Value;

    UsageLimitNoticeDto? _usageLimit;
    TerminalDialogDto? _terminalDialog;
    string? _interruption;
    bool _terminalDialogShown;
    bool _hasTerminalMenu;
    string _terminalMenuTitle = "";
    string _terminalMenuHeading = "";
    string _terminalMenuSummary = "";
    string _terminalMenuScreen = "";
    string _terminalMenuPrompt = "";
    string _terminalMenuHint = "";
    string _terminalMenuError = "";
    IReadOnlyList<TerminalMenuChoiceViewModel> _terminalMenuChoices = [];
    bool _terminalMenuChoiceTaken;

    /// A vendor menu is on the terminal: a usage-limit question or a select dialog. Text sent now
    /// would be typed into it. Stays set while a pending card hides the card itself.
    public bool HasTerminalMenu {
        get => _hasTerminalMenu;
        private set => this.RaiseAndSetIfChanged(ref _hasTerminalMenu, value);
    }

    bool _showsTerminalMenu;
    /// The menu card. A pending hook card hides the screen's copy of the dialog it raised.
    public bool ShowsTerminalMenu {
        get => _showsTerminalMenu;
        private set {
            this.RaiseAndSetIfChanged(ref _showsTerminalMenu, value);
            this.RaisePropertyChanged(nameof(ShowsTerminalOffer));
        }
    }

    public string TerminalMenuTitle {
        get => _terminalMenuTitle;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuTitle, value);
    }

    public string TerminalMenuHeading {
        get => _terminalMenuHeading;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuHeading, value);
    }

    /// The dialog as the terminal shows it, for a dialog whose choices could not be read.
    public string TerminalMenuScreen {
        get => _terminalMenuScreen;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuScreen, value);
    }

    /// Set by the workspace hosting this chat when the agent has a terminal; the menu card's fallback
    /// and the empty chat's offer send the user there.
    public Action? ShowTerminal { get; set; }
    /// Called with the session id as a prompt goes out; the action it returns runs only if the
    /// channel accepts the prompt. A reply answers the session's open questions, and no permission
    /// response will ever say so.
    public Func<string, Action>? InputSending { get; init; }
    /// Raised once a turn has gone to the channel and was not refused.
    public event Action? TurnSubmitted;
    volatile string? _sessionId;
    public ReactiveCommand<Unit, Unit> ShowTerminalCommand { get; }

    public string TerminalMenuSummary {
        get => _terminalMenuSummary;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuSummary, value);
    }

    public string TerminalMenuPrompt {
        get => _terminalMenuPrompt;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuPrompt, value);
    }

    public string TerminalMenuHint {
        get => _terminalMenuHint;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuHint, value);
    }

    public string TerminalMenuError {
        get => _terminalMenuError;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuError, value);
    }

    public IReadOnlyList<TerminalMenuChoiceViewModel> TerminalMenuChoices {
        get => _terminalMenuChoices;
        private set => this.RaiseAndSetIfChanged(ref _terminalMenuChoices, value);
    }

    /// False once a choice has been sent, until the menu changes. A failed send turns it back on.
    public bool TerminalMenuChoicesOpen => !_terminalMenuChoiceTaken;

    readonly ObservableAsPropertyHelper<bool> _showsComposer;
    /// Input + Send stay in the tree only while messaging is still possible. An ended session
    /// hides them and leaves the hint — a greyed empty box is the wrong affordance.
    public bool ShowsComposer => _showsComposer.Value;

    readonly ObservableAsPropertyHelper<bool> _showsInterrupt;
    public bool ShowsInterrupt => _showsInterrupt.Value;

    readonly ObservableAsPropertyHelper<bool> _canInterruptWithEscape;
    public bool CanInterruptWithEscape => _canInterruptWithEscape.Value;

    readonly ObservableAsPropertyHelper<string> _interruptLabel;
    public string InterruptLabel => _interruptLabel.Value;

    string _vendor = "";
    IReadOnlyList<HarnessOption> _options = HostedHarnessCatalog.Build(null);

    string _vendorLabel = "";
    public string VendorLabel {
        get => _vendorLabel;
        private set {
            if (_vendorLabel == value) return;
            this.RaiseAndSetIfChanged(ref _vendorLabel, value);
            this.RaisePropertyChanged(nameof(AssistantTitle));
        }
    }

    /// Small title on assistant prose: the harness name once the session has one, else a role.
    public string AssistantTitle => string.IsNullOrEmpty(_vendorLabel) ? "Assistant" : _vendorLabel;

    bool _isOwnedByCurrentUser;
    public string UserTitle => _origin == AgentOrigin.Local || _isOwnedByCurrentUser ? "You" : "User";

    string _modelLabel = "default";
    public string ModelLabel { get => _modelLabel; private set => this.RaiseAndSetIfChanged(ref _modelLabel, value); }

    string? _accountLabel;
    public string? AccountLabel { get => _accountLabel; private set => this.RaiseAndSetIfChanged(ref _accountLabel, value); }

    string? _accountDirectory;
    public string? AccountDirectory { get => _accountDirectory; private set => this.RaiseAndSetIfChanged(ref _accountDirectory, value); }

    string _accountVendor = "";
    public string AccountVendor { get => _accountVendor; private set => this.RaiseAndSetIfChanged(ref _accountVendor, value); }

    bool _showsAccountStrip;
    public bool ShowsAccountStrip { get => _showsAccountStrip; private set => this.RaiseAndSetIfChanged(ref _showsAccountStrip, value); }

    readonly BehaviorSubject<string?> _accountIds = new(null);

    AccountUsageView? _accountStripUsage;
    /// The session's account reading on its host, or null when the host is not drawable or the
    /// account has no registry id there.
    public AccountUsageView? AccountStripUsage {
        get => _accountStripUsage;
        private set {
            if (Equals(_accountStripUsage, value)) return;
            this.RaiseAndSetIfChanged(ref _accountStripUsage, value);
            this.RaisePropertyChanged(nameof(ShowsAccountUsage));
            this.RaisePropertyChanged(nameof(ShowsAccountDirectory));
            this.RaisePropertyChanged(nameof(AccountReadingNote));
        }
    }

    public bool ShowsAccountUsage => _accountStripUsage is { } usage && usage.LineText != AccountUsage.NoReading;

    public bool ShowsAccountDirectory => !ShowsAccountUsage;

    public string? AccountReadingNote => _accountStripUsage?.LineText == AccountUsage.NoReading ? "· " + AccountUsage.NoReading : null;

    static AccountUsageView? StripUsage(string? accountId, AccountsSnapshotDto? snapshot, DateTimeOffset now) =>
        accountId is not null && snapshot?.Accounts?.FirstOrDefault(a => a.Id == accountId) is { } account
            ? AccountUsage.Project(account, now, TimeZoneInfo.Local)
            : null;

    string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    AgentStatusPresentation _agentStatus = AgentStatusPresentation.None;
    public AgentStatusPresentation AgentStatus {
        get => _agentStatus;
        private set => this.RaiseAndSetIfChanged(ref _agentStatus, value);
    }

    bool _showsActivityNote;
    public bool ShowsActivityNote {
        get => _showsActivityNote;
        private set => this.RaiseAndSetIfChanged(ref _showsActivityNote, value);
    }

    bool _showsLiveNote;
    public bool ShowsLiveNote {
        get => _showsLiveNote;
        private set => this.RaiseAndSetIfChanged(ref _showsLiveNote, value);
    }

    string _activityNote = "";
    /// Live elapsed time throughout a busy turn, including while output is streaming.
    public string ActivityNote {
        get => _activityNote;
        private set {
            if (_activityNote == value) return;
            var turnChanged = _activityNote.Length == 0 != (value.Length == 0);
            this.RaiseAndSetIfChanged(ref _activityNote, value);
            if (!turnChanged) return;
            this.RaisePropertyChanged(nameof(HasRunningRuns));
            this.RaisePropertyChanged(nameof(RunningRow));
            this.RaisePropertyChanged(nameof(RunSummary));
        }
    }

    string _status = "";
    bool _ended;
    bool? _awaitingInput;
    bool _turnAttested;
    bool _waitsOnUser;
    int? _liveSubagents;
    long? _workingSince;
    TimeSpan _worked;
    DateTime? _daemonWorkingSince;
    bool _turnBaselined;

    void RefreshActivityNote() {
        var inTurn = SessionStatusDots.IsWorking(_status, _awaitingInput, _liveSubagents);
        var working = inTurn && !HasPendingCards;
        if (!inTurn) {
            _workingSince = null;
            _worked = TimeSpan.Zero;
            _turnBaselined = false;
        } else if (!_turnBaselined) {
            // A pane built mid-turn picks up the time the daemon has already seen pass, read when the
            // turn is first seen so time spent behind a card afterwards stays excluded.
            _turnBaselined = true;
            if (_daemonWorkingSince is { } started && _time.GetUtcNow().UtcDateTime - started is { Ticks: > 0 } before)
                _worked = before;
        }
        if (working) {
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
        ShowsActivityNote = _failureNote is not null && Phase != ChatTabPhase.Failed && ActivityNote.Length > 0;
        ShowsLiveNote = !ShowsActivityNote && ActivityNote.Length > 0;
        RefreshAgentStatus();
        RefreshQueuedSend();
    }

    bool AgentIsIdle => !_ended && !SessionStatusDots.IsWorking(_status, _awaitingInput, _liveSubagents);

    /// Idle, and the channel will take text: a queued prompt is not waiting on a step.
    /// A send still uploading keeps CanAcceptText true, so the busy flag closes that window too.
    bool AgentCanTakeQueuedNow =>
        AgentIsIdle && !IsReadOnlyParticipant && !HasTerminalMenu && _input.CanAcceptText
        && !_sendCommandExecuting && !_queuedSendBusy.Value && CanSendBeforeSeed;

    /// A remote send needs the transcript's seed to be acknowledged against; a local one does not.
    bool CanSendBeforeSeed => HasSeed || _origin != AgentOrigin.Remote;

    void RefreshQueuedSend() {
        bool? idle = _status == "Running" && _awaitingInput is null && !SessionStatusDots.IsWorking(_status, _awaitingInput, _liveSubagents)
            ? null : AgentIsIdle;
        // A discard in flight keeps its row from being sent again under it.
        var can = AgentCanTakeQueuedNow && !_discardBusy;
        var discard = !_discardBusy && !IsReadOnlyParticipant;
        foreach (var queued in _queuedMessages)
            queued.SetPickup(idle, can, discard && (_origin != AgentOrigin.Remote || queued.DispatchId is not null), discard && _input.CanDiscardQueued);
    }

    void RefreshAgentStatus() {
        var question = UsageLimitNoticeDto.IsQuestion(_usageLimit);
        var elapsed = ActivityNote.StartsWith("Working for ", StringComparison.Ordinal) ? ActivityNote : null;
        var stage = ActivityNote.StartsWith("Starting ", StringComparison.Ordinal) ? ActivityNote : null;
        var answerExpected = Cards.PendingCards.Any(static c => c is QuestionCardViewModel or AcpQuestionCardViewModel);
        AgentStatus = SessionStatusDots.Present(
            _status, _awaitingInput, _waitsOnUser, _liveSubagents, HasPendingCards,
            question ? _usageLimit!.Summary : null, stage, elapsed,
            answerExpected: answerExpected, ended: _ended, liveCommands: _runs.RunningCommandCount,
            terminalDialog: _terminalDialog is not null, interruption: _interruption, agentId: _agentId,
            turnAttested: _turnAttested);
        StatusText = AgentStatus.Label;
    }

    static string WorkingNote(TimeSpan elapsed) => $"Working for {RunRow.Duration(elapsed)}";

    bool _isReadOnlyParticipant;
    /// True for a flow participant (any kind other than "agent"): the view swaps the composer
    /// input for the read-only banner, and the send gate refuses regardless of the terminal
    /// attach — the kind, not the handshake, is what makes the session unmessageable.
    public bool IsReadOnlyParticipant {
        get => _isReadOnlyParticipant;
        private set {
            this.RaiseAndSetIfChanged(ref _isReadOnlyParticipant, value);
            this.RaisePropertyChanged(nameof(ShowsReadOnlyBanner));
        }
    }

    bool _takesAttachments = true;
    internal bool TakesAttachments { get => _takesAttachments; set => this.RaiseAndSetIfChanged(ref _takesAttachments, value); }

    internal const string NoAttachmentsHint = "a review agent takes no attachments";

    HandoffComposerViewModel? _handoff;
    /// An ended session's hand-off composer, shown in the composer's place; the host owns its lifetime.
    public HandoffComposerViewModel? Handoff {
        get => _handoff;
        set {
            this.RaiseAndSetIfChanged(ref _handoff, value);
            this.RaisePropertyChanged(nameof(ShowsReadOnlyBanner));
        }
    }

    public bool ShowsReadOnlyBanner => IsReadOnlyParticipant && Handoff is null;

    string _readOnlyNotice = "";
    public string ReadOnlyNotice { get => _readOnlyNotice; private set => this.RaiseAndSetIfChanged(ref _readOnlyNotice, value); }

    /// Why this session cannot be messaged, or "" when it can. Mirrors the wording of the daemon's
    /// attach-time ProtectionReason so the chat banner and the terminal banner name the same block
    /// identically.
    internal static string ParticipantNotice(AgentStatusDto dto) {
        if (AgentActionService.AcceptsTypedInput(dto.Kind)) return "";
        var role = string.IsNullOrEmpty(dto.FlowRole) ? "" : $", role {dto.FlowRole}";
        var flow = string.IsNullOrEmpty(dto.FlowRunId) ? "" : $" (flow {dto.FlowRunId}{role})";
        return $"{dto.Kind} agent{flow}";
    }

    /// The daemon cache as session facts: an add or update is the dto, a removal keeps the last
    /// facts under a Completed status. Identical revisions are not republished.
    static IObservable<ChatSessionInfo> LocalSession(string agentId, IDaemonClientService daemon) =>
        daemon.Agents.Connect()
            .Filter(dto => dto.Id == agentId)
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Scan((ChatSessionInfo?)null, (last, changes) => {
                var info = last;
                foreach (var change in changes) {
                    if (change.Reason == ChangeReason.Remove)
                        info = (info ?? ChatSessionInfo.Gone) with {
                            Status = "Completed", StatusLabel = "Done", Ended = true, UsageLimit = null,
                            TerminalDialog = null, WaitsOnUser = false,
                        };
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
    static Func<string, IChatTranscriptFeed> LocalFeed(string agentId, IChatTranscriptProjection projection, TimeProvider time, LocalTranscripts? transcripts) {
        var logged = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        Action<string> log = reason => { if (logged.TryAdd(reason, 0)) Console.Error.WriteLine($"kcap: chat transcript: {reason}"); };
        return path => transcripts?.Open(path, projection, agentId, time, log) ?? new LocalTranscriptFeed(path, projection, agentId, time, log);
    }

    /// Test-only seam: the read in flight, or the last one started. A switch that loses the
    /// in-flight CAS starts no read of its own, so this still points at the previous file's read —
    /// await that, advance one tick, then await again to see the new path's first rows.
    internal Task? PendingReadForTesting => _pendingRead;

    /// Test-only seam: withdraws sent and not yet acknowledged, or failed.
    internal int WithdrawsInFlightForTesting => _withdrawing.Count;

    public ChatTabViewModel(
            string agentId, IDaemonClientService daemon, ChatInput input, IAttachmentUploader uploader,
            IChatTranscriptProjection? projection, IUrlOpener opener, TimeProvider time, IPermissionService permissions,
            SessionRuns runs, string? unavailableNote = null, IObservable<string?>? sessionId = null,
            IObservable<bool>? localDaemonOnAppServer = null, PlanActivity? planActivity = null,
            Action<ToolCard>? openCard = null, ComposerDrafts? drafts = null, LocalTranscripts? transcripts = null,
            IMessageSenderSource? senders = null, IObservable<AccountsSnapshotDto?>? accountReadings = null,
            IObservable<DateTimeOffset>? usageClock = null, IObservable<Unit>? signInCompleted = null)
        : this(agentId, AgentOrigin.Local, LocalSession(agentId, daemon), daemon.Snapshots.Select(s => s.Daemon.SupportedVendors),
               input, uploader, projection is null ? null : LocalFeed(agentId, projection, time, transcripts), opener, time, permissions,
               runs, unavailableNote, null, sessionId, localDaemonOnAppServer, planActivity: planActivity, openCard: openCard,
               drafts: drafts, senders: senders, accountReadings: accountReadings, usageClock: usageClock,
               signInCompleted: signInCompleted) { }

    public ChatTabViewModel(
            string agentId, AgentOrigin origin, IObservable<ChatSessionInfo> session, IObservable<string[]?> supportedVendors,
            ChatInput input, IAttachmentUploader uploader, Func<string, IChatTranscriptFeed>? openFeed, IUrlOpener opener, TimeProvider time,
            IPermissionService permissions, SessionRuns runs, string? unavailableNote = null, string? missingNote = null,
            IObservable<string?>? sessionId = null, IObservable<bool>? localDaemonOnAppServer = null,
            IObservable<IReadOnlyList<QueuedInputItem>>? serverQueue = null, PlanActivity? planActivity = null,
            Action<ToolCard>? openCard = null, ComposerDrafts? drafts = null, IMessageSenderSource? senders = null,
            IObservable<AccountsSnapshotDto?>? accountReadings = null, IObservable<DateTimeOffset>? usageClock = null,
            IObservable<Unit>? signInCompleted = null) {
        _drafts = drafts;
        _origin = origin;
        _agentId = agentId;
        _composerText = drafts?.Get(origin, agentId) ?? "";
        drafts?.Watch(origin, agentId, OnStoredDraftChanged).DisposeWith(_disposables);
        _planActivity = planActivity;
        _openCard = openCard;
        _input = input;
        _uploader = uploader;
        _disposables.Add(input);
        _openFeed = openFeed;
        _unavailableNote = unavailableNote;
        _missingNote = missingNote;
        _opener = opener;
        _time = time;
        _permissions = permissions;
        _runs = runs;
        _runs.Changed += RefreshRuns;
        _lifetimeToken = _lifetime.Token;
        _senders = senders is null ? null : new MessageSenderLookup(senders, time, _lifetimeToken, LogOnce, ConfirmAttributedDispatch);
        _phase = openFeed is null ? ChatTabPhase.Unavailable : ChatTabPhase.Waiting;

        // A local session's id names an app-server session only while the local daemon reports
        // that server; otherwise a match is another session's.
        var onAppServer = origin == AgentOrigin.Remote ? Observable.Return(true) : localDaemonOnAppServer ?? Observable.Return(true);
        var admittedSessionId = sessionId?.CombineLatest(onAppServer, (id, admitted) => admitted ? id : null);
        admittedSessionId?.Subscribe(id => _sessionId = id).DisposeWith(_disposables);
        if (_senders is { } lookup) {
            admittedSessionId?.ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(id => lookup.SetSession(id, SenderLabels(_items))).DisposeWith(_disposables);
            signInCompleted?.ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => lookup.Restart(SenderLabels(_items))).DisposeWith(_disposables);
        }
        Cards = new PendingCardsViewModel(
            agentId, origin, sessionId ?? Observable.Return<string?>(null), permissions, _rootSubject,
            localDaemonOnAppServer);
        _hasPendingCards = Cards.WhenAnyValue(c => c.HasPendingCards)
            .ToProperty(this, x => x.HasPendingCards, initialValue: Cards.HasPendingCards)
            .DisposeWith(_disposables);

        this.WhenAnyValue(x => x.HasPendingCards)
            .Subscribe(_ => {
                RefreshActivityNote();
                ApplyTerminalMenu(_usageLimit, _terminalDialog);
            })
            .DisposeWith(_disposables);

        // The note follows whether the chat is empty, which every path that adds or clears rows changes.
        var empty = true;
        _items.CollectionChanged += (_, _) => {
            if (empty == (_items.Count == 0)) return;
            empty = _items.Count == 0;
            RaisePhaseNote();
        };
        ShowTerminalCommand = ReactiveCommand.Create(() => ShowTerminal?.Invoke());
        _disposables.Add(ShowTerminalCommand);

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
                SyncPendingCardItems();
                // HasPendingCards stays true when a question is replaced by a permission, so the
                // header status has to be recomputed from the cards themselves.
                RefreshAgentStatus();
            })
            .DisposeWith(_disposables);

        session
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSession)
            .DisposeWith(_disposables);

        if (accountReadings is not null)
            _accountIds.DistinctUntilChanged()
                .CombineLatest(accountReadings, usageClock ?? Observable.Return(time.GetUtcNow()), StripUsage)
                .DistinctUntilChanged()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(usage => AccountStripUsage = usage)
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

        _showsInterrupt = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.CanInterrupt),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                this.WhenAnyValue(x => x.HasTerminalMenu),
                this.WhenAnyValue(x => x.AgentStatus),
                (can, readOnly, menu, status) => can && !readOnly && !menu
                    && (status.Kind == AgentStatusKind.Working || status.IsRunningUnknownTurn))
            .ToProperty(this, x => x.ShowsInterrupt,
                initialValue: _input.CanInterrupt && !IsReadOnlyParticipant && !HasTerminalMenu
                    && (AgentStatus.Kind == AgentStatusKind.Working || AgentStatus.IsRunningUnknownTurn))
            .DisposeWith(_disposables);

        // The button still interrupts an unproven turn; only the shortcut, which a user presses
        // without looking, needs the daemon's proof that a prompt is not sitting idle.
        _canInterruptWithEscape = this.WhenAnyValue(x => x.ShowsInterrupt, x => x.AgentStatus,
                (shows, status) => shows && status.InForegroundTurn)
            .ToProperty(this, x => x.CanInterruptWithEscape, initialValue: false)
            .DisposeWith(_disposables);

        _interruptLabel = this.WhenAnyValue(x => x.CanInterruptWithEscape,
                escape => escape ? "Interrupt (Esc)" : "Interrupt")
            .ToProperty(this, x => x.InterruptLabel, initialValue: "Interrupt")
            .DisposeWith(_disposables);

        // The banner carries the read-only explanation for a flow participant, so the hint
        // goes blank there instead of offering a reply that can never be sent.
        _composerHint = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.Hint),
                _input.WhenAnyValue(i => i.Availability),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                this.WhenAnyValue(x => x.UploadingFiles),
                this.WhenAnyValue(x => x.TerminalMenuHint),
                _intakeNotice,
                (hint, availability, readOnly, uploading, menuHint, notice) =>
                    availability == SendAvailability.Ended ? (readOnly ? "" : hint)
                        : uploading > 0 ? $"Uploading {uploading} file{(uploading == 1 ? "" : "s")}…"
                        : notice ?? (readOnly ? ""
                            : menuHint.Length > 0 ? menuHint
                            : hint))
            .ToProperty(this, x => x.ComposerHint, initialValue: IsReadOnlyParticipant ? "" : _input.Hint)
            .DisposeWith(_disposables);

        _showsComposer = Observable.CombineLatest(
                _input.WhenAnyValue(i => i.Availability),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant),
                this.WhenAnyValue(x => x.Handoff),
                (availability, readOnly, handoff) => !readOnly && availability != SendAvailability.Ended && handoff is null)
            .ToProperty(this, x => x.ShowsComposer,
                initialValue: !IsReadOnlyParticipant && _input.Availability != SendAvailability.Ended && Handoff is null)
            .DisposeWith(_disposables);

        _input.WhenAnyValue(i => i.Availability)
            .Subscribe(_ => SyncPendingCardItems())
            .DisposeWith(_disposables);

        // The view reaches the gate through the sink, so its two members are the ones the binding
        // listens for: the channel's own notifications are republished under those names.
        Observable.Merge(
                _input.WhenAnyValue(i => i.CanAttach).Select(_ => Unit.Default),
                _input.WhenAnyValue(i => i.AttachHint).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.IsReadOnlyParticipant).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.TakesAttachments).Select(_ => Unit.Default))
            .Subscribe(_ => {
                this.RaisePropertyChanged(nameof(IAttachmentSink.CanAttach));
                this.RaisePropertyChanged(nameof(IAttachmentSink.AttachHint));
            })
            .DisposeWith(_disposables);

        // A paste stays in flight after the row is added. The note is already right; the button
        // waits until the channel will take another send. A terminal menu is the same: the channel
        // may still accept text, but that text would land in the menu.
        Observable.Merge(
                _input.WhenAnyValue(i => i.CanAcceptText).Select(_ => Unit.Default),
                _input.WhenAnyValue(i => i.CanDiscardQueued).Select(_ => Unit.Default),
                this.WhenAnyValue(x => x.HasTerminalMenu).Select(_ => Unit.Default))
            .Subscribe(_ => RefreshQueuedSend())
            .DisposeWith(_disposables);

        _queuedSendBusy.DisposeWith(_disposables);
        var canSend = Observable.CombineLatest(
            this.WhenAnyValue(x => x.ComposerText),
            _input.WhenAnyValue(i => i.CanAcceptText),
            this.WhenAnyValue(x => x.IsReadOnlyParticipant),
            this.WhenAnyValue(x => x.HasTerminalMenu),
            _queuedSendBusy,
            this.WhenAnyValue(x => x.HasSeed),
            (text, can, readOnly, menu, busy, _) => can && !readOnly && !menu && !busy && !string.IsNullOrWhiteSpace(text)
                && CanSendBeforeSeed);
        // The composer keeps whatever the user typed while the channel was deciding: only the
        // snapshot that was actually sent is cleared, and only once the channel commits it. The
        // edit count is what the text alone cannot say — an edit that lands back on the sent text
        // is still the user's own draft, not the snapshot.
        SendCommand = ReactiveCommand.CreateFromTask(async () => {
            await SendComposerAsync(ComposerText, includeTray: true, bypassRecall: false);
        }, canSend);
        _disposables.Add(SendCommand);
        SendCommand.IsExecuting
            .Subscribe(executing => {
                _sendCommandExecuting = executing;
                RefreshQueuedSend();
            })
            .DisposeWith(_disposables);

        // Only unchanged submitted or recalled text belongs to this row.
        SendQueuedCommand = ReactiveCommand.CreateFromTask<QueuedChatMessage>(async message => {
            if (_lifetimeToken.IsCancellationRequested || !AgentCanTakeQueuedNow
                || !message.CanSendNow || !_queuedMessages.Contains(message)) return;
            var ownsDraft = ComposerText == message.Text
                && (_composerEdits == _recallEdits || _composerEdits == message.ComposerEdits);
            _queuedMessages.Remove(message);
            _unechoed.Remove(message);
            if (ReferenceEquals(_lastSent, message)) _lastSent = null;
            RefreshQueue();
            _queuedSendBusy.OnNext(true);
            RefreshQueuedSend();
            ChatSendOutcome? outcome;
            QueuedChatMessage? replacement = null;
            try {
                outcome = await SendComposerAsync(
                    message.Text, includeTray: false, bypassRecall: true, ownsDraft: ownsDraft,
                    onQueued: row => {
                        replacement = row;
                        RetargetSuperseded(message, row);
                        _superseded.Add((message, row));
                    });
            } finally {
                // Teardown disposes this subject after cancelling the send. The await can resume
                // once that has happened, and OnNext on a disposed subject throws.
                if (!_lifetimeToken.IsCancellationRequested) {
                    _queuedSendBusy.OnNext(false);
                    RefreshQueuedSend();
                }
            }
            if (_lifetimeToken.IsCancellationRequested) return;
            if (outcome is null or ChatSendOutcome.Rejected) {
                _superseded.RemoveAll(pair => ReferenceEquals(pair.Old, message));
                if (replacement?.Acknowledged == true) message.Acknowledged = true;
                else if (replacement is not null) RetargetSuperseded(replacement, message);
                RestoreQueuedRow(message);
            }
        });
        _disposables.Add(SendQueuedCommand);

        // Discarding stops tracking a message; it cannot recall one the agent may already have.
        // A row no server holds is this pane's alone to drop.
        DiscardQueuedCommand = ReactiveCommand.CreateFromTask<QueuedChatMessage>(async message => {
            if (!message.CanDiscard || _discardBusy || !_queuedMessages.Contains(message)) return;
            if (!message.IsServerHeld) {
                RetireDiscarded(message);
                return;
            }
            if (message.DispatchId is not { } id) return;
            var queueKey = _queueKey;
            _discardBusy = true;
            RefreshQueuedSend();
            try {
                var discarded = await _input.DiscardQueuedAsync(id, _lifetimeToken);
                if (_lifetimeToken.IsCancellationRequested) return;
                if (discarded) {
                    if (_intakeNotice.Value == DiscardFailedNotice) Notice(null);
                    RetireDiscarded(message);
                } else if (_queueKey == queueKey) Notice(DiscardFailedNotice);
            } finally {
                _discardBusy = false;
                if (!_lifetimeToken.IsCancellationRequested) RefreshQueuedSend();
            }
        });
        _disposables.Add(DiscardQueuedCommand);

        InterruptCommand = ReactiveCommand.CreateFromTask(
            () => _input.InterruptAsync(_lifetimeToken), this.WhenAnyValue(x => x.ShowsInterrupt));
        _disposables.Add(InterruptCommand);

        OpenLinkCommand = ReactiveCommand.Create<string>(url => LinkPolicy.Open(_opener, url));
        _disposables.Add(OpenLinkCommand);

        OpenAttachmentCommand = ReactiveCommand.Create<UserAttachment>(OpenAttachment);
        _disposables.Add(OpenAttachmentCommand);

        // A code block's command goes out the composer's own path rather than straight to the
        // channel, so it is queued, recalled and cleared exactly as a typed prompt is. Whatever
        // draft is sitting there is replaced, which is what putting it in the composer means.
        // A send that is still uploading has not reached the channel, so the channel still reports
        // it can take text: without the command's own executing state a code block could start a
        // second send over the same tray, and whichever upload lands first takes the chips while
        // the other send is refused.
        var canRun = Observable.CombineLatest(
            _input.WhenAnyValue(i => i.CanAcceptText),
            this.WhenAnyValue(x => x.IsReadOnlyParticipant),
            SendCommand.IsExecuting,
            _queuedSendBusy,
            (can, readOnly, sending, busy) => can && !readOnly && !sending && !busy);
        RunCodeCommand = ReactiveCommand.CreateFromTask<string>(async text => {
            ComposerText = text;
            await SendCommand.Execute();
        }, canRun);
        _disposables.Add(RunCodeCommand);

        serverQueue?.ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(ApplyServerQueue).DisposeWith(_disposables);
    }

    /// Reconcile pending server dispatches; only a transcript echo confirms an own send.
    void ApplyServerQueue(IReadOnlyList<QueuedInputItem> items) {
        var listed = new HashSet<Guid>();
        // A retry can precede its original's first listing. Match both attempts in send order.
        var attempts = Tracked.Concat(_superseded.Select(pair => pair.Old))
            .Distinct().OrderBy(q => q.SendOrder).ToList();
        foreach (var item in items.OrderBy(item => item.DispatchedAt)) {
            // Empty ids cannot distinguish dispatches or establish ownership.
            if (item.DispatchId == Guid.Empty || _discardedDispatches.Contains(item.DispatchId)) continue;
            if (_senders?.HasReceipt(item.DispatchId) == true) {
                // The receipt can precede the first listing, but only an attempt present when
                // that message was observed can acquire its id. A later identical send cannot.
                if (_unboundReceipts.TryGetValue(item.DispatchId, out var candidates) && candidates.QueueKey == _queueKey) {
                    var received = candidates.Attempts.FirstOrDefault(q => attempts.Contains(q) && q.DispatchId is null
                        && !q.Acknowledged && SameSender(q, item) && q.MatchesText(item.Text));
                    if (received is not null) {
                        received.MarkQueued(item.DispatchId);
                        ConfirmAttributedDispatch(item.DispatchId, null);
                        _unboundReceipts.Remove(item.DispatchId);
                    }
                }
                continue;
            }
            if (!listed.Add(item.DispatchId)) continue;
            // Snapshots have no revision; listing a known own row must disable its retry.
            if (attempts.FirstOrDefault(q => q.DispatchId == item.DispatchId) is { } known) {
                if (!known.IsForeign) {
                    known.MarkQueued(item.DispatchId);
                    if (!known.Acknowledged && !Tracked.Contains(known)) _queuedMessages.Add(known);
                }
                continue;
            }
            var own = attempts.FirstOrDefault(q => q.DispatchId is null && !q.Acknowledged && SameSender(q, item) && q.MatchesText(item.Text));
            if (own is not null) {
                own.MarkQueued(item.DispatchId);
                if (!Tracked.Contains(own)) _queuedMessages.Add(own);
            } else _queuedMessages.Add(QueuedChatMessage.FromServer(item));
        }
        foreach (var gone in _queuedMessages.Where(q => q.IsForeign && q.DispatchId is { } id && !listed.Contains(id)).ToList())
            _queuedMessages.Remove(gone);
        foreach (var own in _queuedMessages.Where(q => !q.IsForeign && q.DispatchId is { } id && !listed.Contains(id)))
            own.MarkUnlisted();
        RefreshQueue();
    }

    static bool SameSender(QueuedChatMessage own, QueuedInputItem item) =>
        item.SenderUserId is { Length: > 0 } sender && own.SenderIdentity?.Owns(sender) == true;

    ReceiptCandidates CaptureReceiptCandidates() => new(_queueKey,
        Tracked.Concat(_superseded.Select(pair => pair.Old)).Distinct()
            .Where(q => q.DispatchId is null && !q.Acknowledged).OrderBy(q => q.SendOrder).ToArray());

    void OnSession(ChatSessionInfo info) {
        if (_isOwnedByCurrentUser != info.IsOwnedByCurrentUser) {
            _isOwnedByCurrentUser = info.IsOwnedByCurrentUser;
            this.RaisePropertyChanged(nameof(UserTitle));
        }
        ReadOnlyNotice = info.ReadOnlyNotice;
        IsReadOnlyParticipant = info.ReadOnlyNotice.Length > 0;
        TakesAttachments = info.TakesAttachments;
        _vendor = info.Vendor;
        _root = info.Root;
        _rootSubject.OnNext(info.Root);
        VendorLabel = HostedHarnessCatalog.LabelFor(_options, info.Vendor);
        ModelLabel = HostedHarnessCatalog.ModelLabelFor(info.Vendor, info.Model ?? "");
        ApplyAccount(info);
        _status = info.Status;
        _interruption = info.Interruption;
        if (_ended != info.Ended) {
            _ended = info.Ended;
            RaisePhaseNote();
        }
        _waitsOnUser = info.WaitsOnUser;
        if (info.Ended)
            foreach (var queued in Tracked.Where(q => !q.IsForeign)) queued.MarkUnconfirmed();
        _awaitingInput = info.AwaitingInput;
        _turnAttested = info.TurnAttested == true;
        _liveSubagents = info.LiveSubagents;
        _daemonWorkingSince = info.WorkingSince;
        if (!_feedReleased) _runs.SessionOver = info.Ended;
        if (_planActivity is not null) _planActivity.SessionOver = info.Ended;
        // A foreign row is the server's answer for one session. Moving to another — or to none,
        // where no snapshot can ever arrive to retire it — leaves nothing to keep it honest.
        if (info.FeedKey != _queueKey) {
            _queueKey = info.FeedKey;
            foreach (var foreign in _queuedMessages.Where(q => q.IsForeign).ToList()) _queuedMessages.Remove(foreign);
        }
        var switched = _openFeed is { } open && info.FeedKey is { } key && key != _feedKey;
        if (switched) SwitchFeed(info.FeedKey!, _openFeed!);
        ApplyTerminalMenu(info.UsageLimit, info.TerminalDialog);
        RefreshActivityNote();
        if (!switched) RefreshQueue();
    }

    void ApplyAccount(ChatSessionInfo info) {
        var directory = info.AccountDirectory;
        AccountVendor = info.Vendor;
        AccountDirectory = directory;
        AccountLabel = string.IsNullOrWhiteSpace(info.AccountLabel) && directory is not null ? AccountRows.LastSegment(directory) : info.AccountLabel;
        ShowsAccountStrip = directory is not null
            && (string.Equals(info.Vendor, HarnessId.Claude.VendorId, StringComparison.OrdinalIgnoreCase)
             || string.Equals(info.Vendor, HarnessId.Codex.VendorId, StringComparison.OrdinalIgnoreCase));
        _accountIds.OnNext(info.AccountId);
    }

    /// A panel command such as /status writes nothing to the transcript, so the panel opening
    /// right after it at an idle prompt is the only receipt it leaves. Any later send, or one
    /// made mid-turn, could be what the dialog answers instead.
    void RetireCommandThatOpened() {
        var latest = Tracked.FirstOrDefault(q => !q.IsForeign && q.SendOrder == _nextSendOrder);
        if (latest is { Acknowledged: false, SentIdle: true, IsSlashCommand: true }) Acknowledge(latest);
    }

    /// A hook-driven card already asks the same question as the dialog it raised, so the screen's
    /// copy stays out of the way while one is pending.
    void ApplyTerminalMenu(UsageLimitNoticeDto? limit, TerminalDialogDto? dialog) {
        var question = UsageLimitNoticeDto.IsQuestion(limit);
        var shown = !question && dialog is not null && !HasPendingCards;
        if (Equals(_usageLimit, limit) && Equals(_terminalDialog, dialog) && _terminalDialogShown == shown) return;
        // Cursor redraws and pending cards hiding the dialog do not make a new menu. Keep an
        // answered menu closed until the underlying menu changes or leaves the screen.
        var sameMenu = question
            ? Equals(_usageLimit, limit)
            : dialog is not null && _terminalDialog is { } previous
                && previous.Heading == dialog.Heading && previous.Options.SequenceEqual(dialog.Options);
        if (dialog is not null && _terminalDialog is null) RetireCommandThatOpened();
        _usageLimit = limit;
        _terminalDialog = dialog;
        _terminalDialogShown = shown;
        foreach (var old in _terminalMenuChoices) old.Choose.Dispose();
        if (!sameMenu) SetTerminalMenuChoiceTaken(false);
        TerminalMenuError = "";
        List<(string Label, byte[] Keys)> choices = [];
        (TerminalMenuTitle, TerminalMenuHeading, TerminalMenuSummary, TerminalMenuPrompt, TerminalMenuScreen, TerminalMenuHint) =
            ("", "", "", "", "", "");
        if (question) {
            TerminalMenuSummary = limit!.Summary;
            TerminalMenuPrompt = limit.Prompt;
            TerminalMenuHint = "Choose how to handle the usage limit. A message here would be typed into that menu.";
            choices = limit.Options.Where(o => o.Index is >= 1 and <= 9)
                .Select(o => (o.Label, new[] { (byte)('0' + o.Index) })).ToList();
        } else if (shown && !_input.CanSendKeys) {
            // This channel cannot type into the dialog, so the card sends the viewer to the terminal.
            TerminalMenuTitle = "Waiting for you in Terminal";
            TerminalMenuHint = "Answer the dialog in the agent's terminal. The chat continues once it closes.";
            TerminalMenuScreen = dialog!.Screen;
        } else if (shown) {
            TerminalMenuTitle = "Waiting for you in Terminal";
            TerminalMenuHint = "Answer the terminal dialog first. A message here would be typed into it.";
            if (dialog!.Options.Count > 0) {
                TerminalMenuHeading = dialog.Heading;
                TerminalMenuSummary = dialog.Body;
                choices = dialog.Options
                    .Select((label, i) => (label, TerminalInputEncoder.ChooseMenuOption(dialog.Selected, i))).ToList();
            } else {
                TerminalMenuScreen = dialog.Screen;
                choices = [("Close", new byte[] { 0x1b })];
            }
        }
        HasTerminalMenu = question || dialog is not null;
        ShowsTerminalMenu = question || shown;
        TerminalMenuChoices = choices.Select(choice => new TerminalMenuChoiceViewModel(
                choice.Label, choice.Keys,
                ReactiveCommand.CreateFromTask(
                    () => ChooseTerminalMenuAsync(choice.Keys),
                    this.WhenAnyValue(x => x.TerminalMenuChoicesOpen))))
            .ToList();
    }

    void SetTerminalMenuChoiceTaken(bool taken) {
        if (_terminalMenuChoiceTaken == taken) return;
        _terminalMenuChoiceTaken = taken;
        this.RaisePropertyChanged(nameof(TerminalMenuChoicesOpen));
    }

    // The keys are written before the menu leaves the screen, so another click would land on
    // the next prompt. A failed send is the only reason to try the same menu again.
    async Task ChooseTerminalMenuAsync(byte[] keys) {
        if (_terminalMenuChoiceTaken) return;
        SetTerminalMenuChoiceTaken(true);
        TerminalMenuError = "";
        var sent = await _input.SendKeysAsync(keys, _lifetimeToken);
        if (sent || _lifetimeToken.IsCancellationRequested) return;
        TerminalMenuError = "The terminal is not attached, so that choice was not sent.";
        SetTerminalMenuChoiceTaken(false);
    }

    void SwitchFeed(string key, Func<string, IChatTranscriptFeed> open) {
        // A released tab's runs belong to the monitor that took its feed; this tab reads nothing more.
        if (_feedReleased) return;
        _items.Clear();
        _history.Clear();
        _literalInputHistory.Clear();
        _unechoed.Clear();
        _superseded.Clear();
        _pendingTools.Clear();
        _settledTools.Clear();
        _questionAsks.Clear();
        _openGroup = null;
        _openShell = null;
        _marked.Clear();
        _runs.Clear();
        _planActivity?.Clear();
        _senders?.Clear();
        _observedReceipts.Clear();
        _unboundReceipts.Clear();
        _feedKey = key;
        var previous = _lease;
        _lease = new FeedLease(open(key), Interlocked.Increment(ref _generation), SignalFeedRead);
        HasSeed = false;
        previous?.Dispose();
        RebaseQueuedMessages(CurrentOffset);
        _failureNote = null;
        var wasWaiting = _phase == ChatTabPhase.Waiting;
        Phase = ChatTabPhase.Waiting;
        // The rows are gone, so the view has to re-read what stands in for them even when the phase
        // is unchanged — and only then, since the setter itself raises the note on a real change.
        if (wasWaiting) RaisePhaseNote();
        SyncPendingCardItems();
        OnTick();
    }

    void RebaseQueuedMessages(long? offset) {
        _inputGeneration++;
        foreach (var queued in Tracked.Where(q => !q.IsForeign)) queued.Rebase(_inputGeneration, offset);
        foreach (var (old, replacement) in _superseded) {
            old.Rebase(_inputGeneration, offset);
            replacement.Rebase(_inputGeneration, offset);
        }
        RefreshQueue();
    }

    void ClearSentDraft(QueuedChatMessage queued) {
        if (_composerEdits == queued.ComposerEdits && ComposerText == queued.Text) ComposerText = "";
    }

    void RestoreQueuedRow(QueuedChatMessage message, bool isPredecessor = false) {
        if (message.Acknowledged) return;
        if (!_queuedMessages.Contains(message)) {
            var next = isPredecessor ? _queuedMessages.FirstOrDefault(q => !q.IsForeign && q.SendOrder > message.SendOrder) : null;
            if (next is null) _queuedMessages.Add(message);
            else _queuedMessages.Insert(_queuedMessages.IndexOf(next), message);
        }
        if (!_unechoed.Contains(message)) {
            var next = isPredecessor ? _unechoed.FindIndex(q => q.SendOrder > message.SendOrder) : -1;
            if (next < 0) _unechoed.Add(message);
            else _unechoed.Insert(next, message);
        }
        if (!isPredecessor) _lastSent ??= message;
        RefreshQueue();
    }

    void RetireDiscarded(QueuedChatMessage message) {
        var predecessors = _superseded
            .Where(pair => ReferenceEquals(pair.Replacement, message) && !pair.Old.Acknowledged)
            .Select(pair => pair.Old).Distinct().OrderBy(q => q.SendOrder).ToArray();
        if (message.DispatchId is { } id) {
            _discardedDispatches.Add(id);
            _queuedMessages.RemoveAll(_queuedMessages.Where(q => q.DispatchId == id).ToList());
        }
        _queuedMessages.Remove(message);
        _unechoed.Remove(message);
        _superseded.RemoveAll(pair => ReferenceEquals(pair.Old, message) || ReferenceEquals(pair.Replacement, message));
        if (ReferenceEquals(_lastSent, message)) _lastSent = null;
        foreach (var predecessor in predecessors) {
            _queuedMessages.Remove(predecessor);
            _unechoed.Remove(predecessor);
            RestoreQueuedRow(predecessor, isPredecessor: true);
        }
        RefreshQueue();
    }

    void Acknowledge(QueuedChatMessage queued) {
        queued.Acknowledged = true;
        _queuedMessages.Remove(queued);
        ConfirmDelivery(queued);
    }

    // Text alone cannot distinguish a retry from its predecessor. The server's attribution
    // identifies the dispatch whose transcript receipt it actually matched.
    void ConfirmAttributedDispatch(Guid dispatchId, SenderLabel? label) {
        var attempts = Tracked.Concat(_superseded.Select(pair => pair.Old)).Distinct()
            .Where(queued => queued.DispatchId == dispatchId).ToArray();
        if (attempts.Length == 0 && label is not null && _observedReceipts.TryGetValue(label, out var candidates)
            && candidates.Attempts.Length > 0) _unboundReceipts.TryAdd(dispatchId, candidates);
        foreach (var queued in attempts) {
            if (!queued.Acknowledged) Acknowledge(queued);
            AbsorbSuperseded(queued);
        }
        RefreshQueue();
    }

    // Every predecessor follows the newest retry's ordering and transcript boundary. On refusal,
    // only surviving links return to the previous attempt; an echo already consumed stays consumed.
    void RetargetSuperseded(QueuedChatMessage previous, QueuedChatMessage replacement) {
        for (var i = 0; i < _superseded.Count; i++) {
            var pair = _superseded[i];
            if (ReferenceEquals(pair.Replacement, previous)) _superseded[i] = (pair.Old, replacement);
        }
    }

    // A server-held retry cannot claim its known predecessor's delayed echo. Otherwise the
    // replacement owns the first eligible echo; the predecessor absorbs a further copy only
    // when no later send can claim it. Transcript offsets cannot identify a dispatch.
    bool TakeSupersededEcho(string text, long offset) {
        for (var i = 0; i < _superseded.Count; i++) {
            var (old, replacement) = _superseded[i];
            if (old.Acknowledged || !old.Matches(text, _inputGeneration, offset)) continue;
            if (replacement.Baseline is { } fence && offset < fence) {
                AbsorbSuperseded(old);
                return true;
            }
            // A known server predecessor can echo after its removal snapshot and after a
            // retry was queued. That echo cannot prove delivery of a retry the server still holds.
            if (old.DispatchId is not null && replacement.IsServerHeld) {
                Acknowledge(old);
                AbsorbSuperseded(old);
                return true;
            }
            if (!replacement.Acknowledged && Tracked.Contains(replacement)
                && replacement.Matches(text, _inputGeneration, offset)) {
                Acknowledge(replacement);
                return true;
            }
            if (replacement.Acknowledged) {
                if (ClaimEchoForLaterSend(replacement, text, offset)) return true;
                AbsorbSuperseded(old);
                return true;
            }
        }
        return false;
    }

    // The earliest send handed to the channel after the replacement owns this line.
    // A row already queued before that resend keeps waiting.
    bool ClaimEchoForLaterSend(QueuedChatMessage replacement, string text, long offset) {
        QueuedChatMessage? earliest = null;
        foreach (var queued in Tracked) {
            if (queued.Acknowledged || queued.IsForeign || queued.SendOrder <= replacement.SendOrder) continue;
            if (!queued.Matches(text, _inputGeneration, offset)) continue;
            if (earliest is null || queued.SendOrder < earliest.SendOrder) earliest = queued;
        }
        if (earliest is null) return false;
        Acknowledge(earliest);
        return true;
    }

    void AbsorbSuperseded(QueuedChatMessage old) {
        old.Acknowledged = true;
        _superseded.RemoveAll(pair => ReferenceEquals(pair.Old, old));
    }

    async Task<ChatSendOutcome?> SendComposerAsync(string snapshot, bool includeTray, bool bypassRecall, bool ownsDraft = true, Action<QueuedChatMessage>? onQueued = null) {
        var edits = _composerEdits;
        // An unchanged recall is already queued; typing the same words is a separate send.
        if (!bypassRecall) {
            var already = edits == _recallEdits
                ? _queuedMessages.FirstOrDefault(q =>
                    !q.IsForeign && !q.Acknowledged && q.AttachmentIds.Count == 0 && q.MatchesText(snapshot))
                : null;
            if (already is not null && Tray.Snapshot().Count == 0) {
                if (_composerEdits == edits && ComposerText == snapshot) ComposerText = "";
                return null;
            }
        }
        var draftRevision = ownsDraft ? _drafts?.Revision(_origin, _agentId) ?? 0 : 0;
        var files = includeTray ? Tray.Snapshot() : [];
        if (files.Count > 0 && !Attachments.CanAttach) { Notice(Attachments.AttachHint); return null; }
        IReadOnlyList<string> ids = [];
        if (files.Count > 0) {
            // The bytes have to be on the server before the prompt names them: a prompt that
            // arrives first points the agent at ids the store has never seen.
            UploadingFiles = files.Count;
            UploadOutcome upload;
            try { upload = await _uploader.UploadAsync(files, _lifetimeToken); }
            catch (OperationCanceledException) { return null; }
            finally { UploadingFiles = 0; }
            if (_lifetimeToken.IsCancellationRequested) return null;
            if (upload.Kind != UploadKind.Uploaded) {
                Notice(upload.Kind == UploadKind.Unauthorized ? "sign in to attach files" : upload.Reason ?? "the upload failed");
                return null;
            }
            ids = upload.Ids;
            // Re-asked after the upload: the window is long enough for the session to end or the
            // daemon to drop the capability, and the channel would otherwise refuse the send with
            // the chips still staged and nothing said about them.
            if (!Attachments.CanAttach) {
                Notice(Attachments.AttachHint ?? LocalFrameChatInput.AttachmentsRefused);
                return null;
            }
        }
        // The upload can finish after the menu is on screen. The text would be typed into
        // that menu, so the draft and the chips stay where they are. Or after the pane moved to a
        // feed not yet seeded; the draft waits for Send to open again.
        if (HasTerminalMenu || !CanSendBeforeSeed) return null;
        var chipIds = files.Select(f => f.Id).ToList();
        var sender = _input.SenderIdentity;
        var queued = new QueuedChatMessage(snapshot, ownsDraft ? edits : -1, _inputGeneration, CurrentOffset, chipIds) {
            SendOrder = ++_nextSendOrder,
            SentIdle = AgentIsIdle && _awaitingInput == true && !HasTerminalMenu,
            SenderIdentity = sender is null ? null : sender with { Ids = sender.Ids.ToArray() },
        };
        if (!queued.HasBaseline) Interlocked.Increment(ref _baselineVersion);
        _lastSent = queued;
        _sending = queued;
        _unechoed.Add(queued);
        if (_unechoed.Count > ComposerHistory.Capacity) _unechoed.RemoveAt(0);
        onQueued?.Invoke(queued);
        var delivered = _sessionId is { Length: > 0 } sid ? InputSending?.Invoke(sid) : null;
        ChatSendOutcome outcome;
        try { outcome = await _input.SendAsync(snapshot, ids, _lifetimeToken); }
        catch (OperationCanceledException) { outcome = ChatSendOutcome.Unconfirmed; }
        catch (Exception ex) {
            LogOnce($"send: {ex.Message}");
            outcome = ChatSendOutcome.Unconfirmed;
        }
        finally { _sending = null; }
        if (_lifetimeToken.IsCancellationRequested) {
            // The tab is gone, so nothing else will clear the stored copy of a delivered prompt.
            if (outcome == ChatSendOutcome.Accepted) {
                _drafts?.ClearDelivered(draftRevision);
                delivered?.Invoke();
            }
            return outcome;
        }
        if (outcome == ChatSendOutcome.Rejected) _unechoed.Remove(queued);
        else TurnSubmitted?.Invoke();
        var waits = !queued.Acknowledged && outcome switch {
            ChatSendOutcome.Accepted    => _openFeed is not null,
            ChatSendOutcome.Unconfirmed => true,
            _                           => false,
        };
        if (waits) {
            if (outcome == ChatSendOutcome.Unconfirmed) queued.MarkUnconfirmed();
            _queuedMessages.Add(queued);
        }
        RefreshQueue();
        if (outcome == ChatSendOutcome.Accepted) {
            ClearSentDraft(queued);
            Tray.RemoveAll(chipIds);
            delivered?.Invoke();
        }
        if (queued.Acknowledged) ConfirmDelivery(queued);
        return outcome;
    }

    void ConfirmDelivery(QueuedChatMessage queued) {
        if (ReferenceEquals(queued, _lastSent)) _input.ConfirmLastSend();
        ClearSentDraft(queued);
        Tray.RemoveAll(queued.AttachmentIds);
    }

    /// The one surface every intake source hands its result to.
    public IAttachmentSink Attachments => this;

    bool IAttachmentSink.CanAttach => _input.CanAttach && !IsReadOnlyParticipant && TakesAttachments;
    string? IAttachmentSink.AttachHint => TakesAttachments ? _input.AttachHint : NoAttachmentsHint;
    int IAttachmentSink.FreeSlots => Tray.FreeSlots;

    void IAttachmentSink.Accept(IntakeResult result) {
        var refused = Tray.AddAll(result.Accepted).Concat(result.Refused).ToList();
        Notice(RefusalNotice.Render(refused));
    }

    void SignalFeedRead(FeedLease lease) {
        Interlocked.Exchange(ref lease.Pending, 1);
        if (Interlocked.Exchange(ref lease.WakePosted, 1) != 0) return;
        Dispatcher.UIThread.Post(() => {
            Volatile.Write(ref lease.WakePosted, 0);
            if (ReferenceEquals(lease, _lease)) OnTick();
        });
    }

    void OnTick() {
        if (!Dispatcher.UIThread.CheckAccess()) {
            Dispatcher.UIThread.Post(OnTick);
            return;
        }
        if (_lifetimeToken.IsCancellationRequested) return;
        RefreshActivityNote();
        _runs.Tick();
        if (_lease is not { } lease) return;
        if (lease.Feed is RemoteTranscriptFeed && Volatile.Read(ref lease.Pending) == 0) return;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return;
        Interlocked.Exchange(ref lease.Pending, 0);
        _pendingRead = ReadAndApplyAsync(lease);
    }

    async Task ReadAndApplyAsync(FeedLease lease) {
        var baselineVersion = Volatile.Read(ref _baselineVersion);
        var inputGeneration = _inputGeneration;
        var canSkipEmpty = Phase == ChatTabPhase.Reading && _failureNote is null
            && Tracked.All(queued => queued.IsForeign || queued.HasBaseline);
        try {
            var read = lease.Feed is RemoteTranscriptFeed
                ? lease.Feed.ReadAppended()
                : await Task.Run(lease.Feed.ReadAppended).ConfigureAwait(false);
            if (canSkipEmpty && read.Status == FeedStatus.Ok && read.Lines.Count == 0 && read.Runs.Count == 0
                && baselineVersion == Volatile.Read(ref _baselineVersion) && inputGeneration == Volatile.Read(ref _inputGeneration)) return;
            await Dispatcher.UIThread.InvokeAsync(() => ApplyAsync(lease.Generation, read));
        } catch (Exception ex) {
            LogOnce($"read: {ex.Message}");
        } finally {
            Volatile.Write(ref _readInFlight, 0);
            if (_lease is { } current && (Volatile.Read(ref current.Pending) != 0
                || current.Feed is RemoteTranscriptFeed { HasPendingChanges: true }))
                SignalFeedRead(current);
        }
    }

    async Task ApplyAsync(int generation, FeedRead read) {
        // Projection may run in the background after Prepare enables Send. Those later sends
        // cannot acquire dispatches from the history this read is already replaying.
        var candidates = CaptureReceiptCandidates();
        if (!Prepare(generation, read)) return;
        if (read.Lines.Count == 0) {
            Finish(read, cursor: null, candidates);
            return;
        }
        // Building every row shares the UI thread with the switch cover's timer, so a long
        // transcript holds the launcher on screen until the replay returns.
        var cursor = LineCursor.Copy(this);
        if (CanProjectAway()) {
            var history = new HashSet<(long Offset, string Text)>(_literalInputHistory);
            cursor = await Task.Run(() => ProjectLines(read, cursor, matchSends: false, history));
        } else
            cursor = ProjectLines(read, cursor, matchSends: true, _literalInputHistory);
        if (generation != Volatile.Read(ref _generation)) return;
        Finish(read, cursor, candidates);
    }

    // A queued send is matched as the lines are replayed. An open group or shell is already on
    // screen, and a pending call is the same row a later result settles. Those stay on the UI
    // thread: settling raises the group's bound lists.
    bool CanProjectAway() =>
        _openGroup is null && _openShell is null && _superseded.Count == 0
        && _pendingTools.Count == 0
        && !Tracked.Any(static q => !q.IsForeign && !q.Acknowledged);

    bool Prepare(int generation, FeedRead read) {
        if (generation != Volatile.Read(ref _generation)) return false;

        switch (read.Status) {
            case FeedStatus.Missing:
                Phase = ChatTabPhase.Missing;
                return false;
            case FeedStatus.Failed:
                var reason = read.Failure ?? "read failed";
                LogOnce(reason);
                var changed = !string.Equals(_failureNote, reason, StringComparison.Ordinal);
                _failureNote = reason;
                // No rows on screen: the reason stands in for them. Rows already shown stay, with
                // the reason beneath them.
                if (_items.Count == 0) Phase = ChatTabPhase.Failed;
                if (changed) {
                    RaisePhaseNote();
                    RefreshActivityNote();
                }
                return false;
            case FeedStatus.Reset:
                HasSeed = true;
                // Skip everything the new source replays: it may be history, not receipts. The feed
                // names where that history ends — anything past it arrived after and can acknowledge.
                RebaseQueuedMessages(read.SnapshotOffset ?? CurrentOffset ?? 0);
                _items.Clear();
                _history.ClearForReplay();
                _pendingTools.Clear();
                _settledTools.Clear();
                _questionAsks.Clear();
                _openGroup = null;
                _openShell = null;
                _marked.Clear();
                _runs.Clear();
                _planActivity?.Clear();
                _senders?.Clear();
                _observedReceipts.Clear();
                break;
        }

        // Any read that is not a refusal clears one: how long a refusal lasts is the feed's to
        // say, and a feed whose refusal stands keeps answering with it.
        if (_failureNote is not null) {
            _failureNote = null;
            RefreshActivityNote();
        }
        // A send made before the transcript existed has no safe baseline. Its first successful
        // read establishes one; that initial history cannot acknowledge the send.
        var rebased = false;
        foreach (var queued in Tracked.Where(q => !q.HasBaseline && !q.IsForeign)) {
            queued.Rebase(_inputGeneration, read.SnapshotOffset ?? CurrentOffset ?? 0);
            rebased = true;
        }
        if (rebased) RefreshQueue();
        return true;
    }

    LineCursor ProjectLines(FeedRead read, LineCursor cursor, bool matchSends, HashSet<(long Offset, string Text)> history) {
        foreach (var (line, offset) in read.Lines) {
            var projected = line.RestoreSubmittedInputs(text => {
                if (!matchSends) return false;
                if (!Tracked.Any(q => !q.IsForeign && !q.Acknowledged && q.Matches(text, _inputGeneration, offset))
                    && !_superseded.Any(pair => !pair.Old.Acknowledged && pair.Old.Matches(text, _inputGeneration, offset))) return false;
                _literalInputHistory.Add((offset, text));
                return true;
            }, text => history.Contains((offset, text)));
            cursor.Restored.Add(projected);
            if (matchSends) {
                foreach (var text in projected.SubmittedInputs) {
                    if (TakeSupersededEcho(text, offset)) continue;
                    var acknowledged = Tracked.FirstOrDefault(q => !q.Acknowledged && q.Matches(text, _inputGeneration, offset));
                    if (acknowledged is null) continue;
                    Acknowledge(acknowledged);
                }
            }
            foreach (var e in projected.Envelopes) {
                switch (e.Kind) {
                    case AcpEventKind.UserMessage when e.ToolKind == ChatDisplayKind.Shell:
                        cursor.OpenGroup = null;
                        cursor.OpenShell = new ShellCommandItem(e.Text ?? "", SenderOf(e, offset));
                        cursor.Fresh.Add(cursor.OpenShell);
                        break;
                    case AcpEventKind.UserMessage:
                        cursor.OpenGroup = null;
                        cursor.OpenShell = null;
                        var (typed, files) = AttachmentTrailer.Split(e.Text ?? "");
                        cursor.Fresh.Add(new UserTurnItem(typed, [.. files.Select(file => UserAttachment.From(file, _origin == AgentOrigin.Local ? _root : null))],
                            SenderOf(e, offset)));
                        break;
                    case AcpEventKind.AssistantText:
                        cursor.OpenGroup = null;
                        cursor.OpenShell = null;
                        cursor.Fresh.Add(new AssistantTextItem(e.Text ?? ""));
                        break;
                    case AcpEventKind.AssistantThinking when !string.IsNullOrWhiteSpace(e.Text):
                        cursor.OpenGroup = null;
                        cursor.OpenShell = null;
                        cursor.Fresh.Add(new AssistantThinkingItem(e.Text));
                        break;
                    case AcpEventKind.SystemNote when e.ToolKind == ChatDisplayKind.Shell && cursor.OpenShell is { HasOutput: false } shell:
                        cursor.OpenGroup = null;
                        shell.Output = e.Text ?? "";
                        break;
                    case AcpEventKind.SystemNote when e.ToolKind == ChatDisplayKind.Shell:
                        cursor.OpenGroup = null;
                        cursor.OpenShell = null;
                        cursor.Fresh.Add(new ShellCommandItem("") { Output = e.Text ?? "" });
                        break;
                    case AcpEventKind.SystemNote:
                        cursor.OpenGroup = null;
                        cursor.OpenShell = null;
                        cursor.Fresh.Add(new SystemNoteItem(e.Text ?? "", e.ToolKind switch {
                            ChatDisplayKind.AgentReport    => SystemNoteItem.AgentReportLabel,
                            ChatDisplayKind.AgentMessage   => SystemNoteItem.AgentMessageLabel,
                            ChatDisplayKind.CompactSummary => SystemNoteItem.CompactSummaryLabel,
                            _                              => SystemNoteItem.NoteLabel,
                        }));
                        break;
                    case AcpEventKind.ToolCall: {
                        cursor.OpenShell = null;
                        var name = e.ToolName ?? "tool";
                        var entry = KcapToolCatalogue.Match(name);
                        var isToolSearch = entry is null && KcapToolCatalogue.IsToolSearch(name);
                        var category = entry?.Category ?? ToolSummary.Categorize(name, e.ToolInputJson);
                        var foreignLabel = entry is null && !isToolSearch ? KcapToolCatalogue.ForeignLabel(name) : null;
                        var detail = entry is not null ? ToolDetail.ForKey(e.ToolInputJson, entry.DetailKey)
                            : isToolSearch ? TextElision.End(KcapToolCatalogue.FormatToolSearchQuery(ToolDetail.RawForKey(e.ToolInputJson, "query")), 80)
                            : foreignLabel is not null ? FirstOrAny(e.ToolInputJson, category)
                            : ToolDetail.From(e.ToolInputJson, _root, category);
                        var label = entry?.Label ?? (isToolSearch ? "Looked up tools" : foreignLabel ?? ToolSummary.RowLabel(name, category, e.ToolInputJson));
                        var chip = foreignLabel is not null ? KcapToolCatalogue.ForeignServer(name) : null;
                        var item = new ToolCallItem(name, detail, category, label, entry?.Card ?? ToolCardKind.None, e.ToolInputJson, chip);
                        if (e.ToolCallId is { } id) {
                            cursor.Pending[id] = item;
                            if (name == ClaudeElicitation.ToolName) NoteQuestionCall(cursor, id, e.ToolInputJson);
                        }
                        if (cursor.OpenGroup is null) {
                            cursor.OpenGroup = new ToolGroupItem();
                            cursor.Fresh.Add(cursor.OpenGroup);
                        }
                        cursor.OpenGroup.Add(item);
                        break;
                    }
                    case AcpEventKind.ToolResult:
                        if (e.ToolCallId is not { } resultId) break;
                        cursor.Settled.Add(resultId);
                        NoteQuestionResult(cursor, resultId, e.TimestampIso);
                        if (cursor.Pending.Remove(resultId, out var call)) {
                            if (!e.ToolIsError && call.CardKind != ToolCardKind.None
                                && ToolCards.Build(call.CardKind, call.InputJson, e.ToolResult) is { } card)
                                call.SetCard(card, CardOpener(card));
                            call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                            if (!e.ToolIsError && _origin == AgentOrigin.Local && call.Category == ToolCategory.Read)
                                call.PreviewPath = ToolDetail.ResolvePreviewPath(ToolDetail.ImageFile(call.InputJson), _root);
                        }
                        break;
                }
            }
        }
        return cursor;
    }

    void Finish(FeedRead read, LineCursor? cursor, ReceiptCandidates candidates) {
        if (cursor is not null) {
            _openGroup = cursor.OpenGroup;
            _openShell = cursor.OpenShell;
            _pendingTools.Clear();
            foreach (var (id, call) in cursor.Pending) _pendingTools.Add(id, call);
            _settledTools.Clear();
            _settledTools.UnionWith(cursor.Settled);
            _questionAsks.Clear();
            _questionAsks.AddRange(cursor.Questions);
            _planActivity?.Apply(read.Lines.Select(line => line.Projection));
            _runs.ApplyAll(cursor.Restored);
            foreach (var projected in cursor.Restored)
                foreach (var text in projected.SubmittedInputs)
                    _history.Observe(AttachmentTrailer.Strip(text));
            if (cursor.Fresh.Count > 0) {
                _items.AddRange(cursor.Fresh);
                _senders?.Track(SenderLabels(cursor.Fresh, candidates));
            }
        }
        Phase = ChatTabPhase.Reading;
        ApplyFeedRuns(read);
        if (cursor is null) {
            if (read.Status == FeedStatus.Reset) {
                SyncPendingCardItems();
                RefreshActivityNote();
            }
            return;
        }
        RefreshQueue();
        Reconcile();
        SyncPendingCardItems();
        RefreshActivityNote();
    }

    /// A remote feed's offset is the line's number in the session's root stream; a local one's is a file
    /// position, so only the envelope's own keys name it there. A journaled message with no dispatch is the
    /// owner's own input and keeps the fallback.
    SenderLabel SenderOf(AcpEventEnvelope e, long offset) =>
        new(new MessageKeys(e.SourceEventId, _origin == AgentOrigin.Remote ? offset : null, e.DispatchId), Timestamp(e.TimestampIso));

    IEnumerable<SenderLabel> SenderLabels(IEnumerable<ChatItemViewModel> items, ReceiptCandidates? candidates = null) {
        foreach (var item in items) {
            var label = item is UserTurnItem turn ? turn.Sender
                : item is ShellCommandItem { HasCommand: true } shell ? shell.Sender : null;
            if (label is null) continue;
            if (!_observedReceipts.ContainsKey(label))
                _observedReceipts.Add(label, candidates ?? CaptureReceiptCandidates());
            yield return label;
        }
    }

    /// Test-only seam: the sender read in flight, or the last one started.
    internal Task? PendingSenderReadForTesting => _senders?.PendingReadForTesting;

    void ApplyFeedRuns(FeedRead read) {
        if (read.Runs.Count > 0) _runs.Apply(new ChatProjectionResult([], [], read.Runs));
    }

    /// The one group carrying the pack/suppress flags. Only the trailing group ever carries them,
    /// and it stops being the trailing group without any card changing, so it is cleared by
    /// identity rather than by sweeping every row on each drain.
    ToolGroupItem? _flaggedGroup;

    /// The group a question card has stood in for, so the row can come back the moment that card
    /// retires rather than waiting for the transcript to carry the answer.
    ToolGroupItem? _questionCardHost;

    /// Cards ride the same virtualizing list as the thread, always last, so a path switch or a
    /// transcript reset cannot bury them in the middle of replayed rows.
    void SyncPendingCardItems() {
        var cards = PendingCards;
        var start = _items.Count;
        while (start > 0 && _items[start - 1] is PendingCardItem) start--;
        var trailingGroup = start > 0 && _items[start - 1] is ToolGroupItem g ? g : null;
        var questionPending = cards.Any(static c => c is QuestionCardViewModel or AcpQuestionCardViewModel);
        if (questionPending && trailingGroup is not null) _questionCardHost = trailingGroup;
        var suppressGroup = trailingGroup is not null && HidesBehindQuestionCard(trailingGroup, questionPending);

        if (!TrailingCardsMatch(start, cards)) {
            for (var i = _items.Count - 1; i >= 0; i--)
                if (_items[i] is PendingCardItem) _items.RemoveAt(i);
            var first = true;
            foreach (var card in cards) {
                _items.Add(new PendingCardItem(card, packsWithPrevious: first && trailingGroup is not null && !suppressGroup));
                first = false;
            }
        }

        if (!ReferenceEquals(_flaggedGroup, trailingGroup) && _flaggedGroup is { } stale) {
            stale.PacksWithCard = false;
            stale.SuppressedForPendingQuestion = false;
        }
        _flaggedGroup = trailingGroup;
        if (trailingGroup is not null) {
            trailingGroup.PacksWithCard = cards.Count > 0 && !suppressGroup;
            trailingGroup.SuppressedForPendingQuestion = suppressGroup;
        }
    }

    /// The centered card is where a question is answered, so the left card for the same call is
    /// chrome while the question is live. Hiding keys off the call, not off the card: the card
    /// lands a round trip later, and waiting for it shows the left card only to take it away
    /// again. Two cases hand the row back — an ended session, which is getting no card at all,
    /// and a card that has already retired, after which the row is the only record of the call
    /// until the transcript carries its result.
    bool HidesBehindQuestionCard(ToolGroupItem group, bool questionPending) =>
        _input.Availability != SendAvailability.Ended
        && (questionPending || !ReferenceEquals(_questionCardHost, group))
        && group.Calls.Any(static c => c.Category == ToolCategory.Question && !c.IsSettled);

    bool TrailingCardsMatch(int start, ReadOnlyObservableCollection<PendingCardViewModel> cards) {
        if (_items.Count - start != cards.Count) return false;
        for (var i = 0; i < cards.Count; i++)
            if (!ReferenceEquals(((PendingCardItem)_items[start + i]).Card, cards[i])) return false;
        return true;
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
    /// (the vendor's own terminal prompt), so this tab is the one party that can retire it.
    /// AskUserQuestion's hook carries no tool-use id, so that card matches the latest ask with
    /// the same question text, and only once that ask's result is at or after the card. A card
    /// whose requested time did not parse is left: that stamp is not an ordering. Sent once per
    /// request; a failed send reopens it and retries on a bounded backoff.
    void WithdrawSettled() {
        if (_lifetimeToken.IsCancellationRequested) return;
        foreach (var request in _requests.Values) {
            if (!ShouldWithdraw(request) || !_withdrawing.Add(request.Key)) continue;
            _ = WithdrawAsync(request);
        }
    }

    bool ShouldWithdraw(PendingPermissionRequest request) {
        if (request.ToolUseId is { } id && _settledTools.Contains(id)) return true;
        if (!request.IsQuestion || request.RequestedAt == DateTimeOffset.MinValue) return false;
        AskedQuestion? latest = null;
        foreach (var ask in _questionAsks)
            if (request.OverlapsQuestion(ask.Fingerprints)) latest = ask;
        return latest is { SettledAt: { } settled } && request.RequestedAt <= settled;
    }

    void NoteQuestionCall(LineCursor cursor, string callId, string? inputJson) {
        if (cursor.Questions.Exists(a => a.CallId == callId)) return;
        if (PendingPermissionRequest.QuestionTexts(inputJson) is not { } texts) return;
        var ask = new AskedQuestion(callId, texts);
        if (cursor.Settled.Contains(callId)) ask.SettledAt = _time.GetUtcNow();
        cursor.Questions.Add(ask);
    }

    void NoteQuestionResult(LineCursor cursor, string callId, string? timestampIso) {
        var ask = cursor.Questions.LastOrDefault(a => a.CallId == callId);
        if (ask is null || ask.SettledAt is not null) return;
        ask.SettledAt = Timestamp(timestampIso) ?? _time.GetUtcNow();
    }

    sealed class LineCursor {
        public List<ChatItemViewModel> Fresh { get; } = [];
        public List<ChatProjectionResult> Restored { get; } = [];
        public ToolGroupItem? OpenGroup;
        public ShellCommandItem? OpenShell;
        public Dictionary<string, ToolCallItem> Pending { get; }
        public HashSet<string> Settled { get; }
        public List<AskedQuestion> Questions { get; }

        LineCursor(ToolGroupItem? openGroup, ShellCommandItem? openShell,
            Dictionary<string, ToolCallItem> pending, HashSet<string> settled, List<AskedQuestion> questions) {
            OpenGroup = openGroup;
            OpenShell = openShell;
            Pending = pending;
            Settled = settled;
            Questions = questions;
        }

        public static LineCursor Copy(ChatTabViewModel tab) => new(
            tab._openGroup,
            tab._openShell,
            new Dictionary<string, ToolCallItem>(tab._pendingTools, StringComparer.Ordinal),
            new HashSet<string>(tab._settledTools, StringComparer.Ordinal),
            new List<AskedQuestion>(tab._questionAsks));
    }

    static DateTimeOffset? Timestamp(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    sealed class AskedQuestion(string callId, HashSet<string> fingerprints) {
        public string CallId { get; } = callId;
        public HashSet<string> Fingerprints { get; } = fingerprints;
        public DateTimeOffset? SettledAt { get; set; }
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

    /// <summary>Gives up a local feed to whoever carries on reading it after this tab closes. Only between reads,
    /// so two readers never share it; null otherwise, and the caller reads afresh. The tab reads nothing after.</summary>
    public TranscriptFeedHandoff? ReleaseFeed() {
        if (_lease is not { Feed: LocalTranscriptFeed or LocalTranscripts.Reader } lease || _feedKey is not { } key) return null;
        if (Interlocked.CompareExchange(ref _readInFlight, 1, 0) != 0) return null;
        Interlocked.Increment(ref _generation);
        _feedReleased = true;
        _lease = null;
        return new TranscriptFeedHandoff(lease.Feed, key, _runs);
    }

    public Task TeardownAsync() {
        Interlocked.Increment(ref _generation);
        var lease = _lease;
        _lease = null;
        lease?.Dispose();
        _timer?.Dispose();
        _timer = null;
        _runs.Changed -= RefreshRuns;
        // Ahead of the disposables: the input is one of them, and an in-flight send has to see
        // the cancellation before the channel it is sending through goes away.
        foreach (var choice in _terminalMenuChoices) choice.Choose.Dispose();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _disposables.Dispose();
        Cards.Dispose();
        Tray.Clear();
        _rootSubject.Dispose();
        _intakeNotice.Dispose();
        _lifetime.Dispose();
        return Task.CompletedTask;
    }
}
