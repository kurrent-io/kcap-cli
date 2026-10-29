using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The workspace for a session on another machine. DataContext is a RemoteSessionViewModel,
/// supplied by MainWindow's workspace slot; this view builds nothing of its own.
public partial class RemoteSessionView : UserControl {
    IDisposable? _tabFocus;

    // The session header carries window chrome beside the work-context pane.
    void OnHeaderPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) =>
        WindowChrome.BeginDrag(this, e);

    public RemoteSessionView() {
        InitializeComponent();
        ContextPaneSizing.Attach(this, SplitGrid.ColumnDefinitions[1], ContextColumn, ContextSplitter,
            ContextToggleButton, ContextToggleIcon);
        TerminalClipboard.Attach(this, TerminalHost);
        TerminalHost.ViewportChanged += (cols, rows) => {
            if (DataContext is RemoteSessionViewModel model) model.Terminal?.SetViewport(cols, rows);
        };
        TerminalHost.PropertyChanged += (_, e) => {
            if (e.Property == SvcSystems.UI.Terminal.TerminalControl.ModelProperty && TerminalHost.Model is not null
                && DataContext is RemoteSessionViewModel { IsTerminalActive: true })
                TerminalHost.Focus();
        };
        DataContextChanged += (_, _) => {
            ArtefactsPane.Content = null;
            PullRequestPane.Content = null;
            ArmTabFocus();
            TerminalHost.ReportViewport();
        };
    }

    void OnTerminalKeyClick(object? sender, RoutedEventArgs e) {
        TerminalViewport.ShowTail();
        TerminalHost.Focus();
    }

    void ArmTabFocus() {
        _tabFocus?.Dispose();
        var model = DataContext as RemoteSessionViewModel;
        _tabFocus = model is null ? null : model
            .WhenAnyValue(vm => vm.ActiveTab, vm => vm.ShowsPanes)
            .Subscribe(pair => Dispatcher.UIThread.Post(() => {
                if (!ReferenceEquals(model, DataContext) || !(pair.Property2 || model is { ShowsTerminalPane: true })) return;
                if (pair.Property1 == RemoteTab.Artefacts && ArtefactsPane.Content is null && model is not null)
                    ArtefactsPane.Content = new ArtefactsView { DataContext = model.Artefacts };
                if (pair.Property1 == RemoteTab.PullRequest && PullRequestPane.Content is null && model?.PullRequests is { } pullRequests)
                    PullRequestPane.Content = new PullRequestReader { DataContext = pullRequests };
                if (PullRequestPane.Content is PullRequestReader reader) reader.IsVisible = pair.Property1 == RemoteTab.PullRequest;
                if (pair.Property1 == RemoteTab.Chat) ChatHost.FocusComposer();
                else if (pair.Property1 == RemoteTab.Terminal) TerminalHost.Focus();
            }, DispatcherPriority.Loaded));
    }

    /// A host that re-attaches this view with its DataContext unchanged raises no DataContextChanged.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        if (_tabFocus is null) ArmTabFocus();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _tabFocus?.Dispose();
        _tabFocus = null;
        base.OnDetachedFromVisualTree(e);
    }
}
