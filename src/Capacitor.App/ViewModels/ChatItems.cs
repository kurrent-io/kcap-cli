using System.ComponentModel;
using System.Reactive;
using Avalonia.Collections;
using Capacitor.App.Views;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One row of the Chat tab. Matched by DataTemplates on the concrete type.
public abstract class ChatItemViewModel : ReactiveObject { }

public sealed class UserTurnItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public sealed class AssistantTextItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

/// System-attributed text — a finished background task, a reconnect note — never anyone's speech.
public sealed class SystemNoteItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public enum ToolOutcome { Running, Done, Error }

public sealed class ToolCallItem(string name, string detail, ToolCategory category) : ChatItemViewModel {
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public ToolCategory Category { get; } = category;

    /// What the row shows: detail when present, otherwise the tool name.
    public string LineText => string.IsNullOrEmpty(Detail) ? Name : Detail;

    /// True when the transcript carried a useful detail (brighter paint than a bare name).
    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    /// A question's detail is the question itself — prose, read whole and wrapped. Every other
    /// detail is a command or a path, which the row keeps to one line and elides.
    public bool DetailIsProse => Category == ToolCategory.Question;

    ToolOutcome _outcome;
    /// Flipped in place when the matching tool_result arrives; a result is terminal.
    public ToolOutcome Outcome {
        get => _outcome;
        set {
            if (_outcome == value) return;
            _outcome = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(IsSettled));
            this.RaisePropertyChanged(nameof(IsRunning));
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
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
            this.RaisePropertyChanged(nameof(IsRunning));
        }
    }

    public bool IsSettled => _outcome != ToolOutcome.Running;
    public bool IsError => _outcome == ToolOutcome.Error;
    /// True while the call is in flight and not waiting on a permission prompt — drives the
    /// pulsing status pill so a live row is never blank.
    public bool IsRunning => _outcome == ToolOutcome.Running && !_isAwaitingPermission;
    public string OutcomeGlyph => _outcome switch {
        ToolOutcome.Done  => "✓",
        ToolOutcome.Error => "✕",
        _                 => _isAwaitingPermission ? "?" : "",
    };

}

/// A run of consecutive tool calls. Settled calls fold into Summary when there are two or more
/// calls in the group; a lone call stays a single row with no "Ran a command" chrome.
/// VisibleCalls is the one list the view binds, swapped on toggle so a folded multi-call group
/// holds no containers for its settled rows.
public sealed class ToolGroupItem : ChatItemViewModel {
    readonly AvaloniaList<ToolCallItem> _calls = new();
    readonly AvaloniaList<ToolCallItem> _live = new();

    public IAvaloniaReadOnlyList<ToolCallItem> Calls => _calls;
    public IAvaloniaReadOnlyList<ToolCallItem> LiveCalls => _live;
    public IAvaloniaReadOnlyList<ToolCallItem> VisibleCalls =>
        _calls.Count <= 1 || _isExpanded ? _calls : _live;

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

    /// What the header shows: the category phrase, plus a peek at the first settled detail while
    /// folded so the useful bit is visible without expanding.
    public string SummaryLine {
        get {
            if (_summary.Length == 0) return "";
            if (_isExpanded || PeekDetail() is not { Length: > 0 } peek) return _summary;
            return $"{_summary} · {peek}";
        }
    }

    bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set => this.RaiseAndSetIfChanged(ref _hasSummary, value); }

    /// Summary chrome only when folding would hide something — a single call is the row itself.
    public bool ShowsSummaryHeader => _hasSummary && _calls.Count > 1;

    /// Lone-call cards have no summary phrase, so a kind chip names what the callout is.
    public bool ShowsKindChip => _calls.Count == 1;

    public string KindChip =>
        _calls.Count == 1 ? ToolSummary.ChipLabel(_calls[0].Category) : "";

    public string HeaderIconData {
        get {
            if (_calls.Count == 1) return ToolCategoryIcons.ForCategory(_calls[0].Category);
            var firstSettled = _calls.FirstOrDefault(c => c.IsSettled);
            return firstSettled is null ? "" : ToolCategoryIcons.ForCategory(firstSettled.Category);
        }
    }

    /// The single call on a lone card — header status binds here.
    public ToolCallItem? LoneCall => _calls.Count == 1 ? _calls[0] : null;

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
        if (call.IsSettled) { Recompute(); return; }
        _live.Add(call);
        call.PropertyChanged += OnCallChanged;
        // After the add: a folded group's VisibleCalls is _live, and HasVisibleCalls read before
        // the add would publish false for the call's whole run.
        NotifyVisible();
    }

    void RefreshLoneChrome() {
        this.RaisePropertyChanged(nameof(ShowsKindChip));
        this.RaisePropertyChanged(nameof(KindChip));
        this.RaisePropertyChanged(nameof(HeaderIconData));
        this.RaisePropertyChanged(nameof(LoneCall));
    }

    void OnCallChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(ToolCallItem.Outcome) || sender is not ToolCallItem call || !call.IsSettled) return;
        call.PropertyChanged -= OnCallChanged;
        _live.Remove(call);
        Recompute();
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
        this.RaisePropertyChanged(nameof(HeaderIconData));
        NotifyVisible();
    }

    void NotifyVisible() {
        this.RaisePropertyChanged(nameof(VisibleCalls));
        this.RaisePropertyChanged(nameof(HasVisibleCalls));
        this.RaisePropertyChanged(nameof(SummaryLine));
    }

    /// First settled call's line, capped so the header stays one scannable row.
    string? PeekDetail() {
        var first = _calls.FirstOrDefault(c => c.IsSettled);
        if (first is null) return null;
        return first.DetailIsProse ? TextElision.End(first.LineText, 56) : TextElision.Middle(first.LineText, 56);
    }
}

/// A live prompt card sitting in the transcript list so it virtualizes with the thread.
public sealed class PendingCardItem(PendingCardViewModel card, bool packsWithPrevious = false) : ChatItemViewModel {
    public PendingCardViewModel Card { get; } = card;
    public bool PacksWithPrevious { get; } = packsWithPrevious;
}
