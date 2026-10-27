using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using ReactiveUI.Avalonia.Reactive;

namespace Capacitor.App.Views;

public partial class UninstallWindow : ReactiveWindow<UninstallRunViewModel> {
    bool _handoff;

    public UninstallWindow() {
        InitializeComponent();
    }

    // Shown by the original app before its first shutdown await. Destructive cleanup still
    // belongs to the relaunched copy, once the original app has released its resources.
    internal static UninstallWindow ShowHandoff(IEnumerable<Window> appWindows) {
        var previous = appWindows.ToArray();
        var window = new UninstallWindow { _handoff = true };
        window.RunContent.IsVisible = false;
        window.HandoffContent.IsVisible = true;
        window.Show();
        foreach (var other in previous) other.Hide();
        window.Activate();
        return window;
    }

    protected override void OnDataContextChanged(EventArgs e) {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm) vm.Lines.CollectionChanged += OnLinesChanged;
    }

    // Closing mid-run would quit with the CLI still removing things.
    protected override void OnClosing(WindowClosingEventArgs e) {
        if (e.CloseReason == WindowCloseReason.WindowClosing && (_handoff || ViewModel?.State == UninstallRunState.Running)) e.Cancel = true;
        base.OnClosing(e);
    }

    void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(LinesScroll.ScrollToEnd, DispatcherPriority.Background);

    void OnChromePointerPressed(object? sender, PointerPressedEventArgs e) => WindowChrome.BeginDrag(this, e);
}
