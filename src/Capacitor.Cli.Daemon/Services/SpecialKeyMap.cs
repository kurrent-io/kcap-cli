using Capacitor.Remote.Models;

namespace Capacitor.Cli.Daemon.Services;

/// Encodes named keys and bounded wheel reports for the PTY; unknown inputs produce no bytes.
public static class SpecialKeyMap {
    public static byte[] ToBytes(string? key) {
        if (TerminalScrollKey.TryDecode(key, out var scroll)) return scroll;
        return key switch {
            "Escape"    => [0x1b],
            "Tab"       => [0x09],
            "Enter"     => [0x0d],
            "CtrlC"     => [0x03],
            "ArrowUp"   => [0x1b, 0x5b, 0x41],
            "ArrowDown" => [0x1b, 0x5b, 0x42],
            "ShiftTab"  => [0x1b, 0x5b, 0x5a],
            _           => []
        };
    }
}
