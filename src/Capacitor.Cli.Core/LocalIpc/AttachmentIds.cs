namespace Capacitor.Cli.Core.LocalIpc;

/// The one definition of an acceptable attachment id list, run by the uploader over a server
/// response and by the daemon in front of every fetch, whichever lane the ids arrived on.
public static class AttachmentIds {
    public static string Canonical(string id) => id.ToLowerInvariant();

    /// Null when the list is acceptable; otherwise the refusal wording.
    public static string? Validate(IReadOnlyList<string?>? ids) {
        if (ids is null || ids.Count == 0) return null;
        if (ids.Count > InputWire.MaxAttachmentsPerPrompt) return $"up to {InputWire.MaxAttachmentsPerPrompt} attachments per message";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids) {
            if (!InputWire.IsValidAttachmentId(id)) return "malformed attachment id";
            if (!seen.Add(Canonical(id!))) return "duplicate attachment id";
        }
        return null;
    }
}
