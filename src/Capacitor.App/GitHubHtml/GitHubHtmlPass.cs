using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rewrites the HTML the reader understands into Markdig nodes. Depth counts the containers above
/// a node, the document at 0; a subtree's reach counts the levels it occupies, its top included.
public static class GitHubHtmlPass {
    /// Markdig's renderer throws past 128 nested containers, after parsing has returned.
    public const int MaxDepth = 100;
    public const int MaxDetailsNesting = 8;
    public const int MaxHoistedEmphasis = 8;

    public static void Run(MarkdownDocument document) => ProcessContainer(document, 0);

    /// Returns the reach of the container's children.
    static int ProcessContainer(ContainerBlock container, int depth) {
        var reach = 0;
        foreach (var child in container) {
            reach = Math.Max(reach, child switch {
                ContainerBlock nested => 1 + ProcessContainer(nested, depth + 1),
                LeafBlock leaf        => 1 + InlinePass.Process(leaf, depth + 1),
                _                     => 1,
            });
        }
        return reach;
    }
}
