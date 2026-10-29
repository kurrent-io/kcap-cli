using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// A `pre` element: inline formatting kept, line structure as hard breaks.
public sealed class HtmlPreBlock : LeafBlock {
    public HtmlPreBlock(ContainerInline inlines) : base(null) => Inline = inlines;
}
