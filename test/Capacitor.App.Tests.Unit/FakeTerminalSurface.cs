using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// The shared ITerminalSurface fake: records what was fed and lets a test raise input/resize as
/// if the control did. Suites that only need an inert surface use it as-is and ignore the
/// recorders.
sealed class FakeTerminalSurface : ITerminalSurface {
    public List<string> Fed { get; } = [];
    public void Feed(string text, bool replay = false) => Fed.Add(text);
    /// One entry per draw, as the control would show it.
    public void Feed(IReadOnlyList<string> pieces, bool replay = false) => Fed.Add(string.Concat(pieces));
    public event Action<byte[]>? InputProduced;
    public event Action<int, int>? Resized;
    public void RaiseInput(byte[] bytes) => InputProduced?.Invoke(bytes);
    public void RaiseResize(int cols, int rows) => Resized?.Invoke(cols, rows);
    public (int Cols, int Rows) CurrentSize { get; set; } = (80, 24);
    public List<(int Cols, int Rows)> Resizes { get; } = [];
    public void Resize(int cols, int rows) { Resizes.Add((cols, rows)); CurrentSize = (cols, rows); }
}
