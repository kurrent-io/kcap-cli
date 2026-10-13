namespace Capacitor.App.Views;

/// Shortcut hints as the platform spells them: the key bindings answer Meta on macOS and Ctrl
/// elsewhere, and a ⌘ glyph means nothing on a Windows keyboard.
public static class ShortcutLabels {
    public static string NewSession { get; } = For("N", OperatingSystem.IsMacOS());

    public static string For(string key, bool isMacOs) => isMacOs ? $"⌘{key}" : $"Ctrl+{key}";
}
