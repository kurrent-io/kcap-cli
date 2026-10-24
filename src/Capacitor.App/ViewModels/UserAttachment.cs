namespace Capacitor.App.ViewModels;

/// A file the daemon delivered with a prompt. PreviewPath is set only for an image under a root on
/// this machine; a remote session passes none, so its files stay names.
public sealed record UserAttachment(string FileName, string? PreviewPath) {
    public bool HasPreview => PreviewPath is not null;

    public static UserAttachment From(string path, string? root) {
        var resolved = root is not null && ToolDetail.IsImageExtension(path) ? ToolDetail.ResolvePreviewPath(path, root) : null;
        return new(Path.GetFileName(path), resolved);
    }
}
