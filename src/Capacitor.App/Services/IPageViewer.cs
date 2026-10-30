namespace Capacitor.App.Services;

/// Shows a published kcap page. Every page row opens through this, so an in-app viewer can replace the browser
/// without the rows changing; it gets the id to fetch the page by as well as the URL the server listed.
public interface IPageViewer {
    void Open(string pageId, string url);
}
