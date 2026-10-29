using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// Qodo's High-Level Assessment writes pros and cons as a bullet list whose items already open
/// with ➕ / ➖. Keeping the list marker stacks a second bullet beside those signs, and the emoji
/// read faint on the dark canvas. When every item of an unordered list opens that way, the list
/// becomes ordinary paragraphs led by a bold `+` / `-`.
public static class ProsConsLists {
    const string Plus = "➕";
    const string Minus = "➖";

    public static void Run(ContainerBlock container) {
        for (var i = 0; i < container.Count; i++) {
            if (container[i] is ContainerBlock nested and not ListBlock) Run(nested);
            if (container[i] is not ListBlock { IsOrdered: false } list) continue;
            if (!TryFlatten(list, out var paragraphs)) continue;
            container.RemoveAt(i);
            foreach (var paragraph in paragraphs) container.Insert(i++, paragraph);
            i--;
        }
    }

    static bool TryFlatten(ListBlock list, out List<ParagraphBlock> paragraphs) {
        paragraphs = new();
        if (list.Count == 0) return false;
        foreach (var child in list) {
            if (child is not ListItemBlock { Count: 1 } item || item[0] is not ParagraphBlock { Inline: { } inline }) return false;
            var text = InlineText.Plain(inline).TrimStart();
            string sign;
            string rest;
            if (text.StartsWith(Plus, StringComparison.Ordinal)) {
                sign = "+";
                rest = text[Plus.Length..].TrimStart();
            } else if (text.StartsWith(Minus, StringComparison.Ordinal)) {
                sign = "-";
                rest = text[Minus.Length..].TrimStart();
            } else return false;

            var root = new ContainerInline();
            var bold = new EmphasisInline { DelimiterChar = '*', DelimiterCount = 2 };
            bold.AppendChild(new LiteralInline(sign));
            root.AppendChild(bold);
            if (rest.Length > 0) root.AppendChild(new LiteralInline(" " + rest));
            paragraphs.Add(new ParagraphBlock { Inline = root });
        }
        return true;
    }
}
