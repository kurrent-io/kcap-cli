namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex.task` on a response cluster's first event, from `event_msg.task_complete`.
public sealed record CodexTurnTimingExtension {
    public double? DurationMs         { get; init; }
    public double? TimeToFirstTokenMs { get; init; }
}
