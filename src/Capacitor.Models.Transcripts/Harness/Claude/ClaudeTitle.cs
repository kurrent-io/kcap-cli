namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary><paramref name="ChangedAt"/> is set only for a rename, and only when a timestamped
/// line precedes the start of its last contiguous run in the transcript.</summary>
public sealed record ClaudeTitle(string Title, bool IsRename, DateTimeOffset? ChangedAt);
