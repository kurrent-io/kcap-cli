using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// One of the daemon's special keys as a button: the wire token and what the button says.
public sealed record SpecialKeyChoice(string Key, string Label);

/// Maps a keystroke's terminal bytes onto the daemon's special-key vocabulary. Only an exact
/// sequence matches; a read-only pane sends nothing else through.
public static class SpecialKeyMapper {
    public static readonly IReadOnlyList<SpecialKeyChoice> Choices = [
        new(SpecialKeys.Escape, "Esc"), new(SpecialKeys.Tab, "Tab"), new(SpecialKeys.ShiftTab, "Shift+Tab"),
        new(SpecialKeys.Enter, "Enter"), new(SpecialKeys.CtrlC, "Ctrl+C"),
        new(SpecialKeys.ArrowUp, "↑"), new(SpecialKeys.ArrowDown, "↓"),
    ];

    public static string? Map(ReadOnlySpan<byte> bytes) => bytes switch {
        [0x1b] => SpecialKeys.Escape,
        [0x09] => SpecialKeys.Tab,
        [0x0d] or [0x0a] => SpecialKeys.Enter,
        [0x03] => SpecialKeys.CtrlC,
        [0x1b, (byte)'[', (byte)'A'] or [0x1b, (byte)'O', (byte)'A'] => SpecialKeys.ArrowUp,
        [0x1b, (byte)'[', (byte)'B'] or [0x1b, (byte)'O', (byte)'B'] => SpecialKeys.ArrowDown,
        [0x1b, (byte)'[', (byte)'Z'] => SpecialKeys.ShiftTab,
        _ => null,
    };
}
