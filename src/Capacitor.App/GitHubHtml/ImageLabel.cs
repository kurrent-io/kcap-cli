namespace Capacitor.App.GitHubHtml;

/// What an image reads as: its alt text, else its file name, else a word. The choice is
/// normalised after it is made, because unescaping a file name can produce a line end.
public static class ImageLabel {
    public static string For(string? alt, string? url) {
        string?[] candidates = [alt, FileName(url)];
        foreach (var candidate in candidates) {
            var label = InlineText.Collapse(candidate).Trim();
            if (label.Length > 0) return label;
        }
        return "image";
    }

    /// The longest file name common filesystems allow; anything longer is not a file name.
    const int MaxFileName = 255;

    static string? FileName(string? url) {
        if (string.IsNullOrWhiteSpace(url) || url.AsSpan().TrimStart().StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url.Split('?', '#')[0];
        path = path.TrimEnd('/');
        var name = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        return name.Length <= MaxFileName ? name : null;
    }
}
