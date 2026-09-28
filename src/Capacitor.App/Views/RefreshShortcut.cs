using Avalonia.Input;

namespace Capacitor.App.Views;

/// Command+R on macOS. Linux and Windows have no Command key, so the same action is Control+R
/// there. Control+R stays unbound on macOS: the terminal uses it for reverse-i-search.
static class RefreshShortcut {
    public static bool UsesControl { get; } = !OperatingSystem.IsMacOS();

    public static KeyGesture Primary { get; } = new(Key.R, UsesControl ? KeyModifiers.Control : KeyModifiers.Meta);

    public static string Label { get; } = UsesControl ? "Ctrl+R" : "⌘R";
}
