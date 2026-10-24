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
        var paragraphs = new ParagraphBlockParser { ParseSetexHeadings = false };
        paragraphs.Closed += BlankLines.RecordLastLine;
        builder.BlockParsers.Clear();
        builder.BlockParsers.AddRange([
            new QuoteBlockParser(),
            new RulelessListBlockParser(),
            new FencedCodeBlockParser(),
            paragraphs,
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

    /// Markdig's list parser opens a thematic break itself, ahead of any list item, so leaving the
    /// thematic break parser out is not enough to keep `---` or `***` as typed.
    sealed class RulelessListBlockParser : ListBlockParser {
        public override BlockState TryOpen(BlockProcessor processor) {
            var rules = ThematicBreakParser.Default;
            if (rules.HasOpeningCharacter(processor.CurrentChar) && rules.TryOpen(processor).IsBreak()) {
                processor.NewBlocks.Pop();
                return BlockState.None;
            }
            return base.TryOpen(processor);
        }
    }

    sealed class BlankLines : IMarkdownExtension {
        static readonly object LastLineKey = new();

        public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += Keep;

        public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

        /// A paragraph's source lines are released once its inlines are parsed, and a code span
        /// can cross a line without leaving a line break behind, so the last line is taken here.
        public static void RecordLastLine(BlockProcessor processor, Block block) {
            if (block is ParagraphBlock paragraph) paragraph.SetData(LastLineKey, paragraph.Line + Math.Max(paragraph.Lines.Count - 1, 0));
        }

        /// Markdown reads any run of blank lines as one paragraph break. Adjacent paragraphs are
        /// joined instead, by a line break for each newline that separated them in the source.
        static void Keep(MarkdownDocument document) {
            foreach (var container in document.Descendants<ContainerBlock>().Prepend(document).ToArray())
                for (var i = 0; i < container.Count - 1;) {
                    if (container[i] is not ParagraphBlock { Inline: { } into } before
                        || container[i + 1] is not ParagraphBlock { Inline: { } from } after) {
                        i++;
                        continue;
                    }
                    for (var line = LastLine(before); line < after.Line; line++) into.AppendChild(new LineBreakInline { IsHard = true });
                    while (from.FirstChild is { } inline) {
                        inline.Remove();
                        into.AppendChild(inline);
                    }
                    before.SetData(LastLineKey, LastLine(after));
                    container.RemoveAt(i + 1);
                }
        }

        static int LastLine(ParagraphBlock paragraph) => paragraph.GetData(LastLineKey) as int? ?? paragraph.Line;
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
