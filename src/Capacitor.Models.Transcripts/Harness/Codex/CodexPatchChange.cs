namespace Capacitor.Models.Transcripts.Harness.Codex;

/// One file an `apply_patch` touched, keyed in `patch_apply_end.changes` by its path.
public sealed record CodexPatchChange {
    public string? FilePath    { get; init; }
    public string? Kind        { get; init; }
    public string? UnifiedDiff { get; init; }
    public string? Content     { get; init; }
    public string? MovePath    { get; init; }
}
