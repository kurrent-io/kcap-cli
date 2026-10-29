namespace Capacitor.App.ViewModels;

/// A file the daemon delivered with a prompt. PreviewPath is set only for an image under a root on
/// this machine; a remote session passes none, so its files stay names.
public sealed record UserAttachment(string FileName, string? PreviewPath) {
    public static UserAttachment From(string path, string? root) {
        var resolved = root is not null && ToolDetail.IsImageExtension(path) ? ToolDetail.ResolvePreviewPath(path, root) : null;
        // Either separator: a remote session's Windows path reaches a Mac or Linux app too.
        return new(path[(path.LastIndexOfAny(['/', '\\']) + 1)..], resolved);
    }
}
