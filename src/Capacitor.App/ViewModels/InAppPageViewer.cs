using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// Opens a page in the Artefacts tab's right pane, in place of the document reader.
public sealed class InAppPageViewer(ArtefactsTabViewModel tab) : IPageViewer {
    public void Open(string pageId, string url) => tab.OpenPage(pageId, url);
}
