using System.Text;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

public static class InlineText {
    public static string Collapse(string? text) {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length);
        var inWhitespace = false;
        foreach (var c in text) {
            if (char.IsWhiteSpace(c)) {
                if (!inWhitespace) builder.Append(' ');
                inWhitespace = true;
            } else {
                builder.Append(c);
                inWhitespace = false;
            }
        }
        return builder.ToString();
    }

    public static string Plain(ContainerInline container) =>
        container.FirstChild is { } first ? Plain(first, null) : "";

    /// The text of `first` and its following siblings, stopping before `until`. A line break reads
    /// as a space and a tag left as source reads as its source.
    public static string Plain(Inline first, Inline? until) {
        var builder = new StringBuilder();
        for (var inline = first; inline is not null && !ReferenceEquals(inline, until); inline = inline.NextSibling)
            Append(builder, inline);
        return builder.ToString();
    }

    static void Append(StringBuilder builder, Inline inline) {
        switch (inline) {
            case LiteralInline literal:    builder.Append(literal.Content.ToString()); break;
            case CodeInline code:          builder.Append(code.Content); break;
            case HtmlEntityInline entity:  builder.Append(entity.Transcoded.ToString()); break;
            case LineBreakInline:          builder.Append(' '); break;
            case HtmlInline html:          builder.Append(html.Tag); break;
            case AutolinkInline auto:      builder.Append(auto.Url); break;
            case ContainerInline nested:
                for (var child = nested.FirstChild; child is not null; child = child.NextSibling) Append(builder, child);
                break;
        }
    }
}
