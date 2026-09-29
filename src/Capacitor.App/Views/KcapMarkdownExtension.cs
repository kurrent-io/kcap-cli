using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Services;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;
using MarkView.Avalonia.Rendering.Inlines;

namespace Capacitor.App.Views;

/// The app's rules over MarkView's defaults: a link exists only when the policy would open it and
/// never inside another, an image is fetched only in the reader flavor and only through
/// `MarkdownImages`, and HTML is shown rather than dropped.
public sealed partial class KcapMarkdownExtension(MarkdownFlavor flavor) : IMarkViewExtension {
    public static KcapMarkdownExtension Chat { get; } = new(MarkdownFlavor.Chat);

    public static KcapMarkdownExtension GitHub { get; } = new(MarkdownFlavor.GitHub);

    public void Register(AvaloniaRenderer renderer) {
        var scope = new LinkScope();
        renderer.ReplaceOrAdd<LinkInlineRenderer>(new PolicyLinkRenderer(flavor, scope));
        renderer.ReplaceOrAdd<AutolinkInlineRenderer>(new PolicyAutolinkRenderer(scope));
        renderer.ReplaceOrAdd<HtmlBlockRenderer>(new SourceHtmlBlockRenderer());
        renderer.ReplaceOrAdd<HtmlInlineRenderer>(new SourceHtmlInlineRenderer());
        if (flavor == MarkdownFlavor.GitHub) {
            renderer.ObjectRenderers.Add(new HtmlPreBlockRenderer());
            renderer.ObjectRenderers.Add(new HtmlIndentBlockRenderer());
            var paragraphs = renderer.ObjectRenderers.OfType<ParagraphRenderer>().FirstOrDefault() ?? new ParagraphRenderer();
            renderer.ReplaceOrAdd<ParagraphRenderer>(new ImageRowParagraphRenderer(paragraphs));
        }
        renderer.ImageLoaders.Clear();
    }

    static MarkdownHyperlink Hyperlink(string url) {
        var link = new MarkdownHyperlink { NavigateUri = new Uri(url, UriKind.Absolute) };
        link.Classes.Add("markdown-link");
        return link;
    }

    /// Only an enclosing openable anchor makes an image a link; a bare image is display-only so a
    /// divider or badge does not open its own CDN URL.
    static MarkdownImage CreateImage(LinkInline image, string? anchor, bool inline) {
        var target = LinkPolicy.IsOpenable(anchor) ? anchor : null;
        return new MarkdownImage(image.Url ?? "", ImageLabel.For(Label(image), image.Url), ImageSize.Of(image), target, inline);
    }

    static string Label(ContainerInline container) =>
        string.Concat(container.Select(inline => inline switch {
            LiteralInline literal   => literal.Content.ToString(),
            CodeInline code         => code.Content,
            HtmlEntityInline entity => entity.Transcoded.ToString(),
            ContainerInline nested  => Label(nested),
            _                       => "",
        }));

    sealed class PolicyLinkRenderer(MarkdownFlavor flavor, LinkScope scope) : AvaloniaObjectRenderer<LinkInline> {
        protected override void Write(AvaloniaRenderer renderer, LinkInline obj) {
            if (obj.IsImage) { Image(renderer, obj); return; }
            if (scope.Inside || !LinkPolicy.IsOpenable(obj.Url)) { renderer.WriteChildren(obj); return; }
            var link = Hyperlink(obj.Url!);
            renderer.Push(link.Inlines);
            scope.Enter(obj.Url);
            renderer.WriteChildren(obj);
            scope.Exit();
            renderer.Pop();
            renderer.WriteInline(link);
        }

        void Image(AvaloniaRenderer renderer, LinkInline obj) {
            if (flavor == MarkdownFlavor.Chat) { renderer.WriteInline(new Run($"![{Label(obj)}]({obj.Url})")); return; }
            renderer.WriteInline(new InlineUIContainer(CreateImage(obj, scope.Url, inline: true)));
        }
    }

    /// A paragraph of nothing but images — a screenshot, a badge row, a divider — renders as a
    /// row of block images, which take their natural size; inside a text block the line's exact
    /// height would squash them. Anything with text in it is left to MarkView's paragraph.
    sealed class ImageRowParagraphRenderer(ParagraphRenderer text) : AvaloniaObjectRenderer<ParagraphBlock> {
        protected override void Write(AvaloniaRenderer renderer, ParagraphBlock obj) {
            var images = new List<(LinkInline Image, string? Anchor)>();
            if (obj.Inline is null || !OnlyImages(obj.Inline, null, images) || images.Count == 0) {
                ((IMarkdownObjectRenderer)text).Write(renderer, obj);
                return;
            }
            var row = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
            row.Classes.Add("markdown-image-row");
            foreach (var (image, anchor) in images) row.Children.Add(CreateImage(image, anchor, inline: false));
            renderer.WriteBlock(row);
        }

        static bool OnlyImages(ContainerInline container, string? anchor, List<(LinkInline, string?)> images) {
            foreach (var inline in container) {
                switch (inline) {
                    case LiteralInline literal when literal.Content.ToString().Trim().Length == 0:
                    case LineBreakInline:
                        continue;
                    case LinkInline { IsImage: true } image:
                        images.Add((image, anchor));
                        continue;
                    case LinkInline link when anchor is null && OnlyImages(link, link.Url, images):
                        continue;
                    default:
                        return false;
                }
            }
            return true;
        }
    }

    sealed class PolicyAutolinkRenderer(LinkScope scope) : AvaloniaObjectRenderer<AutolinkInline> {
        protected override void Write(AvaloniaRenderer renderer, AutolinkInline obj) {
            if (scope.Inside || !LinkPolicy.IsOpenable(obj.Url)) {
                renderer.WriteInline(new Run(obj.Url));
                return;
            }
            var link = Hyperlink(obj.Url);
            link.Inlines.Add(new Run(obj.Url));
            renderer.WriteInline(link);
        }
    }

    sealed class SourceHtmlBlockRenderer : AvaloniaObjectRenderer<HtmlBlock> {
        protected override void Write(AvaloniaRenderer renderer, HtmlBlock obj) {
            var text = new MarkdownSelectableTextBlock { TextWrapping = TextWrapping.Wrap };
            text.Classes.Add("markdown-paragraph");
            renderer.Push(text.Inlines!);
            renderer.WriteLeafRawLines(obj);
            renderer.Pop();
            renderer.WriteBlock(text);
        }
    }

    sealed partial class SourceHtmlInlineRenderer : AvaloniaObjectRenderer<HtmlInline> {
        [GeneratedRegex(@"^<br\s*/?\s*>$", RegexOptions.IgnoreCase)]
        private static partial Regex BrTag();

        protected override void Write(AvaloniaRenderer renderer, HtmlInline obj) {
            if (BrTag().IsMatch(obj.Tag.Trim())) renderer.WriteInline(new LineBreak());
            else SourceText.Write(renderer, obj.Tag);
        }
    }
}
