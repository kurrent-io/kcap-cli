namespace Capacitor.App.Services;

/// The trust boundary for agent-authored links: the shell opener launches whatever it is
/// handed. Absolute http(s) URLs and mailto reach it. javascript, data, file and other schemes stay text.
public static class LinkPolicy {
    public static bool IsOpenable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && Permitted(uri);

    static bool Permitted(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto;

    /// Opens a link that passes the boundary and swallows an opener failure into a log line — a
    /// missing browser must never take the window down with it.
    public static void Open(IUrlOpener opener, string? url) {
        if (!IsOpenable(url)) return;
        try { opener.Open(url!); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: open link failed: {ex.Message}"); }
    }
}
