namespace Capacitor.Models.Transcripts.Harness.Claude;

/// <summary><paramref name="ChangedAt"/> is set only for a rename, and only when a timestamped
/// line precedes its first occurrence in the transcript.</summary>
public sealed record ClaudeTitle(string Title, bool IsRename, DateTimeOffset? ChangedAt);
