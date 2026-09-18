using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// Lifts links above their emphasis ancestors. MarkView activates only a hyperlink that is a
/// direct inline of its text block, and the emphasis renders the same from inside the link.
static class LinkHoister {
    public static void Hoist(ContainerInline root) {
        var links = new List<LinkInline>();
        Collect(root, links);
        // Last link first: what follows a link inside its emphasis moves to a trailing copy, and
        // going backwards leaves each inline to be moved once rather than once per earlier link.
        for (var i = links.Count - 1; i >= 0; i--) HoistOne(links[i]);
    }

    static void Collect(ContainerInline container, List<LinkInline> links) {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling) {
            if (child is LinkInline link) links.Add(link);
            if (child is ContainerInline nested) Collect(nested, links);
        }
    }

    static void HoistOne(LinkInline link) {
        // A childless image gets its label from the renderer, which inherits the emphasis around it.
        if (link.FirstChild is null) return;

        var levels = 0;
        var top = link.Parent;
        while (top is EmphasisInline) {
            levels++;
            top = top.Parent;
        }
        if (levels == 0 || levels > GitHubHtmlPass.MaxHoistedEmphasis || top is null) return;
        // Above the emphasis must be the block's root container or an enclosing link.
        if (top.Parent is not null && top is not LinkInline) return;

        while (link.Parent is EmphasisInline emphasis) LiftOver(link, emphasis);
    }

    static void LiftOver(LinkInline link, EmphasisInline emphasis) {
        link.EmbraceChildrenBy(Copy(emphasis));

        EmphasisInline? tail = null;
        for (var inline = link.NextSibling; inline is not null;) {
            var following = inline.NextSibling;
            inline.Remove();
            (tail ??= Copy(emphasis)).AppendChild(inline);
            inline = following;
        }

        link.Remove();
        emphasis.InsertAfter(link);
        if (tail is not null) link.InsertAfter(tail);
        if (emphasis.FirstChild is null) emphasis.Remove();
    }

    static EmphasisInline Copy(EmphasisInline emphasis) =>
        new() { DelimiterChar = emphasis.DelimiterChar, DelimiterCount = emphasis.DelimiterCount };
}
