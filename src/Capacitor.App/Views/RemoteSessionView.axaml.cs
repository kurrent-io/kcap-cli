using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.ViewModels;
using ReactiveUI.Reactive;

namespace Capacitor.App.Views;

/// The workspace for a session on another machine. DataContext is a RemoteSessionViewModel,
/// supplied by MainWindow's workspace slot; this view builds nothing of its own.
public partial class RemoteSessionView : UserControl {
    IDisposable? _tabFocus;

    public RemoteSessionView() {
        InitializeComponent();
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            var model = DataContext as RemoteSessionViewModel;
            _tabFocus = model?
                .WhenAnyValue(vm => vm.ActiveTab, vm => vm.ShowsPanes)
                .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(model, DataContext) || !pair.Item2) return;
                    if (pair.Item1 == RemoteTab.Chat) ChatHost.FocusComposer();
                }, DispatcherPriority.Loaded));
        };
    }
}
