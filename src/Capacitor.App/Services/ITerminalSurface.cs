namespace Capacitor.App.Services;

/// Minimal surface the VM drives, wrapped in production over the SvcSystems control model. Kept
/// App-local (no Avalonia/SvcSystems types in the signature) so TerminalTabViewModel's own tests
/// never need a real terminal control.
public interface ITerminalSurface {
    /// Renders already-decoded text — callers own UTF-8 decoding (Utf8StreamDecoder) since the
    /// control's own byte-array Feed re-decodes fresh on every call and corrupts a code point
    /// split across frames.
    void Feed(string text);

    /// Keyboard input AND terminal-generated protocol replies (e.g. a DSR/CPR answer) — both
    /// need to reach the attached PTY, so the VM forwards this to SendInputAsync verbatim.
    event Action<byte[]>? InputProduced;

    /// User-driven resize (cols, rows) — the VM forwards this to ResizeAsync.
    event Action<int, int>? Resized;

    /// The surface's own current dimensions — what the pane is actually showing, which corrects
    /// the phantom initial size a client carries until the pane has been measured.
    (int Cols, int Rows) CurrentSize { get; }

    /// Sizes the pane to the source PTY — a remote viewer follows the source, it never drives it.
    void Resize(int cols, int rows);
}
