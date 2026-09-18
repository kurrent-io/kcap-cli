using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Drops structure that would draw chrome around nothing. A rule at the start or end of its
/// container separates nothing, and of two in a row one is enough. A quote or a `dd` holding only
/// details sections is a bot's way of indenting them; the sections are set in on their own, so the
/// wrapper would only add a rail or a second inset per level.
public static class BlockTidy {
    public static void Run(ContainerBlock container) {
        foreach (var child in container) if (child is ContainerBlock nested) Run(nested);
        for (var i = 0; i < container.Count; i++) {
            if (container[i] is not (QuoteBlock or HtmlIndentBlock) || container[i] is not ContainerBlock wrapper) continue;
            if (wrapper.Count == 0 || !wrapper.All(child => child is DetailsBlock)) continue;
            var sections = wrapper.ToList();
            wrapper.Clear();
            container.RemoveAt(i);
            foreach (var section in sections) container.Insert(i++, section);
            i--;
        }
        for (var i = container.Count - 1; i >= 0; i--) {
            if (container[i] is not ThematicBreakBlock) continue;
            if (i == 0 || i == container.Count - 1 || container[i - 1] is ThematicBreakBlock) container.RemoveAt(i);
        }
    }
}
