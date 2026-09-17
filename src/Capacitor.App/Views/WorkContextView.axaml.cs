using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The work-context pane. DataContext is the workspace's WorkContextViewModel, supplied by
/// WorkspaceView; this view builds nothing of its own.
public partial class WorkContextView : UserControl {
    public WorkContextView() {
        InitializeComponent();
    }

    // Drag only from empty chrome — never from the refresh button (or any other Button in the strip).
    void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e) {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        WindowChrome.BeginDrag(this, e);
    }

    async void OnSessionIdClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not WorkContextViewModel { CanCopySessionId: true, SessionIdText: { Length: > 0 } id }) return;
        await CopyTextAsync(sender as Control, id);
    }

    async void OnCopyTextClick(object? sender, RoutedEventArgs e) {
        if (sender is not Control { Tag: string { Length: > 0 } text } control) return;
        await CopyTextAsync(control, text);
    }

    async Task CopyTextAsync(Control? control, string text) {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(text);
        if (control is null) return;
        var previous = ToolTip.GetTip(control);
        ToolTip.SetTip(control, "Copied");
        ToolTip.SetIsOpen(control, true);
        void Restore(object? s, PointerEventArgs args) {
            control.PointerExited -= Restore;
            ToolTip.SetIsOpen(control, false);
            ToolTip.SetTip(control, previous);
        }
        control.PointerExited += Restore;
    }
}
