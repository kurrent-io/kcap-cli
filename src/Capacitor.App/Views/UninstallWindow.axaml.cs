using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

public partial class UninstallWindow : ReactiveWindow<UninstallRunViewModel> {
    UninstallRunViewModel? _observed;

    public UninstallWindow() {
        InitializeComponent();
    }

    internal static UninstallWindow ShowProgress(IEnumerable<Window> appWindows, UninstallRunViewModel run) {
        var previous = appWindows.ToArray();
        var window = new UninstallWindow { DataContext = run };
        window.Show();
        foreach (var other in previous) other.Hide();
        window.Activate();
        return window;
    }

    protected override void OnDataContextChanged(EventArgs e) {
        StopObserving();
        base.OnDataContextChanged(e);
        _observed = ViewModel;
        if (_observed is { } vm) vm.Lines.CollectionChanged += OnLinesChanged;
    }

    protected override void OnClosed(EventArgs e) {
        StopObserving();
        base.OnClosed(e);
    }

    void StopObserving() {
        if (_observed is { } vm) vm.Lines.CollectionChanged -= OnLinesChanged;
        _observed = null;
    }

    protected override void OnClosing(WindowClosingEventArgs e) {
        if (e.CloseReason == WindowCloseReason.WindowClosing && ViewModel?.IsBusy == true) e.Cancel = true;
        base.OnClosing(e);
    }

    void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(LinesScroll.ScrollToEnd, DispatcherPriority.Background);

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
