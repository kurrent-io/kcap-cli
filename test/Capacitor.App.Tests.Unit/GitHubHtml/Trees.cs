using Capacitor.App.GitHubHtml;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

static class Trees {
    static readonly MarkdownPipeline WithoutPass = GitHubPipeline.Configure(new MarkdownPipelineBuilder()).Build();

    public static MarkdownDocument Parse(string markdown) => Markdown.Parse(markdown, GitHubPipeline.Instance);

    /// What Markdig alone makes of the source: a block fixture asserts this first, because whether
    /// a line opens an HTML block or a paragraph decides which rule the fixture exercises.
    public static MarkdownDocument ParseWithoutPass(string markdown) => Markdown.Parse(markdown, WithoutPass);

    public static string Dump(string markdown) => TreeDump.Of(Parse(markdown));

    public static string Shape(string markdown) => TreeDump.Of(ParseWithoutPass(markdown));

    /// The depth of the deepest node, the document at 0, walked without recursion.
    public static int MaxDepth(MarkdownDocument document) {
        var deepest = 0;
        var pending = new Stack<(MarkdownObject Node, int Depth)>();
        pending.Push((document, 0));
        while (pending.Count > 0) {
            var (node, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            switch (node) {
                case ContainerBlock blocks:
                    foreach (var child in blocks) pending.Push((child, depth + 1));
                    if (blocks is DetailsBlock details) pending.Push((details.Summary, depth + 1));
                    break;
                case LeafBlock { Inline: { } inlines }:
                    pending.Push((inlines, depth + 1));
                    break;
                case ContainerInline container:
                    foreach (var child in container) pending.Push((child, depth + 1));
                    break;
            }
        }
        return deepest;
    }
}
