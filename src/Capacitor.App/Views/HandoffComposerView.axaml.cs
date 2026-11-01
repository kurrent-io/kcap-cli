using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The ended session's composer. Enter is the text box's own newline: a resume or a hand-off starts
/// only from its button, never from a key typed into a prompt. Esc closes the hand-off settings the
/// user opened over Resume. DataContext is supplied by the host.
public partial class HandoffComposerView : UserControl {
    AttachmentDropPaste? _attachments;

    public HandoffComposerView() {
        InitializeComponent();
    }

    internal Task? PendingIntakeForTesting => _attachments?.PendingIntakeForTesting;

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.Key == Key.Escape && DataContext is HandoffComposerViewModel { CanCloseStrip: true } vm) {
            vm.CloseStrip();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// Paired with the visual tree, as the chat's composer is: a detached view comes back with it.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _attachments ??= AttachmentDropPaste.Attach(
            HandoffCard, HandoffInput, HandoffAttachButton,
            () => DataContext as HandoffComposerViewModel, TimeProvider.System);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _attachments?.Dispose();
        _attachments = null;
        base.OnDetachedFromVisualTree(e);
    }
}
