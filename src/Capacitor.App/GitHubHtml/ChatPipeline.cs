using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;

namespace Capacitor.App.GitHubHtml;

/// Chat markdown is MarkView's supported set, with links hoisted out of emphasis. A bare builder
/// is CommonMark and drops the tables and autolinks chat already renders. MarkView only activates
/// a hyperlink that is a direct inline of its text block.
public static class ChatPipeline {
    public static MarkdownPipeline Instance { get; } =
        new MarkdownPipelineBuilder().UseSupportedExtensions().Use(new Hoist()).Build();

    sealed class Hoist : IMarkdownExtension {
        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Process;

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

        static void Process(MarkdownDocument document) {
            foreach (var leaf in document.Descendants<LeafBlock>())
                if (leaf.Inline is { } root) {
                    foreach (var auto in root.Descendants<AutolinkInline>().ToArray()) {
                        var link = new LinkInline(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, "") {
                            IsAutoLink = true, IsClosed = true,
                        };
                        link.AppendChild(new LiteralInline(auto.Url));
                        auto.ReplaceBy(link, copyChildren: false);
                    }
                    LinkHoister.Hoist(root, includeImages: false);
                }
        }
    }
}
