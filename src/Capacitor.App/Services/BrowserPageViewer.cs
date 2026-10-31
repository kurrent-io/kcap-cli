namespace Capacitor.App.Services;

/// Opens a page at the URL the server listed, in the system browser.
public sealed class BrowserPageViewer(IUrlOpener opener) : IPageViewer {
    public void Open(string pageId, string url) => LinkPolicy.Open(opener, url);
}
