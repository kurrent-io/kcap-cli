using Capacitor.Cli.Core;

namespace Capacitor.App.Services.LocalSetup;

public static class StderrTail {
    /// The non-blank stderr lines of a run's tail, joined; null when there are none.
    public static string? Text(StreamingResult result) {
        var lines = result.Tail.Where(l => l.Kind == ProcessStreamKind.Stderr && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => l.Text.Trim()).ToList();

        return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
    }
}
