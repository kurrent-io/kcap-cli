namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex.patch` on an `apply_patch` result, from `event_msg.patch_apply_end`. Written
/// even when no file changed, so a reader knows the call was answered.
public sealed record CodexPatchResultExtension {
    public List<CodexPatchChange>? Changes { get; init; }
    public bool?                   Success { get; init; }
}
