using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rebuilds one block container from its children and the plans of its HTML blocks. Matching,
/// rejection and the depth check all run before anything mutates; the two walks share their
/// arithmetic, the first deciding what fits and the second building it.
static class DetailsFolder {
    public sealed class Item(Block block, HtmlBlockPlan? plan, int reach) {
        public Block Block => block;
        public HtmlBlockPlan? Plan => plan;
        /// The levels the block occupies if it stays as it is.
        public int Reach => reach;
        /// One entry per `details` tag of the block, aligned with the plan's `DetailsTags`.
        public List<Pair?> TagPairs { get; } = [];
        public bool Converts => plan is { Rejected: false };
    }

    public sealed class Pair(Item open, Item close) {
        public Item Open => open;
        public Item Close => close;
        public bool Rejected { get; set; }
        public Item Partner(Item item) => ReferenceEquals(item, open) ? close : open;
    }

    sealed class Frame(Pair pair, DetailsBlock? node) {
        public Pair Pair => pair;
        public DetailsBlock? Node => node;
        /// The levels the details node occupies, itself included.
        public int Reach { get; set; } = 1;
        public List<Block> Children { get; } = [];
    }

    /// Returns the reach of the container's children.
    public static int Fold(ContainerBlock container, int depth, List<Item> items) {
        if (!items.Exists(item => item.Plan is not null)) return Reach(items);
        var pairs = Match(items);
        Propagate(items, pairs);
        Walk(container, depth, items, apply: false);
        Propagate(items, pairs);
        if (!items.Exists(item => item.Converts)) return Reach(items);
        return Walk(container, depth, items, apply: true);
    }

    static int Reach(List<Item> items) {
        var reach = 0;
        foreach (var item in items) reach = Math.Max(reach, item.Reach);
        return reach;
    }

    /// Pairs every `details` open tag with its close tag across the container's HTML blocks,
    /// rejected ones included, so a rejected block cannot change who pairs with whom.
    static List<Pair> Match(List<Item> items) {
        var pairs = new List<Pair>();
        var open = new Stack<(Item Item, int Tag)>();
        foreach (var item in items) {
            if (item.Plan is not { } plan) continue;
            for (var tag = 0; tag < plan.DetailsTags.Count; tag++) {
                item.TagPairs.Add(null);
                if (plan.DetailsTags[tag]) { open.Push((item, tag)); continue; }
                if (open.Count == 0) { plan.Rejected = true; continue; }
                var (opener, openerTag) = open.Pop();
                var pair = new Pair(opener, item);
                opener.TagPairs[openerTag] = pair;
                item.TagPairs[tag] = pair;
                pairs.Add(pair);
            }
        }
        foreach (var (item, _) in open) item.Plan!.Rejected = true;

        // Levels count matched pairs only: an open tag that never closes nests nothing.
        var level = 0;
        foreach (var item in items) {
            if (item.Plan is not { } plan) continue;
            for (var tag = 0; tag < item.TagPairs.Count; tag++) {
                if (item.TagPairs[tag] is not { } pair) continue;
                if (!plan.DetailsTags[tag]) { level--; continue; }
                if (++level > GitHubHtmlPass.MaxDetailsNesting) pair.Rejected = true;
            }
        }
        return pairs;
    }

    /// A rejected pair rejects both its blocks; a rejected block rejects the partner block of
    /// every pair it takes part in. Each block is rejected once, so this is linear in the tags.
    static void Propagate(List<Item> items, List<Pair> pairs) {
        foreach (var pair in pairs) {
            if (!pair.Rejected) continue;
            pair.Open.Plan!.Rejected = true;
            pair.Close.Plan!.Rejected = true;
        }
        var pending = new Queue<Item>(items.Where(item => item.Plan is { Rejected: true }));
        while (pending.TryDequeue(out var item)) {
            foreach (var pair in item.TagPairs) {
                if (pair is null || pair.Rejected) continue;
                pair.Rejected = true;
                var partner = pair.Partner(item);
                if (partner.Plan!.Rejected) continue;
                partner.Plan.Rejected = true;
                pending.Enqueue(partner);
            }
        }
    }

    /// Walks the items as they will be laid out. With `apply` false it only measures, rejecting a
    /// pair or a block whose deepest node would pass the limit; with `apply` true it builds.
    static int Walk(ContainerBlock container, int depth, List<Item> items, bool apply) {
        var frames = new Stack<Frame>();
        var reach = 0;
        var pending = apply ? new List<Block>() : null;
        var completed = apply ? new List<Frame>() : null;

        foreach (var item in items) {
            if (!item.Converts) {
                if (apply) AddToTarget(item.Block);
                Note(item.Reach);
                continue;
            }
            var tag = 0;
            foreach (var part in item.Plan!.Parts) {
                switch (part.Kind) {
                    case HtmlBlockPartKind.DetailsOpen: {
                        var node = apply ? new DetailsBlock { StartsOpen = part.IsOpen } : null;
                        frames.Push(new Frame(item.TagPairs[tag++]!, node));
                        break;
                    }
                    case HtmlBlockPartKind.DetailsClose: {
                        tag++;
                        var frame = frames.Pop();
                        // The node sits at depth + 1 + the frames still open around it.
                        if (!apply && depth + frames.Count + frame.Reach > GitHubHtmlPass.MaxDepth) frame.Pair.Rejected = true;
                        if (apply) { completed!.Add(frame); AddToTarget(frame.Node!); }
                        Note(frame.Reach);
                        break;
                    }
                    case HtmlBlockPartKind.Summary: {
                        var frame = frames.Peek();
                        if (apply) frame.Node!.Summary = InlineBuilder.Build(part.Tokens, InlineBuildMode.Summary);
                        frame.Reach = Math.Max(frame.Reach, 1 + part.Reach);
                        break;
                    }
                    default: {
                        // Only breaks the switch: the remaining parts still run so this
                        // block's DetailsClose pops the frames it pushed.
                        if (!apply && depth + frames.Count + part.Reach > GitHubHtmlPass.MaxDepth) { item.Plan.Rejected = true; break; }
                        if (apply) AddToTarget(Build(part));
                        Note(part.Reach);
                        break;
                    }
                }
            }
        }

        // The only mutation point, reached only once every InlineBuilder.Build above has
        // already succeeded: a throw before this leaves container untouched.
        if (apply) {
            container.Clear();
            foreach (var frame in completed!)
                foreach (var child in frame.Children) frame.Node!.Add(child);
            foreach (var block in pending!) container.Add(block);
        }
        return reach;

        void AddToTarget(Block block) {
            if (frames.Count > 0) frames.Peek().Children.Add(block);
            else pending!.Add(block);
        }

        void Note(int levels) {
            if (frames.Count > 0) frames.Peek().Reach = Math.Max(frames.Peek().Reach, 1 + levels);
            else reach = Math.Max(reach, levels);
        }
    }

    static Block Build(HtmlBlockPart part) => part.Kind == HtmlBlockPartKind.Pre
        ? new HtmlPreBlock(InlineBuilder.Build(part.Tokens, InlineBuildMode.Pre))
        : new ParagraphBlock { Inline = InlineBuilder.Build(part.Tokens, InlineBuildMode.Paragraph) };
}
