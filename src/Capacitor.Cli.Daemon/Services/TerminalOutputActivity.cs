namespace Capacitor.Cli.Daemon.Services;

/// Distinguishes text written by a PTY from terminal housekeeping. Escape sequences can span
/// reads; their parameters and string payloads must never be mistaken for task output.
internal sealed class TerminalOutputActivity {
    enum State { Ground, Escape, EscapeIntermediate, Csi, String, StringEscape }

    State _state;
    bool _osc;

    public bool Observe(ReadOnlySpan<byte> bytes) {
        var text = false;
        foreach (var b in bytes) {
            if (b is 0x18 or 0x1a) { _state = State.Ground; continue; }
            if (_state is State.String or State.StringEscape) {
                if ((_osc && b == 7) || (_state == State.StringEscape && b == '\\')) _state = State.Ground;
                else _state = b == 0x1b ? State.StringEscape : State.String;
                continue;
            }
            if (b == 0x1b) { _state = State.Escape; continue; }
            switch (_state) {
                case State.Ground:
                    if (b > 0x20 && b != 0x7f) text = true;
                    break;
                case State.Escape:
                    if (b == '[') _state = State.Csi;
                    else if (b is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X') {
                        _osc = b == ']';
                        _state = State.String;
                    } else if (b is >= 0x20 and <= 0x2f) _state = State.EscapeIntermediate;
                    else if (b >= 0x30) _state = State.Ground;
                    break;
                case State.EscapeIntermediate:
                    if (b is >= 0x30 and <= 0x7e) _state = State.Ground;
                    break;
                case State.Csi:
                    if (b is >= 0x40 and <= 0x7e) _state = State.Ground;
                    break;
            }
        }
        return text;
    }
}
