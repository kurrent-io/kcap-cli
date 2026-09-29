namespace Capacitor.App.Services;

using System.Text;
using SvcSystems.UI.Terminal;

/// Production ITerminalSurface wrapping SvcSystems.UI.Terminal's TerminalControlModel.
///
/// InputProduced fans in two sources, and a PTY round trip needs both: the model's UserInput
/// (keyboard and mouse) and the engine's DataReceived (terminal-originated replies such as a
/// cursor position report), which neither the SvcSystems wrapper nor the model re-expose.
public sealed class XtermTerminalSurface : ITerminalSurface {
    /// The VM-owned model handle the view binds.
    public TerminalControlModel Model { get; }

    public event Action<byte[]>? InputProduced;
    public event Action<int, int>? Resized;

    // The model exposes no Cols/Rows of its own; the wrapper's track every resize.
    public (int Cols, int Rows) CurrentSize => (Model.Terminal.Cols, Model.Terminal.Rows);

    readonly string? _dumpPath;

    /// <paramref name="dumpPath"/>: a file every fed frame is appended to as received, before
    /// any rewriting — the only record of what the emulator was given.
    public XtermTerminalSurface(int cols, int rows, string? dumpPath = null) {
        _dumpPath = dumpPath;
        Model = new TerminalControlModel(new TerminalOptions {
            Cols = cols,
            Rows = rows,
            ReflowOnResize = false,
        });

        // The control encodes keys with the legacy generators only, so the engine must not
        // advertise the kitty keyboard protocol: an agent that reads the advertisement expects
        // encodings the pane never sends.
        Model.Terminal.Engine.Options.KittyKeyboardEnabled = false;

        Model.UserInput += OnUserInput;
        Model.SizeChanged += OnSizeChanged;
        Model.Terminal.Engine.DataReceived += OnDataReceived;
    }

    public void Feed(string text) {
        if (_dumpPath is not null) File.AppendAllText(_dumpPath, text);
        Model.Feed(TerminalGlyphSubstitution.Apply(text));
    }

    void OnUserInput(object? sender, TerminalUserInputEventArgs e) =>
        InputProduced?.Invoke(e.Data.ToArray());

    // The engine hands a reply back as a decoded string; the PTY side needs bytes.
    void OnDataReceived(object? sender, XTerm.Events.TerminalEvents.DataEventArgs e) =>
        InputProduced?.Invoke(Encoding.UTF8.GetBytes(e.Data));

    void OnSizeChanged(object? sender, TerminalSizeChangedEventArgs e) =>
        Resized?.Invoke(e.Cols, e.Rows);

    public void Resize(int cols, int rows) => Model.Terminal.Resize(cols, rows);
}
