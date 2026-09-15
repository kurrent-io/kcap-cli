namespace Capacitor.App.ViewModels;

/// Display text for a launch the daemon has not published yet. The stage vocabulary is each
/// runtime's own and open, so anything unnamed here is still rendered rather than dropped.
public static class LaunchStages {
    public static string Label(string? stage) => stage switch {
        null or "" => "Launch accepted",
        "spawned" => "Process started",
        "initialized" => "Initialized",
        "session_created" => "Session created",
        "model_set" => "Model set",
        "thread_started" => "Thread started",
        "thread_resumed" => "Thread resumed",
        _ => Humanize(stage),
    };

    public static string StartingText(string vendor, string? stage) => $"Starting {VendorLabel(vendor)} · {Label(stage)}";

    static string VendorLabel(string vendor) => vendor.Length == 0 ? "agent" : char.ToUpperInvariant(vendor[0]) + vendor[1..];

    static string Humanize(string stage) {
        var text = stage.Replace('_', ' ').Replace('-', ' ').Trim();
        return text.Length == 0 ? "Starting" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
