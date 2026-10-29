using System.Text;
using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Services;

/// A fixed viewport that applies the cursor and erase sequences a full-screen TUI uses to redraw.
/// Stripping those sequences and keeping the bytes leaves a menu on the scrollback after the
/// screen has overwritten it, so a match has to be read off the cells the cursor last wrote.
internal sealed class AnsiScreen {
    int _cols;
    int _rows;
    char[] _cells;
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    char[] _decoded = new char[4096];

    int _row;
    int _col;
    int _savedRow;
    int _savedCol;
    Mode _mode = Mode.Ground;
    bool _private;
    bool _paramStarted;
    int _param;
    readonly int[] _params = new int[8];
    int _paramCount;

    /// The grid is one cell per column and row. The product is the array length: an unchecked
    /// multiply wraps, and a size that does not fit is not a grid this screen can keep.
    internal const int MaxCells = 1 << 20;

    internal static bool Fits(int cols, int rows) {
        if (cols <= 0 || rows <= 0) return false;
        try { return checked(cols * rows) <= MaxCells; }
        catch (OverflowException) { return false; }
    }

    public AnsiScreen(int cols, int rows) {
        if (!Fits(cols, rows)) (cols, rows) = (PtyDefaults.Cols, PtyDefaults.Rows);
        _cols  = cols;
        _rows  = rows;
        _cells = new char[cols * rows];
        Array.Fill(_cells, ' ');
    }

    public int Cols => _cols;
    public int Rows => _rows;

    /// Follows the PTY to a new size. Cursor moves are relative to the width the TUI drew for, so
    /// a grid of any other size misplaces every redraw. Keeps the top-left overlap; the TUI
    /// repaints on the resize anyway.
    public void Resize(int cols, int rows) {
        if (cols == _cols && rows == _rows || !Fits(cols, rows)) return;
        var cells = new char[cols * rows];
        Array.Fill(cells, ' ');
        for (var row = 0; row < Math.Min(rows, _rows); row++)
            Array.Copy(_cells, row * _cols, cells, row * cols, Math.Min(cols, _cols));
        (_cols, _rows, _cells) = (cols, rows, cells);
        _row      = Math.Min(_row, rows - 1);
        _col      = Math.Min(_col, cols - 1);
        _savedRow = Math.Min(_savedRow, rows - 1);
        _savedCol = Math.Min(_savedCol, cols - 1);
    }

    public void Write(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) return;
        var need = bytes.Length;
        if (_decoded.Length < need) _decoded = new char[need];
        var count = _decoder.GetChars(bytes, _decoded, flush: false);
        for (var i = 0; i < count; i++) Feed(_decoded[i]);
    }

    public string Text() {
        var builder = new StringBuilder(_cells.Length);
        for (var row = 0; row < _rows; row++) {
            var end = _cols;
            var start = row * _cols;
            while (end > 0 && _cells[start + end - 1] == ' ') end--;
            if (end == 0) {
                builder.Append('\n');
                continue;
            }
            builder.Append(_cells, start, end);
            builder.Append('\n');
        }
        return builder.ToString().TrimEnd('\n');
    }

    void Feed(char c) {
        switch (_mode) {
            case Mode.Ground:
                Ground(c);
                break;
            case Mode.Esc:
                Esc(c);
                break;
            case Mode.Csi:
                Csi(c);
                break;
            case Mode.Osc:
                if (c == '\a') _mode = Mode.Ground;
                else if (c == '\x1b') _mode = Mode.OscEsc;
                break;
            case Mode.OscEsc:
                _mode = Mode.Ground;
                break;
        }
    }

    void Ground(char c) {
        switch (c) {
            case '\x1b':
                _mode = Mode.Esc;
                break;
            case '\r':
                _col = 0;
                break;
            case '\n':
                LineFeed();
                break;
            case '\b':
                if (_col > 0) _col--;
                break;
            default:
                if (c < ' ') return;
                if (_col >= _cols) { _col = 0; LineFeed(); }
                _cells[_row * _cols + _col] = c;
                _col++;
                break;
        }
    }

    void Esc(char c) {
        switch (c) {
            case '[':
                BeginCsi();
                break;
            case ']':
                _mode = Mode.Osc;
                break;
            case '(':
            case ')':
                _mode = Mode.OscEsc;
                break;
            default:
                _mode = Mode.Ground;
                break;
        }
    }

    void BeginCsi() {
        _mode = Mode.Csi;
        _private = false;
        _paramStarted = false;
        _param = 0;
        _paramCount = 0;
    }

    void Csi(char c) {
        // Any of < = > ? opens a private sequence; Claude Code's version query is CSI > 0 q.
        if (c is >= '<' and <= '?' && !_paramStarted && _paramCount == 0) { _private = true; return; }
        if (c is >= '0' and <= '9') {
            _paramStarted = true;
            _param = _param * 10 + (c - '0');
            return;
        }
        if (c == ';') { PushParam(); return; }
        if (c is >= ' ' and <= '/') return;
        PushParam();
        if (!_private) Execute(c);
        _mode = Mode.Ground;
    }

    void PushParam() {
        if (_paramCount < _params.Length) _params[_paramCount++] = _paramStarted ? _param : 0;
        _param = 0;
        _paramStarted = false;
    }

    void Execute(char c) {
        switch (c) {
            case 'A': Move(0, -Param(0, 1)); break;
            case 'B': Move(0, Param(0, 1)); break;
            case 'C': Move(Param(0, 1), 0); break;
            case 'D': Move(-Param(0, 1), 0); break;
            case 'G': _col = Math.Clamp(Param(0, 1) - 1, 0, _cols - 1); break;
            case 'H':
            case 'f':
                _row = Math.Clamp(Param(0, 1) - 1, 0, _rows - 1);
                _col = Math.Clamp(Param(1, 1) - 1, 0, _cols - 1);
                break;
            case 'J':
                EraseDisplay(Param(0, 0));
                break;
            case 'K':
                EraseLine(Param(0, 0));
                break;
            case 's':
                _savedRow = _row;
                _savedCol = _col;
                break;
            case 'u':
                _row = _savedRow;
                _col = _savedCol;
                break;
        }
    }

    int Param(int index, int missing) =>
        index < _paramCount && _params[index] > 0 ? _params[index] : missing;

    void Move(int dCol, int dRow) {
        _col = Math.Clamp(_col + dCol, 0, _cols - 1);
        _row = Math.Clamp(_row + dRow, 0, _rows - 1);
    }

    void EraseDisplay(int mode) {
        if (mode is 2 or 3) {
            Array.Fill(_cells, ' ');
            _row = 0;
            _col = 0;
            return;
        }
        if (mode == 0) Fill(_row * _cols + _col, _cells.Length);
        else Fill(0, _row * _cols + _col + 1);
    }

    void EraseLine(int mode) {
        var start = _row * _cols;
        if (mode == 2) Fill(start, start + _cols);
        else if (mode == 0) Fill(start + _col, start + _cols);
        else Fill(start, start + _col + 1);
    }

    void Fill(int from, int to) {
        for (var i = Math.Max(0, from); i < Math.Min(_cells.Length, to); i++) _cells[i] = ' ';
    }

    void LineFeed() {
        _col = 0;
        if (_row < _rows - 1) { _row++; return; }
        Array.Copy(_cells, _cols, _cells, 0, _cells.Length - _cols);
        Fill(_cells.Length - _cols, _cells.Length);
    }

    enum Mode { Ground, Esc, Csi, Osc, OscEsc }
}
