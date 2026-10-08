using Avalonia;
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
        TerminalClipboard.Attach(this, TerminalHost);
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null
                && DataContext is RemoteSessionViewModel { IsTerminalActive: true })
                TerminalHost.Focus();
        };
        DataContextChanged += (_, _) => {
            _tabFocus?.Dispose();
            ArtefactsPane.Content = null;
            var model = DataContext as RemoteSessionViewModel;
            _tabFocus = model?
                .WhenAnyValue(vm => vm.ActiveTab, vm => vm.ShowsPanes)
                .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                    if (!ReferenceEquals(model, DataContext) || !pair.Item2) return;
                    if (pair.Item1 == RemoteTab.Artefacts && ArtefactsPane.Content is null && model is not null)
                        ArtefactsPane.Content = new ArtefactsView { DataContext = model.Artefacts };
                    if (pair.Item1 == RemoteTab.Chat) ChatHost.FocusComposer();
                    else if (pair.Item1 == RemoteTab.Terminal) TerminalHost.Focus();
                }, DispatcherPriority.Loaded));
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _tabFocus?.Dispose();
        _tabFocus = null;
        base.OnDetachedFromVisualTree(e);
    }
}
