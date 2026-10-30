namespace Capacitor.App.Services;

/// The document the page viewer's WebView loads: the page's HTML inside a sandboxed srcdoc frame,
/// which gives it an opaque origin with no reach into the host document.
public static class PageHostDocument {
    public const string FrameSandbox = "allow-scripts allow-popups";

    public static string Build(string pageHtml) =>
        "<!doctype html><html><head><meta charset=\"utf-8\">" +
        "<style>html,body{margin:0;height:100%;background:transparent}iframe{display:block;border:0;width:100%;height:100%}</style>" +
        $"</head><body><iframe sandbox=\"{FrameSandbox}\" srcdoc=\"{EscapeAttribute(pageHtml)}\"></iframe></body></html>";

    static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
