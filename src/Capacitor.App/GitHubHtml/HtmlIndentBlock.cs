using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// A `dd` element: its content set in from the left, with no other chrome.
public sealed class HtmlIndentBlock : ContainerBlock {
    public HtmlIndentBlock() : base(null) { }
}
