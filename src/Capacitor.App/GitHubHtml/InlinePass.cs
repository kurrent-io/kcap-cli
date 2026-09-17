using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// One leaf block's inline tree, in the order that keeps every measured height true:
/// normalisation adds its children before the inline rule budgets around them.
static class InlinePass {
    /// Returns the reach of the leaf's inlines, 0 when it has none.
    public static int Process(LeafBlock leaf, int leafDepth) {
        if (leaf.Inline is not { } root) return 0;
        var rootDepth = leafDepth + 1;
        Normalise(root, rootDepth);
        var reach = InlinePairing.Process(root, rootDepth);
        // Hoisting moves nodes sideways and never deepens the tree, so the reach still holds.
        LinkHoister.Hoist(root);
        return 1 + reach;
    }

    static void Normalise(ContainerInline root, int rootDepth) {
        var pending = new Stack<(ContainerInline Container, int Depth)>();
        pending.Push((root, rootDepth));
        while (pending.Count > 0) {
            var (container, depth) = pending.Pop();
            for (var child = container.FirstChild; child is not null;) {
                var next = child.NextSibling;
                switch (child) {
                    case LinkInline { IsImage: true } image:
                        LabelImage(image, depth + 1);
                        break;
                    case AutolinkInline { IsEmail: false } auto when depth + 2 <= GitHubHtmlPass.MaxDepth: {
                        var link = new LinkInline(auto.Url, "") { IsAutoLink = true, IsClosed = true };
                        link.AppendChild(new LiteralInline(auto.Url));
                        auto.ReplaceBy(link, copyChildren: false);
                        break;
                    }
                    case ContainerInline nested:
                        pending.Push((nested, depth + 1));
                        break;
                }
                child = next;
            }
        }
    }

    /// Replacing existing children with one literal cannot deepen the tree; giving a childless
    /// image a child can, and such an image stays childless for its renderer to label.
    static void LabelImage(LinkInline image, int imageDepth) {
        if (image.FirstChild is null && imageDepth + 1 > GitHubHtmlPass.MaxDepth) return;
        var label = ImageLabel.For(InlineText.Plain(image), image.Url);
        image.Clear();
        image.AppendChild(new LiteralInline(label));
    }
}
