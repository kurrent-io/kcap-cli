using Avalonia.Input;

namespace Capacitor.App.Views;

/// ⌘R on macOS. Linux and Windows use Ctrl+R, and also Ctrl+Shift+R, which still refreshes
/// while the terminal has focus. Ctrl+R itself does not: that key is the terminal's
/// reverse-i-search. macOS leaves Ctrl+R unbound for the same reason.
static class RefreshShortcut {
    public static bool UsesControl { get; } = !OperatingSystem.IsMacOS();

    public static KeyGesture Primary { get; } = new(Key.R, UsesControl ? KeyModifiers.Control : KeyModifiers.Meta);

    public static KeyGesture? FromTerminal { get; } =
        UsesControl ? new KeyGesture(Key.R, KeyModifiers.Control | KeyModifiers.Shift) : null;

    public static string Label { get; } = UsesControl ? "Ctrl+R" : "⌘R";

    public static string? FromTerminalLabel { get; } = UsesControl ? "Ctrl+Shift+R" : null;
}
