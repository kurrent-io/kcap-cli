namespace Capacitor.Cli.Daemon.Services;

/// Tracks DEC mouse and bracketed-paste modes across PTY chunks, ignoring escape sequences inside
/// terminal strings.
internal sealed class TerminalModeTracker {
    enum State { Ground, Escape, Csi, String, StringEscape }

    const int MouseModes     = 1 | 2 | 4 | 8;
    const int BracketedPaste = 16;

    readonly byte[] _csi = new byte[64];
    State _state;
    int _length;
    bool _osc;
    int _modes;

    public bool IsMouseReporting => (Volatile.Read(ref _modes) & MouseModes) != 0;

    public bool IsBracketedPasteEnabled => (Volatile.Read(ref _modes) & BracketedPaste) != 0;

    public void Feed(ReadOnlySpan<byte> bytes) {
        foreach (var b in bytes) {
            if (b is 0x18 or 0x1a) { _state = State.Ground; continue; }
            if (_state is State.String or State.StringEscape) {
                if ((_osc && b == 7) || (_state == State.StringEscape && b == '\\')) _state = State.Ground;
                else _state = b == 0x1b ? State.StringEscape : State.String;
                continue;
            }
            if (b == 0x1b) { _state = State.Escape; continue; }
            if (_state == State.Escape) {
                _state = State.Ground;
                if (b == '[') { _length = 0; _state = State.Csi; }
                else if (b is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^' or (byte)'X') {
                    _osc = b == ']';
                    _state = State.String;
                } else if (b == 'c') Volatile.Write(ref _modes, 0);
            } else if (_state == State.Csi) {
                if (b is >= 0x40 and <= 0x7e) {
                    Apply(b);
                    _state = State.Ground;
                } else if (b >= 0x20) {
                    if (_length == _csi.Length) _state = State.Ground;
                    else _csi[_length++] = b;
                }
            }
        }
    }

    void Apply(byte final) {
        var parameters = _csi.AsSpan(0, _length);
        if (final == 'p' && parameters.SequenceEqual("!"u8)) {
            Volatile.Write(ref _modes, 0);
            return;
        }
        if (final is not ((byte)'h' or (byte)'l') || parameters.IsEmpty || parameters[0] != '?') return;
        var modes = _modes;
        var value = 0;
        foreach (var b in parameters[1..]) {
            if (b == ';') { UpdateMode(ref modes, value, final == 'h'); value = 0; }
            else if (b is >= (byte)'0' and <= (byte)'9' && value <= 10000) value = value * 10 + b - '0';
            else return;
        }
        UpdateMode(ref modes, value, final == 'h');
        Volatile.Write(ref _modes, modes);
    }

    static void UpdateMode(ref int modes, int value, bool enabled) {
        var bit = value switch { 9 => 1, 1000 => 2, 1002 => 4, 1003 => 8, 2004 => BracketedPaste, _ => 0 };
        modes = enabled ? modes | bit : modes & ~bit;
    }
}
