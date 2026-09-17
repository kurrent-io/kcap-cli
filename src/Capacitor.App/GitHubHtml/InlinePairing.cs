using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

static class InlinePairing {
    /// Returns the reach of the container's children.
    public static int Process(ContainerInline container, int containerDepth) {
        var reach = 0;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
            reach = Math.Max(reach, child is ContainerInline nested ? 1 + Process(nested, containerDepth + 1) : 1);
        return reach;
    }
}
