using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The card host for a session on another machine. DataContext is a RemoteSessionViewModel,
/// supplied by MainWindow's workspace slot; this view builds nothing of its own.
public partial class RemoteSessionView : UserControl {
    public RemoteSessionView() {
        InitializeComponent();
    }
}
