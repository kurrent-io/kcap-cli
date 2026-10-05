using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Capacitor.App.Services;
using SvcSystems.UI.Terminal;

namespace Capacitor.App.Views;

/// Paste for a terminal pane. The platform paste chord is claimed on an ancestor while the
/// terminal holds focus, and the text is the composer's bracketed block, without a trailing
/// submit. Copy is left to the control.
static class TerminalClipboard {
    public static void Attach(Control host, TerminalControl terminal) =>
        host.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(terminal, e), RoutingStrategies.Tunnel);

    static void OnKeyDown(TerminalControl terminal, KeyEventArgs e) {
        if (e.Handled || terminal.Model is not { } || !terminal.IsKeyboardFocusWithin || !IsPaste(e)) return;
        e.Handled = true;
        _ = PasteAsync(terminal);
    }

    static bool IsPaste(KeyEventArgs e) =>
        Application.Current?.PlatformSettings?.HotkeyConfiguration.Paste.Any(g => g.Matches(e)) == true;

    internal static async Task PasteAsync(TerminalControl terminal) {
        if (TopLevel.GetTopLevel(terminal)?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text) || terminal.Model is not { } model) return;
        model.Send(TerminalInputEncoder.Paste(text));
    }
}
