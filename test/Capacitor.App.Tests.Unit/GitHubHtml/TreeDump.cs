using System.Text;
using Capacitor.App.GitHubHtml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

/// Prints a Markdig tree on one line, so a test can assert an exact tree:
/// `doc(p('a ',em*2('b')))`.
static class TreeDump {
    public static string Of(MarkdownObject node) {
        var builder = new StringBuilder();
        Write(builder, node);
        return builder.ToString();
    }

    static void Write(StringBuilder builder, MarkdownObject node) {
        switch (node) {
            case MarkdownDocument document: Blocks(builder, "doc", document); break;
            case HtmlPreBlock pre:        Leaf(builder, "pre", pre); break;
            case ParagraphBlock paragraph: Leaf(builder, "p", paragraph); break;
            case HeadingBlock heading:    Leaf(builder, "h" + heading.Level, heading); break;
            case HtmlBlock html:          builder.Append("html(").Append(Quote(Lines(html))).Append(')'); break;
            case QuoteBlock quote:        Blocks(builder, "quote", quote); break;
            case ListBlock list:          Blocks(builder, "list", list); break;
            case ListItemBlock item:      Blocks(builder, "li", item); break;
            case ContainerBlock container: Blocks(builder, container.GetType().Name, container); break;
            case LeafBlock leaf:          Leaf(builder, leaf.GetType().Name, leaf); break;
            case LiteralInline literal:   builder.Append(Quote(literal.Content.ToString())); break;
            case CodeInline code:         builder.Append("code(").Append(Quote(code.Content)).Append(')'); break;
            case LineBreakInline line:    builder.Append(line.IsHard ? "br" : "sp"); break;
            case HtmlInline tag:          builder.Append("tag(").Append(Quote(tag.Tag)).Append(')'); break;
            case AutolinkInline auto:     builder.Append("auto(").Append(auto.Url).Append(')'); break;
            case HtmlEntityInline entity: builder.Append(Quote(entity.Transcoded.ToString())); break;
            case EmphasisInline emphasis:
                builder.Append("em").Append(emphasis.DelimiterChar).Append(emphasis.DelimiterCount).Append('(');
                Inlines(builder, emphasis);
                builder.Append(')');
                break;
            case LinkInline link:
                builder.Append(link.IsImage ? "img[" : "link[").Append(link.Url).Append("](");
                Inlines(builder, link);
                builder.Append(')');
                break;
            case ContainerInline other:
                builder.Append(other.GetType().Name).Append('(');
                Inlines(builder, other);
                builder.Append(')');
                break;
            default: builder.Append(node.GetType().Name); break;
        }
    }

    static void Blocks(StringBuilder builder, string name, ContainerBlock container) {
        builder.Append(name).Append('(');
        for (var i = 0; i < container.Count; i++) {
            if (i > 0) builder.Append(',');
            Write(builder, container[i]);
        }
        builder.Append(')');
    }

    static void Leaf(StringBuilder builder, string name, LeafBlock leaf) {
        builder.Append(name).Append('(');
        if (leaf.Inline is not null) Inlines(builder, leaf.Inline);
        builder.Append(')');
    }

    static void Inlines(StringBuilder builder, ContainerInline container) {
        var first = true;
        foreach (var inline in container) {
            if (!first) builder.Append(',');
            first = false;
            Write(builder, inline);
        }
    }

    static string Lines(LeafBlock block) {
        var lines = new List<string>();
        for (var i = 0; i < block.Lines.Count; i++) lines.Add(block.Lines.Lines[i].Slice.ToString());
        return string.Join("\\n", lines);
    }

    static string Quote(string text) => "'" + text.Replace("\n", "\\n").Replace("\r", "\\r") + "'";
}
