using System.Text;

namespace Capacitor.Cli.Daemon;

/// <summary>The start of what a child wrote to stderr, one line per row, never longer than its cap.
/// Over-cap output is dropped, never rotated: this explains a failed launch or an unexpected exit,
/// and that explanation is at the start. A line that does not fit is cut at the cap.</summary>
internal sealed class StderrHead(int cap) {
    readonly Lock          _gate = new();
    readonly StringBuilder _text = new();

    /// <summary>What was kept, or null if the child wrote nothing.</summary>
    public string? Text {
        get { lock (_gate) return _text.Length == 0 ? null : _text.ToString(); }
    }

    public void Capture(string line) {
        lock (_gate) {
            var room = cap - _text.Length;
            if (room < 2) return;

            _text.Append(line.AsSpan(0, Math.Min(line.Length, room - 1))).Append('\n');
        }
    }
}
