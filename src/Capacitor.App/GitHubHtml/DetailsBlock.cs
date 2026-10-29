using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// A `details` element: its summary, whether it starts open, and its position among the
/// document's details sections, which a view keys its expanded state by.
public sealed class DetailsBlock : ContainerBlock {
    public DetailsBlock() : base(null) { }

    public ContainerInline Summary { get; set; } = new();

    public bool StartsOpen { get; set; }

    public int Ordinal { get; set; }
}
