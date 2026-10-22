using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Maps named keys and bounded wheel reports onto the daemon input lane.
public static class SpecialKeyMapper {
    public static readonly IReadOnlyList<SpecialKeyChoice> Choices = [
        new(SpecialKeys.Escape, "Esc"), new(SpecialKeys.Tab, "Tab"), new(SpecialKeys.ShiftTab, "Shift+Tab"),
        new(SpecialKeys.Enter, "Enter"), new(SpecialKeys.CtrlC, "Ctrl+C"),
        new(SpecialKeys.ArrowUp, "↑"), new(SpecialKeys.ArrowDown, "↓"),
    ];

    public static string? Map(ReadOnlySpan<byte> bytes) {
        if (TerminalScrollKey.TryCreate(bytes, out var scroll)) return scroll;
        return bytes switch {
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
}
