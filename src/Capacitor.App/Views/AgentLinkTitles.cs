using Markdig.Renderers;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// A link to one of the app server's agent pages whose label is only its URL reads as the agent's
/// title. A label the writer chose stays. Registered after <see cref="KcapMarkdownExtension"/>,
/// whose link renderers it wraps, so the link policy still decides whether a link exists.
public sealed class AgentLinkTitles(MarkdownView view) : IMarkViewExtension {
    public void Register(AvaloniaRenderer renderer) {
        Wrap<LinkInline>(renderer, inner => new LinkRenderer(view, inner));
        Wrap<AutolinkInline>(renderer, inner => new AutolinkRenderer(view, inner));
    }

    static void Wrap<T>(AvaloniaRenderer renderer, Func<IMarkdownObjectRenderer, IMarkdownObjectRenderer> wrap) {
        var renderers = renderer.ObjectRenderers;
        for (var i = 0; i < renderers.Count; i++) {
            if (!renderers[i].Accept(renderer, typeof(T))) continue;
            renderers[i] = wrap(renderers[i]);
            return;
        }
    }

    sealed class LinkRenderer(MarkdownView view, IMarkdownObjectRenderer inner) : AvaloniaObjectRenderer<LinkInline> {
        protected override void Write(AvaloniaRenderer renderer, LinkInline obj) {
            if (!obj.IsImage && obj.Url is { } url && (obj.IsAutoLink || KcapMarkdownExtension.Label(obj) == url)
                && view.AgentLinkLabel(url) is { } label) {
                while (obj.FirstChild is { } child) child.Remove();
                obj.AppendChild(new LiteralInline(label));
            }
            inner.Write(renderer, obj);
        }
    }

    // `<url>` carries no label to replace, so it is rewritten as a link with one.
    sealed class AutolinkRenderer(MarkdownView view, IMarkdownObjectRenderer inner) : AvaloniaObjectRenderer<AutolinkInline> {
        protected override void Write(AvaloniaRenderer renderer, AutolinkInline obj) {
            if (obj.IsEmail || view.AgentLinkLabel(obj.Url) is not { } label) {
                inner.Write(renderer, obj);
                return;
            }
            var link = new LinkInline(obj.Url, "");
            link.AppendChild(new LiteralInline(label));
            renderer.Write(link);
        }
    }
}
