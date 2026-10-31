using System.Buffers.Text;
using System.Security.Cryptography;

namespace Capacitor.App.Services;

/// The document the page viewer's WebView loads: the page's HTML inside a sandboxed srcdoc frame,
/// which gives it an opaque origin with no reach into the host document, and the host script that
/// relays the frame's bridge messages to the app.
public static class PageHostDocument {
    public const string FrameSandbox = "allow-scripts allow-popups";

    /// The server's content policy for a page, less frame-ancestors and sandbox, which a meta
    /// element cannot carry. It is what keeps the page and every frame inside it off the network.
    public const string ContentPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; " +
        "media-src data: blob:; connect-src 'none'; form-action 'none'; base-uri 'none'";

    /// The host's own policy, which the srcdoc frame also inherits: the content policy again, so it
    /// holds even for a page that defeats its own meta element, except that the frame may start an
    /// http(s) or mailto navigation. That never loads: the view cancels it and asks the user first.
    public const string HostPolicy = ContentPolicy + "; frame-src http: https: mailto:";

    const string NoncePlaceholder = "\"__KCAP_NONCE__\"";
    /// CSP does not govern DNS prefetch, which a link's host would otherwise trigger with no request.
    const string DnsPrefetchOff = "<meta http-equiv=\"x-dns-prefetch-control\" content=\"off\">";
    const string PolicyMeta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{ContentPolicy}\">{DnsPrefetchOff}";

    static readonly Lazy<string> HostScript = new(() => {
        using var stream = typeof(PageHostDocument).Assembly.GetManifestResourceStream("Capacitor.App.Assets.page-host.js")
            ?? throw new InvalidOperationException("page-host.js is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// 32 random bytes, base64url: the per-load proof that a message came through the host script.
    public static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Build(string pageHtml, string nonce) {
        if (nonce.Length == 0 || !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new ArgumentException("The nonce must be base64url.", nameof(nonce));

        return $"<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"{HostPolicy}\">" + DnsPrefetchOff +
            "<style>html,body{margin:0;height:100%;background:transparent}iframe{display:block;border:0;width:100%;height:100%}</style>" +
            "</head><body><script>" + HostScript.Value.Replace(NoncePlaceholder, $"\"{nonce}\"", StringComparison.Ordinal) + "</script>" +
            $"<iframe id=\"kcap-page\" sandbox=\"{FrameSandbox}\" srcdoc=\"{EscapeAttribute(WithPolicy(pageHtml))}\"></iframe></body></html>";
    }

    /// The policy precedes all of the page's markup, its doctype included: an srcdoc document
    /// without a leading doctype is still in standards mode, and the late doctype is ignored.
    static string WithPolicy(string pageHtml) => PolicyMeta + pageHtml;

    static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
