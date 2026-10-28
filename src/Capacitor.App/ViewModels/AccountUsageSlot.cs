using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A row's usage, held by reference so a clock tick changes the view without rebuilding the row list.
public sealed class AccountUsageSlot : ReactiveObject {
    AccountUsageView? _view;

    public AccountUsageView? View {
        get => _view;
        set => this.RaiseAndSetIfChanged(ref _view, value);
    }
}
