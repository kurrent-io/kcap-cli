namespace Capacitor.Cli.Core.Pages;

/// <summary>A page named the way a person hands one over: its bare id, or any link to it — the viewer
/// at <c>/pages/{id}</c>, the older <c>/artefacts/{id}</c>, or the content URL beneath either.</summary>
public static class PageReference {
    static readonly string[] Routes = ["pages", "artefacts"];

    /// <summary>The page id, or null when the text names none. The host of a link is not checked: the
    /// id is looked up on this profile's server, which answers not found for a page it does not hold.</summary>
    public static string? ParseId(string? reference) {
        var text = reference?.Trim();

        if (string.IsNullOrEmpty(text)) return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var route    = Array.FindIndex(segments, s => Routes.Contains(s, StringComparer.Ordinal));

            return route >= 0 && route + 1 < segments.Length ? Valid(Uri.UnescapeDataString(segments[route + 1])) : null;
        }

        return Valid(text);
    }

    // Escaping alone leaves "." and ".." to walk out of the route.
    static string? Valid(string id) =>
        id is "." or ".." || id.Contains('/') || id.Contains('\\') || id.Any(char.IsWhiteSpace) ? null : id;
}
