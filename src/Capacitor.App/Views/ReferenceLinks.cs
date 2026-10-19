using System.Text.RegularExpressions;
using Markdig.Renderers;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia.Extensions;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Inlines;
using Capacitor.App.Services;

namespace Capacitor.App.Views;

/// GitHub's references in text — `@login`, `#N`, `owner/repo#N` and a commit SHA — as links into
/// the repository the view's <see cref="MarkdownView.ReferenceBase"/> names. Only literal text is
/// read, so a code span, a fenced block and an existing link's label stay as they are.
public sealed partial class ReferenceLinks(MarkdownView view) : IMarkViewExtension {
    public void Register(AvaloniaRenderer renderer) {
        var plain = renderer.ObjectRenderers.OfType<LiteralInlineRenderer>().FirstOrDefault() ?? new LiteralInlineRenderer();
        renderer.ReplaceOrAdd<LiteralInlineRenderer>(new Renderer(view, plain));
    }

    // A SHA needs a letter and a digit, so a bare number or word never reads as one.
    [GeneratedRegex("""
        (?<![\w@/.-])@(?<login>[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38})(?![\w-])
        |(?<![\w/.-])(?<owner>[A-Za-z0-9][A-Za-z0-9-]{0,38})/(?<repo>(?!\.+\#)[A-Za-z0-9._-]{1,100})\#(?<other>[1-9][0-9]*)\b
        |(?<![\w&/\#.])\#(?<number>[1-9][0-9]*)\b
        |(?<![\w/@.-])(?=[0-9a-fA-F]*[a-fA-F])(?=[0-9a-fA-F]*[0-9])(?<sha>[0-9a-fA-F]{7,40})(?![\w-])
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Reference();

    /// The link a reference names, given the repository URL it is read against.
    internal static string? Target(Match match, Uri repository) {
        var site = repository.GetLeftPart(UriPartial.Authority);
        var repo = repository.AbsoluteUri.TrimEnd('/');
        if (match.Groups["login"].Success) return $"{site}/{match.Groups["login"].Value}";
        if (match.Groups["other"].Success) return $"{site}/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}/issues/{match.Groups["other"].Value}";
        if (match.Groups["number"].Success) return $"{repo}/issues/{match.Groups["number"].Value}";
        if (match.Groups["sha"].Success) return $"{repo}/commit/{match.Groups["sha"].Value.ToLowerInvariant()}";

        return null;
    }

    static bool InsideLink(Inline inline) {
        for (var parent = inline.Parent; parent is not null; parent = parent.Parent)
            if (parent is LinkInline) return true;

        return false;
    }

    sealed class Renderer(MarkdownView view, LiteralInlineRenderer plain) : AvaloniaObjectRenderer<LiteralInline> {
        protected override void Write(AvaloniaRenderer renderer, LiteralInline obj) {
            var text = obj.Content.ToString();
            if (!Uri.TryCreate(view.ReferenceBase, UriKind.Absolute, out var repository) || InsideLink(obj)) {
                Plain(renderer, text);
                return;
            }
            var at = 0;
            foreach (Match match in Reference().Matches(text)) {
                // `\#12` and `\@login` are the writer's way of saying "not a reference".
                if (match.Index == 0 && obj.IsFirstCharacterEscaped) continue;
                if (Target(match, repository) is not { } url || !LinkPolicy.IsOpenable(url)) continue;
                if (match.Index > at) Plain(renderer, text[at..match.Index]);
                var link = KcapMarkdownExtension.Hyperlink(url);
                renderer.Push(link.Inlines);
                Plain(renderer, match.Value);
                renderer.Pop();
                renderer.WriteInline(link);
                at = match.Index + match.Length;
            }
            if (at < text.Length) Plain(renderer, text[at..]);
        }

        void Plain(AvaloniaRenderer renderer, string text) =>
            ((IMarkdownObjectRenderer)plain).Write(renderer, new LiteralInline(text));
    }
}
