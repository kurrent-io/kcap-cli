using Avalonia.Input;
using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

public partial class UpdateFinishWindow : ReactiveWindow<UpdateFinishViewModel> {
    public UpdateFinishWindow() {
        InitializeComponent();
    }

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
