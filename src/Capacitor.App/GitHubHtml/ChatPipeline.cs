using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
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

    /// What a person typed, shown as they typed it: emphasis, strikethrough, code, lists, quotes
    /// and bare URLs render, and no other markdown rule applies. A backslash is never an escape —
    /// in `C:\Users\me\.config` the `\.` is a path separator — and every newline is a line break.
    public static MarkdownPipeline Prompt { get; } = PromptPipeline();

    /// Only the parsers named here run, so a parser a Markdig upgrade adds stays off. Markdig's own
    /// order is kept: a paragraph, and plain text after every inline parser, take what nothing
    /// earlier claimed. The paragraph parser still reads a `[label]: url` line as a definition.
    static MarkdownPipeline PromptPipeline() {
        var builder = new MarkdownPipelineBuilder();
        builder.BlockParsers.Clear();
        builder.BlockParsers.AddRange([
            new QuoteBlockParser(),
            new ListBlockParser(),
            new FencedCodeBlockParser(),
            new ParagraphBlockParser { ParseSetexHeadings = false },
        ]);
        builder.InlineParsers.Clear();
        builder.InlineParsers.AddRange([
            new EmphasisInlineParser(),
            new CodeInlineParser(),
            new LineBreakInlineParser { EnableSoftAsHard = true },
        ]);
        return builder
            .UseAutoLinks()
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .Use(new Hoist())
            .Use(new BlankLines())
            .Build();
    }

    sealed class BlankLines : IMarkdownExtension {
        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Keep;

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

        /// Markdown reads any run of blank lines as one paragraph break. Adjacent paragraphs are
        /// joined instead, by a line break for each newline that separated them in the source.
        static void Keep(MarkdownDocument document) {
            foreach (var container in document.Descendants<ContainerBlock>().Prepend(document).ToArray())
                for (var i = container.Count - 1; i > 0; i--) {
                    if (container[i - 1] is not ParagraphBlock { Inline: { } into } before
                        || container[i] is not ParagraphBlock { Inline: { } from } after) continue;
                    var lastLine = before.Line + into.Descendants<LineBreakInline>().Count();
                    for (var line = lastLine; line < after.Line; line++) into.AppendChild(new LineBreakInline { IsHard = true });
                    while (from.FirstChild is { } inline) {
                        inline.Remove();
                        into.AppendChild(inline);
                    }
                    container.RemoveAt(i);
                }
        }
    }

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
