using System.Reactive.Disposables.Fluent;
using Avalonia.Input;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

public partial class LifecyclePromptWindow : ReactiveWindow<LifecyclePromptViewModel> {
    public LifecyclePromptWindow() {
        InitializeComponent();

        this.WhenActivated(disposables => {
            ViewModel?.CloseRequested.Subscribe(_ => Close()).DisposeWith(disposables);
        });
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
