using System.Text.RegularExpressions;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Capacitor.App.Services;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Blocks;
using MarkView.Avalonia.Rendering.Inlines;

namespace Capacitor.App.Views;

/// The app's rules over MarkView's defaults: a link exists only when the policy would open it,
/// an image is its source text and never a fetch, and HTML is shown rather than dropped.
public sealed partial class KcapMarkdownExtension : IMarkViewExtension {
    public void Register(AvaloniaRenderer renderer) {
        renderer.ReplaceOrAdd<LinkInlineRenderer>(new PolicyLinkRenderer());
        renderer.ReplaceOrAdd<AutolinkInlineRenderer>(new PolicyAutolinkRenderer());
        renderer.ReplaceOrAdd<HtmlBlockRenderer>(new SourceHtmlBlockRenderer());
        renderer.ReplaceOrAdd<HtmlInlineRenderer>(new SourceHtmlInlineRenderer());
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

    sealed class PolicyLinkRenderer : AvaloniaObjectRenderer<LinkInline> {
        protected override void Write(AvaloniaRenderer renderer, LinkInline obj) {
            if (obj.IsImage) {
                renderer.WriteInline(new Run($"![{Label(obj)}]({obj.Url})"));
                return;
            }
            if (!LinkPolicy.IsOpenable(obj.Url)) {
                renderer.WriteChildren(obj);
                return;
            }
            var link = Hyperlink(obj.Url!);
            renderer.Push(link.Inlines);
            renderer.WriteChildren(obj);
            renderer.Pop();
            renderer.WriteInline(link);
        }
    }

    sealed class PolicyAutolinkRenderer : AvaloniaObjectRenderer<AutolinkInline> {
        protected override void Write(AvaloniaRenderer renderer, AutolinkInline obj) {
            if (!LinkPolicy.IsOpenable(obj.Url)) {
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
            else renderer.WriteInline(new Run(obj.Tag));
        }
    }
}
