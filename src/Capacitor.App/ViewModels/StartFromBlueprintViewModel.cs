using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class StartFromBlueprintViewModel : ReactiveObject {
    bool _isSelected;

    public StartFromBlueprintViewModel(
            StartFromBlueprintKind kind,
            string title,
            string subtitle,
            string placeholder,
            WorktreeLaunchMode defaultWorktreeMode,
            Action<StartFromBlueprintViewModel>? onSelect = null) {
        Kind                = kind;
        Title               = title;
        Subtitle            = subtitle;
        Placeholder         = placeholder;
        DefaultWorktreeMode = defaultWorktreeMode;
        SelectCommand       = ReactiveCommand.Create(() => onSelect?.Invoke(this));
    }

    public StartFromBlueprintKind Kind                { get; }
    public string                 Title               { get; }
    public string                 Subtitle            { get; }
    public string                 Placeholder         { get; }
    public WorktreeLaunchMode     DefaultWorktreeMode { get; }

    public bool IsSelected {
        get => _isSelected;
        internal set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    public ReactiveCommand<Unit, Unit> SelectCommand { get; }
}
