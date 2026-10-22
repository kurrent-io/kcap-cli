using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

public partial class UninstallWindow : ReactiveWindow<UninstallRunViewModel> {
    public UninstallWindow() {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm) vm.Lines.CollectionChanged += OnLinesChanged;
    }

    // Closing mid-run would quit with the CLI still removing things.
    protected override void OnClosing(WindowClosingEventArgs e) {
        if (e.CloseReason == WindowCloseReason.WindowClosing && ViewModel?.State == UninstallRunState.Running) e.Cancel = true;
        base.OnClosing(e);
    }

    void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(LinesScroll.ScrollToEnd, DispatcherPriority.Background);

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
