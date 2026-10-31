using Lucide.Avalonia;
using System.ComponentModel;
using System.Reactive;
using Avalonia.Collections;
using Capacitor.App.Views;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One row of the Chat tab. Matched by DataTemplates on the concrete type.
public abstract class ChatItemViewModel : ReactiveObject { }

public sealed class UserTurnItem(string text, IReadOnlyList<UserAttachment>? attachments = null, SenderLabel? sender = null) : ChatItemViewModel {
    public string Text { get; } = text;
    public SenderLabel Sender { get; } = sender ?? new SenderLabel(default, null);
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public IReadOnlyList<UserAttachment> Attachments { get; } = attachments ?? [];
    public bool HasAttachments => Attachments.Count > 0;
}

public sealed class AssistantTextItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

/// The model's reasoning, rendered as collapsible muted Markdown in the assistant column.
public sealed class AssistantThinkingItem : ChatItemViewModel {
    public const string ThinkingLabel = "Thinking";

    readonly string? _peek;
    bool _isExpanded = true;

    public AssistantThinkingItem(string text) {
        Text = text;
        _peek = PeekFirstLine(text);
        ToggleCommand = ReactiveCommand.Create(Toggle);
    }

    public string Text { get; }

    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HeaderLabel));
        }
    }

    public string HeaderLabel =>
        _isExpanded || string.IsNullOrEmpty(_peek) ? ThinkingLabel : $"{ThinkingLabel} · {_peek}";

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public void Toggle() => IsExpanded = !IsExpanded;

    static string? PeekFirstLine(string text) {
        foreach (var raw in text.AsSpan().EnumerateLines()) {
            var line = raw.Trim().TrimStart("#>-* ").Trim();
            // A fence marker is not a preview: ``` becomes empty, ```python becomes the language.
            if (line.IsEmpty || line.StartsWith("```")) continue;
            var cleaned = line.ToString()
                .Replace("`", "", StringComparison.Ordinal)
                .Replace("**", "", StringComparison.Ordinal)
                .Trim();
            if (cleaned.Length == 0) continue;
            return TextElision.End(cleaned, 56);
        }
        return null;
    }
}

/// System-attributed text — a finished background task, a reconnect note, another agent's message
/// — never the user's speech.
public sealed class SystemNoteItem : ChatItemViewModel {
    public const string NoteLabel = "Note";
    public const string AgentReportLabel = "Subagent report";
    public const string AgentMessageLabel = "Agent message";
    public const string CompactSummaryLabel = "Conversation compacted";

    bool _isExpanded;

    public SystemNoteItem(string text, string label = NoteLabel) {
        Text = text;
        Label = label;
        // A compaction summary restates the whole conversation above it, so it starts folded.
        IsCollapsible = label == CompactSummaryLabel;
        _isExpanded = !IsCollapsible;
        ToggleCommand = ReactiveCommand.Create(Toggle);
    }

    public string Text { get; }
    public string Label { get; }
    public LucideIconKind IconKind => ToolCategoryIcons.ForFixedLabel(Label) ?? ToolCategoryIcons.NoteIcon;
    public bool IsCollapsible { get; }

    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ToggleAccessibleName));
        }
    }

    public string ToggleAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Label}";

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    public void Toggle() {
        if (IsCollapsible) IsExpanded = !IsExpanded;
    }
}

/// A bang command the user ran, and the output record that follows it. The output arrives later,
/// so it is filled in on the same item.
public sealed class ShellCommandItem : ChatItemViewModel {
    public ShellCommandItem(string command, SenderLabel? sender = null) {
        Command = command;
        Sender = sender ?? new SenderLabel(default, null);
    }

    public string Command { get; }
    public SenderLabel Sender { get; }
    public bool HasCommand => Command.Length > 0;

    string? _output;
    public string? Output {
        get => _output;
        internal set {
            if (_output == value) return;
            _output = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasOutput));
        }
    }

    public bool HasOutput => !string.IsNullOrEmpty(_output);
}

public enum ToolOutcome { Running, Done, Error }

public sealed class ToolCallItem(string name, string detail, ToolCategory category, string? label = null,
        ToolCardKind cardKind = ToolCardKind.None, string? inputJson = null, string? chip = null) : ChatItemViewModel {
    public string Name { get; } = name;
    /// Lone-call header when the category word ("Tool") would hide which server this is.
    public string? Chip { get; } = chip;
    public string Detail { get; } = detail;
    public ToolCategory Category { get; } = category;
    /// The verb phrase a row opens with; null for a question, whose row is the detail alone, and
    /// for a tool nothing names.
    public string? Label { get; } = label;
    public bool HasLabel => !string.IsNullOrEmpty(Label);
    public ToolCardKind CardKind { get; } = cardKind;
    /// Kept for the card built when the result arrives and for the opened row.
    internal string? InputJson { get; } = inputJson;

    /// What the row shows: detail when present, otherwise the tool name.
    public string LineText => string.IsNullOrEmpty(Detail) ? Name : Detail;

    /// True when the transcript carried a useful detail (brighter paint than a bare name).
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// A question's detail is the question itself — prose, read whole and wrapped. Every other
    /// detail is a command or a path, which the row keeps to one line and elides.
    public bool DetailIsProse => Category == ToolCategory.Question;

    ToolCard? _card;
    public ToolCard? Card {
        get => _card;
        private set {
            if (ReferenceEquals(_card, value)) return;
            _card = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasCard));
            this.RaisePropertyChanged(nameof(CanOpenCard));
        }
    }
    public bool HasCard => _card is not null;
    public bool CanOpenCard => OpenCardCommand is not null;
    public ReactiveCommand<Unit, Unit>? OpenCardCommand { get; private set; }

    /// Set before Outcome so a group sees the card the moment the call settles.
    public void SetCard(ToolCard card, Action? open) {
        OpenCardCommand = open is null ? null : ReactiveCommand.Create(open);
        this.RaisePropertyChanged(nameof(OpenCardCommand));
        Card = card;
    }

    ToolOutcome _outcome;
    /// Flipped in place when the matching tool_result arrives; a result is terminal.
    public ToolOutcome Outcome {
        get => _outcome;
        set {
            if (_outcome == value) return;
            _outcome = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(IsSettled));
            this.RaisePropertyChanged(nameof(IsRunning));
            InvalidateInspection();
        }
    }

    string? _result;
    /// The result text as the transcript carried it; set before Outcome.
    public string? Result {
        get => _result;
        internal set {
            if (_result == value) return;
            _result = value;
            this.RaisePropertyChanged();
            InvalidateInspection();
        }
    }

    bool _isAwaitingPermission;
    /// Owned by the permission cache, not the transcript: the two arrive in either order.
    public bool IsAwaitingPermission {
        get => _isAwaitingPermission;
        set {
            if (_isAwaitingPermission == value) return;
            _isAwaitingPermission = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(IsRunning));
            InvalidateInspection();
        }
    }

    bool _isExpanded;
    /// Kept on the item so a recycled row container comes back open or closed as the reader left it.
    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ToggleAccessibleName));
            InvalidateInspection();
        }
    }

    ReactiveCommand<Unit, Unit>? _toggleCommand;
    /// Created on first bind: a long chat holds thousands of rows and most are never opened.
    public ReactiveCommand<Unit, Unit> ToggleCommand => _toggleCommand ??= ReactiveCommand.Create(Toggle);

    public void Toggle() => IsExpanded = !IsExpanded;

    public string ToggleAccessibleName =>
        $"{(IsExpanded ? "Hide details" : "Show details")}: {string.Join(" ", new[] { Label, LineText }.Where(s => !string.IsNullOrEmpty(s)))}";

    ToolInspection? _inspection;
    /// Built only while the row is open, from what the item holds at that moment.
    public ToolInspection? Inspection =>
        _isExpanded ? _inspection ??= ToolInspection.Build(Name, InputJson, _result, _outcome, _isAwaitingPermission) : null;

    void InvalidateInspection() {
        if (!_isExpanded && _inspection is null) return;
        _inspection = null;
        this.RaisePropertyChanged(nameof(Inspection));
    }

    public bool IsSettled => _outcome != ToolOutcome.Running;
    public bool IsError => _outcome == ToolOutcome.Error;
    /// True while the call is in flight and not waiting on a permission prompt — drives the
    /// pulsing status pill so a live row is never blank.
    public bool IsRunning => _outcome == ToolOutcome.Running && !_isAwaitingPermission;

    string? _previewPath;
    public string? PreviewPath {
        get => _previewPath;
        set {
            if (_previewPath == value) return;
            _previewPath = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(HasPreview));
        }
    }
    public bool HasPreview => !string.IsNullOrEmpty(PreviewPath);

}

/// A run of consecutive tool calls. Settled calls fold into Summary when there are two or more
/// calls in the group; a lone call stays a single row with no "Ran a command" chrome.
/// VisibleCalls is the one list the view binds, swapped on toggle so a folded multi-call group
/// holds no containers for its settled rows.
public sealed class ToolGroupItem : ChatItemViewModel {
    readonly AvaloniaList<ToolCallItem> _calls = new();
    readonly AvaloniaList<ToolCallItem> _live = new();
    /// What a folded group shows: the live calls and every card, in call order.
    readonly AvaloniaList<ToolCallItem> _folded = new();

    public IAvaloniaReadOnlyList<ToolCallItem> Calls => _calls;
    public IAvaloniaReadOnlyList<ToolCallItem> LiveCalls => _live;
    public IAvaloniaReadOnlyList<ToolCallItem> VisibleCalls =>
        _calls.Count <= 1 || _isExpanded ? _calls : _folded;

    /// False when a multi-call group is folded and every call has settled — the list would
    /// otherwise still occupy a StackPanel slot under the summary.
    public bool HasVisibleCalls => VisibleCalls.Count > 0;

    bool _isExpanded;
    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            NotifyVisible();
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set => this.RaiseAndSetIfChanged(ref _hasSummary, value); }

    /// Summary chrome only when folding would hide something — a single call is the row itself.
    public bool ShowsSummaryHeader => _hasSummary && _calls.Count > 1;

    /// Lone-call cards have no summary phrase, so a kind chip names what the callout is.
    public bool ShowsKindChip => _calls.Count == 1;

    public string KindChip =>
        _calls.Count == 1 ? _calls[0].Chip ?? ToolSummary.ChipLabel(_calls[0].Category) : "";

    public LucideIconKind? HeaderIconKind {
        get {
            if (_calls.Count == 1) return ToolCategoryIcons.ForCategory(_calls[0].Category);
            var firstSettled = _calls.FirstOrDefault(c => c.IsSettled);
            if (firstSettled is null) return null;
            // Match the categories named by Summary; live rows are shown separately.
            var category = _calls.Any(c => c.IsSettled && c.Category != firstSettled.Category)
                ? ToolCategory.Other
                : firstSettled.Category;
            return ToolCategoryIcons.ForCategory(category);
        }
    }

    bool _hasFailure;
    public bool HasFailure { get => _hasFailure; private set => this.RaiseAndSetIfChanged(ref _hasFailure, value); }

    bool _packsWithCard;
    /// A live card answering this group sits in the next row; the view drops the paragraph gap
    /// so the two read as one block.
    public bool PacksWithCard {
        get => _packsWithCard;
        set => this.RaiseAndSetIfChanged(ref _packsWithCard, value);
    }

    bool _suppressedForPendingQuestion;
    /// While a question prompt is open, the centered card owns the turn — hide this system bubble.
    public bool SuppressedForPendingQuestion {
        get => _suppressedForPendingQuestion;
        set => this.RaiseAndSetIfChanged(ref _suppressedForPendingQuestion, value);
    }

    public ToolGroupItem() {
        ToggleCommand = ReactiveCommand.Create(Toggle);
    }

    public void Toggle() => IsExpanded = !IsExpanded;

    public void Add(ToolCallItem call) {
        _calls.Add(call);
        RefreshLoneChrome();
        this.RaisePropertyChanged(nameof(ShowsSummaryHeader));
        call.PropertyChanged += OnCallChanged;
        if (call.IsSettled) {
            if (call.HasCard) _folded.Add(call);
            Recompute();
            return;
        }
        _live.Add(call);
        _folded.Add(call);
        // After the add: a folded group's VisibleCalls is _folded, and HasVisibleCalls read before
        // the add would publish false for the call's whole run.
        NotifyVisible();
    }

    void RefreshLoneChrome() {
        this.RaisePropertyChanged(nameof(ShowsKindChip));
        this.RaisePropertyChanged(nameof(KindChip));
        this.RaisePropertyChanged(nameof(HeaderIconKind));
    }

    void OnCallChanged(object? sender, PropertyChangedEventArgs e) {
        if (sender is not ToolCallItem call) return;
        if (e.PropertyName == nameof(ToolCallItem.Card)) {
            if (call.HasCard && !_folded.Contains(call)) InsertInCallOrder(_folded, call);
            NotifyVisible();
            return;
        }
        if (e.PropertyName != nameof(ToolCallItem.Outcome) || !call.IsSettled) return;
        _live.Remove(call);
        if (!call.HasCard) _folded.Remove(call);
        Recompute();
    }

    void InsertInCallOrder(AvaloniaList<ToolCallItem> list, ToolCallItem call) {
        var index = _calls.IndexOf(call);
        var at = 0;
        while (at < list.Count && _calls.IndexOf(list[at]) < index) at++;
        list.Insert(at, call);
    }

    void Recompute() {
        var settled = _calls.Where(c => c.IsSettled).ToList();
        Summary = ToolSummary.Describe(settled.Select(c => c.Category));
        var failed = settled.Any(c => c.IsError);
        // Rising edge only: the reader can still collapse after this opens the error pills.
        if (failed && !_hasFailure) IsExpanded = true;
        HasFailure = failed;
        HasSummary = settled.Count > 0;
        this.RaisePropertyChanged(nameof(ShowsSummaryHeader));
        this.RaisePropertyChanged(nameof(HeaderIconKind));
        NotifyVisible();
    }

    void NotifyVisible() {
        this.RaisePropertyChanged(nameof(VisibleCalls));
        this.RaisePropertyChanged(nameof(HasVisibleCalls));
    }
}

/// A live prompt card sitting in the transcript list so it virtualizes with the thread.
public sealed class PendingCardItem(PendingCardViewModel card, bool packsWithPrevious = false) : ChatItemViewModel {
    public PendingCardViewModel Card { get; } = card;
    public bool PacksWithPrevious { get; } = packsWithPrevious;
}
