namespace Capacitor.App.Services;

using System.Text;
using SvcSystems.UI.Terminal;

/// Production ITerminalSurface wrapping SvcSystems.UI.Terminal's TerminalControlModel.
///
/// InputProduced fans in TWO distinct sources, neither re-exposing the other:
/// TerminalControlModel.UserInput (keyboard/mouse-originated bytes, ReadOnlyMemory&lt;byte&gt;
/// already) and Terminal.Engine.DataReceived (terminal-originated protocol replies, e.g. a
/// DSR/CPR answer — only reachable via the raw XTerm.NET engine object, which neither the
/// SvcSystems Terminal wrapper nor the model re-expose; its payload is a string that must be
/// UTF-8 encoded before it can join the same byte[] event). Both are needed for a correct PTY
/// round trip: dropping either one silently breaks either keystrokes or terminal-side
/// query/response protocols (cursor position reports, etc.).
public sealed class XtermTerminalSurface : ITerminalSurface {
    /// The VM-owned model handle the view binds.
    public TerminalControlModel Model { get; }

    public event Action<byte[]>? InputProduced;
    public event Action<int, int>? Resized;

    // Terminal (the SvcSystems wrapper), not the model itself — the model exposes no Cols/Rows of
    // its own, while Terminal.Cols/Rows are live, tracking every resize applied via
    // Model.Terminal.Resize(cols, rows).
    public (int Cols, int Rows) CurrentSize => (Model.Terminal.Cols, Model.Terminal.Rows);

    readonly TerminalFeedSanitizer _sanitizer = new();
    readonly string? _dumpPath;
    int _cursorLine;

    /// <paramref name="dumpPath"/>: a file every fed frame is appended to as received, before
    /// any rewriting — the only record of what the emulator was given.
    public XtermTerminalSurface(int cols, int rows, string? dumpPath = null) {
        _dumpPath = dumpPath;
        Model = new TerminalControlModel(new TerminalOptions {
            Cols = cols,
            Rows = rows,
            ReflowOnResize = false,
        });

        Model.UserInput += OnUserInput;
        Model.SizeChanged += OnSizeChanged;
        Model.Terminal.Engine.DataReceived += OnDataReceived;
    }

    public void Feed(string text) {
        if (_dumpPath is not null) File.AppendAllText(_dumpPath, text);
        Model.Feed(TerminalGlyphSubstitution.Apply(_sanitizer.Sanitize(text)));
        RememberCursorLine();
    }

    // XTerm.NET 1.2.0 pulls scrollback back into view on a taller viewport by lowering the
    // buffer's YBase and leaving Y where it stood, so the cursor slides UP the content by every
    // line YBase gave back. The agent's own SIGWINCH repaint then lands that many rows early and
    // erases below itself — a blank tail under a live prompt, and the rows it covered destroyed.
    // A cursor's line is YBase + Y, so remembering that across a resize is enough to put it back.
    // A shorter viewport is the mirror case: correcting it needs YBase, which only the buffer can
    // move, and the drift there is already at the viewport's last row, so this leaves it alone.
    //
    // XTerm.NET 2.x moves both halves itself, and this whole correction goes when the pane can
    // take it — but the version cannot be raised on its own. SvcSystems.UI.Terminal 1.1.4 ships
    // compiled against 1.2.0, where BufferCell.Content is a FIELD and in 2.x is a property: the
    // build stays clean and every model construction throws MissingFieldException from the
    // control's own render path. It has to move first, which costs it net8.0 and net9.0 —
    // XTerm.NET 2.x targets net10.0 alone.
    void KeepCursorOnItsLine() {
        var buffer = Model.Terminal.Buffer;
        var drift = _cursorLine - (buffer.BaseY + buffer.Y);
        if (drift > 0) buffer.SetCursorRaw(buffer.X, Math.Min(buffer.Y + drift, Model.Terminal.Rows - 1));
        RememberCursorLine();
    }

    void RememberCursorLine() => _cursorLine = Model.Terminal.Buffer.BaseY + Model.Terminal.Buffer.Y;

    void OnUserInput(object? sender, TerminalUserInputEventArgs e) =>
        InputProduced?.Invoke(e.Data.ToArray());

    // Terminal-originated protocol reply (DSR/CPR etc.) — the engine hands back a decoded
    // string, not bytes; the PTY side needs bytes, so encode here rather than push the
    // encoding concern onto every consumer of InputProduced.
    void OnDataReceived(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e) =>
        InputProduced?.Invoke(Encoding.UTF8.GetBytes(e.Data));

    void OnSizeChanged(object? sender, TerminalSizeChangedEventArgs e) {
        KeepCursorOnItsLine();
        Resized?.Invoke(e.Cols, e.Rows);
    }

    public void Resize(int cols, int rows) {
        Model.Terminal.Resize(cols, rows);
        KeepCursorOnItsLine();
    }
}
