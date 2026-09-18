using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One ACP option, shared by a permission's own options and an ACP question's. Identity is
/// OptionId, not Label: two options may carry the same label and must stay distinct.
public sealed class AcpOptionViewModel : ReactiveObject {
    readonly Action _selectionChanged;
    bool _isSelected;

    public string OptionId { get; }
    public string Label { get; }
    public string? Description { get; }
    public ReactiveCommand<Unit, Unit> PickCommand { get; }

    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) return;
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            _selectionChanged();
        }
    }

    internal AcpOptionViewModel(string optionId, string label, string? description, Func<AcpOptionViewModel, Task> pick, IObservable<bool> idle, Action selectionChanged) {
        OptionId = optionId;
        Label = label;
        Description = description;
        _selectionChanged = selectionChanged;
        PickCommand = ReactiveCommand.CreateFromTask(() => pick(this), idle);
    }
}
