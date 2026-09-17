using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rebuilds one block container from its children and the plans of its HTML blocks. Both walks
/// share their arithmetic: the first decides what fits, the second builds it.
static class DetailsFolder {
    public sealed class Item(Block block, HtmlBlockPlan? plan, int reach) {
        public Block Block => block;
        public HtmlBlockPlan? Plan => plan;
        /// The levels the block occupies if it stays as it is.
        public int Reach => reach;
        public bool Converts => plan is { Rejected: false };
    }

    /// Returns the reach of the container's children.
    public static int Fold(ContainerBlock container, int depth, List<Item> items) {
        foreach (var item in items)
            if (item.Plan is { DetailsTags.Count: > 0 } plan) plan.Rejected = true;

        if (!items.Exists(item => item.Converts)) return Walk(container, depth, items, apply: false);
        Walk(container, depth, items, apply: false);
        return Walk(container, depth, items, apply: true);
    }

    static int Walk(ContainerBlock container, int depth, List<Item> items, bool apply) {
        var reach = 0;
        if (apply) container.Clear();

        foreach (var item in items) {
            if (!item.Converts) {
                if (apply) container.Add(item.Block);
                reach = Math.Max(reach, item.Reach);
                continue;
            }
            foreach (var part in item.Plan!.Parts) {
                // A block placed in the container sits at depth + 1, so its deepest node is at depth + reach.
                if (!apply && depth + part.Reach > GitHubHtmlPass.MaxDepth) { item.Plan.Rejected = true; break; }
                if (apply) container.Add(Build(part));
                reach = Math.Max(reach, part.Reach);
            }
        }
        return reach;
    }

    static Block Build(HtmlBlockPart part) => part.Kind == HtmlBlockPartKind.Pre
        ? new HtmlPreBlock(InlineBuilder.Build(part.Tokens, InlineBuildMode.Pre))
        : new ParagraphBlock { Inline = InlineBuilder.Build(part.Tokens, InlineBuildMode.Paragraph) };
}
