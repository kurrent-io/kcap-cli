using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Capacitor.App.Services;
using SvcSystems.UI.Terminal;

namespace Capacitor.App.Views;

/// Copy and paste for a terminal pane. The control turns Ctrl+C into an interrupt and Ctrl+V
/// into a control byte, and it clears any selection before either, so the gesture is claimed
/// on an ancestor while the terminal holds focus. A paste is the composer's bracketed block,
/// without the trailing submit.
static class TerminalClipboard {
    public static void Attach(Control host, TerminalControl terminal) =>
        host.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(terminal, e), RoutingStrategies.Tunnel);

    static void OnKeyDown(TerminalControl terminal, KeyEventArgs e) {
        if (e.Handled || terminal.Model is not { } model || !terminal.IsKeyboardFocusWithin) return;
        if (IsCopy(e) && terminal.HasSelection) {
            e.Handled = true;
            _ = terminal.CopySelectionAsync();
            model.ClearSelection();
            return;
        }
        if (!IsPaste(e)) return;
        e.Handled = true;
        _ = PasteAsync(terminal);
    }

    static bool IsCopy(KeyEventArgs e) =>
        Matches(Application.Current?.PlatformSettings?.HotkeyConfiguration.Copy, e)
        || e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control;

    static bool IsPaste(KeyEventArgs e) =>
        Matches(Application.Current?.PlatformSettings?.HotkeyConfiguration.Paste, e)
        || e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control;

    static bool Matches(IEnumerable<KeyGesture>? gestures, KeyEventArgs e) =>
        gestures?.Any(g => g.Matches(e)) == true;

    static async Task PasteAsync(TerminalControl terminal) {
        if (TopLevel.GetTopLevel(terminal)?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text) || terminal.Model is not { } model) return;
        model.Send(TerminalInputEncoder.Paste(text));
    }
}
