using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class WhatsNextLaneViewModel : ReactiveObject {
    public const int DefaultVisibleCap = 3;

    IReadOnlyList<WhatsNextCardViewModel> _allCards = [];
    IReadOnlyList<WhatsNextCardViewModel> _visibleCards = [];
    IReadOnlyList<WhatsNextCardViewModel>? _queuedCards;
    bool _isExpanded;
    int _pinCount;

    public WhatsNextLaneViewModel(string groupKey, string title) {
        GroupKey            = groupKey;
        Title               = title;
        ToggleExpandCommand = ReactiveCommand.Create(ToggleExpand);
    }

    public string GroupKey { get; }
    public string Title    { get; }

    public IReadOnlyList<WhatsNextCardViewModel> AllCards => _allCards;

    public IReadOnlyList<WhatsNextCardViewModel> VisibleCards {
        get => _visibleCards;
        private set => this.RaiseAndSetIfChanged(ref _visibleCards, value);
    }

    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            RecomputeVisible();
        }
    }

    public bool IsPinned => _pinCount > 0;
    public bool IsVisible => _visibleCards.Count > 0;
    public bool ShowMoreVisible => _allCards.Count > DefaultVisibleCap;
    public int HiddenCount => Math.Max(0, _allCards.Count - DefaultVisibleCap);
    public string ExpandButtonText => _isExpanded ? "Show less" : $"Show more ({HiddenCount})";

    public ReactiveCommand<Unit, Unit> ToggleExpandCommand { get; }

    public void Pin() => _pinCount++;

    public void Release() {
        if (_pinCount <= 0) return;
        _pinCount--;
        if (_pinCount == 0 && _queuedCards is { } queued) {
            _queuedCards = null;
            ApplyCards(queued);
        }
    }

    public void UpdateCards(IReadOnlyList<WhatsNextCardViewModel> cards) {
        if (_pinCount > 0 && _allCards.Count > 0) {
            _queuedCards = cards;
            return;
        }

        _queuedCards = null;
        ApplyCards(cards);
    }

    void ApplyCards(IReadOnlyList<WhatsNextCardViewModel> cards) {
        _allCards = cards;
        if (_allCards.Count <= DefaultVisibleCap && _isExpanded)
            _isExpanded = false;
        this.RaisePropertyChanged(nameof(AllCards));
        this.RaisePropertyChanged(nameof(IsExpanded));
        RecomputeVisible();
    }

    void ToggleExpand() => IsExpanded = !IsExpanded;

    void RecomputeVisible() {
        VisibleCards = _isExpanded || _allCards.Count <= DefaultVisibleCap
            ? _allCards
            : _allCards.Take(DefaultVisibleCap).ToList();
        this.RaisePropertyChanged(nameof(IsVisible));
        this.RaisePropertyChanged(nameof(ShowMoreVisible));
        this.RaisePropertyChanged(nameof(HiddenCount));
        this.RaisePropertyChanged(nameof(ExpandButtonText));
    }
}
