using Avalonia;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The ended session's composer. Enter is the text box's own newline: a hand-off starts only from
/// the button, never from a key typed into a prompt. DataContext is supplied by the host.
public partial class HandoffComposerView : UserControl {
    AttachmentDropPaste? _attachments;

    public HandoffComposerView() {
        InitializeComponent();
    }

    internal Task? PendingIntakeForTesting => _attachments?.PendingIntakeForTesting;

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
