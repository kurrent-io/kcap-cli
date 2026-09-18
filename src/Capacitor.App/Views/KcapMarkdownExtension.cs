using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Services;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;
using MarkView.Avalonia.Rendering.Inlines;

namespace Capacitor.App.Views;

/// The app's rules over MarkView's defaults: a link exists only when the policy would open it and
/// never inside another, an image is never fetched, and HTML is shown rather than dropped.
public sealed partial class KcapMarkdownExtension(MarkdownFlavor flavor) : IMarkViewExtension {
    public static KcapMarkdownExtension Chat { get; } = new(MarkdownFlavor.Chat);

    public static KcapMarkdownExtension GitHub { get; } = new(MarkdownFlavor.GitHub);

    public void Register(AvaloniaRenderer renderer) {
        var scope = new LinkScope();
        renderer.ReplaceOrAdd<LinkInlineRenderer>(new PolicyLinkRenderer(flavor, scope));
        renderer.ReplaceOrAdd<AutolinkInlineRenderer>(new PolicyAutolinkRenderer(scope));
        renderer.ReplaceOrAdd<HtmlBlockRenderer>(new SourceHtmlBlockRenderer());
        renderer.ReplaceOrAdd<HtmlInlineRenderer>(new SourceHtmlInlineRenderer());
        if (flavor == MarkdownFlavor.GitHub) renderer.ObjectRenderers.Add(new HtmlPreBlockRenderer());
        renderer.ImageLoaders.Clear();
    }

    static MarkdownHyperlink Hyperlink(string url) {
        var link = new MarkdownHyperlink { NavigateUri = new Uri(url, UriKind.Absolute) };
        link.Classes.Add("markdown-link");
        return link;
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
            scope.Enter();
            renderer.WriteChildren(obj);
            scope.Exit();
            renderer.Pop();
            renderer.WriteInline(link);
        }

        void Image(AvaloniaRenderer renderer, LinkInline obj) {
            if (flavor == MarkdownFlavor.Chat) { renderer.WriteInline(new Run($"![{Label(obj)}]({obj.Url})")); return; }
            if (scope.Inside || !LinkPolicy.IsOpenable(obj.Url)) { WriteLabel(renderer, obj); return; }
            var link = Hyperlink(obj.Url!);
            renderer.Push(link.Inlines);
            scope.Enter();
            WriteLabel(renderer, obj);
            scope.Exit();
            renderer.Pop();
            renderer.WriteInline(link);
        }

        /// The pass gives an image its label as a child; one it had to leave childless is
        /// labelled here, so an image always shows one.
        static void WriteLabel(AvaloniaRenderer renderer, LinkInline obj) {
            if (obj.FirstChild is null) renderer.WriteInline(new Run(ImageLabel.For(null, obj.Url)));
            else renderer.WriteChildren(obj);
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
