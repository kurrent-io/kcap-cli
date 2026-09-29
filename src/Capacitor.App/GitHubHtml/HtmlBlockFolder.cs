using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// Rebuilds one block container from its children and the plans of its HTML blocks. Matching,
/// rejection and the depth check all run before anything mutates; the two walks share their
/// arithmetic, the first deciding what fits and the second building it.
static class HtmlBlockFolder {
    public sealed class Item(Block block, HtmlBlockPlan? plan, int reach) {
        public Block Block => block;
        public HtmlBlockPlan? Plan => plan;
        /// The levels the block occupies if it stays as it is.
        public int Reach => reach;
        /// One entry per structural tag of the block, aligned with the plan's `StructuralTags`.
        public List<Pair?> TagPairs { get; } = [];
        public bool Converts => plan is { Rejected: false };
    }

    public sealed class Pair(Item open, Item close, string name) {
        public Item Open => open;
        public Item Close => close;
        public string Name => name;
        public bool Rejected { get; set; }
        public Item Partner(Item item) => ReferenceEquals(item, open) ? close : open;
    }

    sealed class Frame(Pair pair, HtmlToken tag, ContainerBlock? node, bool inHeader) {
        public Pair Pair => pair;
        public string Name => tag.Name;
        public ContainerBlock? Node => node;
        public bool InHeader => inHeader;
        /// One for an element with a node of its own, none for a transparent one.
        public int Levels { get; } = HtmlContainers.HasNode(tag.Name) ? 1 : 0;
        /// The levels the element occupies, itself included.
        public int Reach { get; set; } = HtmlContainers.HasNode(tag.Name) ? 1 : 0;
        public List<Block> Children { get; } = [];
    }

    /// Returns the reach of the container's children.
    public static int Fold(ContainerBlock container, int depth, List<Item> items) {
        if (!items.Exists(item => item.Plan is not null)) return Reach(items);
        var pairs = Match(items);
        Propagate(items, pairs);
        // A rejection found by measuring moves what followed the rejected tags into another
        // parent, which can fail a nesting check that passed, so measuring runs to a fixpoint.
        while (Walk(container, depth, items, apply: false).Changed) Propagate(items, pairs);
        if (!items.Exists(item => item.Converts)) return Reach(items);
        return Walk(container, depth, items, apply: true).Reach;
    }

    static int Reach(List<Item> items) {
        var reach = 0;
        foreach (var item in items) reach = Math.Max(reach, item.Reach);
        return reach;
    }

    /// Pairs every structural open tag with its close tag across the container's HTML blocks,
    /// rejected ones included, so a rejected block cannot change who pairs with whom. A close tag
    /// that does not name the innermost open element rejects its block and leaves that element
    /// open, to be rejected as unclosed.
    static List<Pair> Match(List<Item> items) {
        var pairs = new List<Pair>();
        var open = new Stack<(Item Item, int Tag, string Name)>();
        foreach (var item in items) {
            if (item.Plan is not { } plan) continue;
            for (var tag = 0; tag < plan.StructuralTags.Count; tag++) {
                item.TagPairs.Add(null);
                var token = plan.StructuralTags[tag];
                if (token.Kind == HtmlTokenKind.OpenTag) { open.Push((item, tag, token.Name)); continue; }
                if (open.Count == 0 || open.Peek().Name != token.Name) { plan.Rejected = true; continue; }
                var (opener, openerTag, _) = open.Pop();
                var pair = new Pair(opener, item, token.Name);
                opener.TagPairs[openerTag] = pair;
                item.TagPairs[tag] = pair;
                pairs.Add(pair);
            }
        }
        foreach (var (item, _, _) in open) item.Plan!.Rejected = true;

        // Levels count matched details pairs only: an open tag that never closes nests nothing.
        var level = 0;
        foreach (var item in items) {
            if (item.Plan is not { } plan) continue;
            for (var tag = 0; tag < item.TagPairs.Count; tag++) {
                if (item.TagPairs[tag] is not { Name: "details" } pair) continue;
                if (plan.StructuralTags[tag].Kind != HtmlTokenKind.OpenTag) { level--; continue; }
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
    /// pair or a block whose deepest node would pass the limit or whose element sits where its
    /// kind cannot; with `apply` true it builds.
    static (int Reach, bool Changed) Walk(ContainerBlock container, int depth, List<Item> items, bool apply) {
        var frames = new Stack<Frame>();
        var nodeFrames = 0;
        var reach = 0;
        var changed = false;
        var pending = apply ? new List<Block>() : null;
        var completed = apply ? new List<Frame>() : null;

        foreach (var item in items) {
            if (!item.Converts) {
                Arrive(item.Block, null, null);
                Note(item.Reach);
                continue;
            }
            var tag = 0;
            foreach (var part in item.Plan!.Parts) {
                switch (part.Kind) {
                    case HtmlBlockPartKind.Open: {
                        var pair = item.TagPairs[tag++]!;
                        var node = apply && HtmlContainers.HasNode(part.Tag!.Name) ? HtmlContainers.Create(part.Tag) : null;
                        var frame = new Frame(pair, part.Tag!, node, frames.Count > 0 && frames.Peek().Name == "thead");
                        frames.Push(frame);
                        nodeFrames += frame.Levels;
                        break;
                    }
                    case HtmlBlockPartKind.Close: {
                        tag++;
                        var frame = frames.Pop();
                        nodeFrames -= frame.Levels;
                        if (frame.Levels == 0) {
                            // Already checked against the same enclosing node when they arrived.
                            if (apply) foreach (var child in frame.Children) Attach(child);
                        } else {
                            // The node sits at depth + 1 + the node frames still open around it.
                            if (!apply && depth + nodeFrames + frame.Reach > GitHubHtmlPass.MaxDepth) RejectPair(frame.Pair);
                            if (apply) completed!.Add(frame);
                            Arrive(frame.Node, frame.Name, frame.Pair);
                        }
                        Note(frame.Reach);
                        break;
                    }
                    case HtmlBlockPartKind.Summary: {
                        var frame = frames.Peek();
                        if (apply) ((DetailsBlock)frame.Node!).Summary = InlineBuilder.Build(part.Tokens, InlineBuildMode.Summary);
                        frame.Reach = Math.Max(frame.Reach, frame.Levels + part.Reach);
                        break;
                    }
                    default: {
                        // Only breaks the switch: the remaining parts still run so this
                        // block's close parts pop the frames it pushed.
                        if (!apply && depth + nodeFrames + part.Reach > GitHubHtmlPass.MaxDepth) { RejectItem(item); break; }
                        Arrive(apply ? Build(part) : null, null, null);
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
            foreach (var frame in completed!) {
                foreach (var child in frame.Children) frame.Node!.Add(child);
                HtmlContainers.Finish(frame.Node!, frame.InHeader);
            }
            foreach (var block in pending!) container.Add(block);
        }
        return (reach, changed);

        // A child that arrives at a frame is checked against the nearest element with a node,
        // which is what it will be a child of once the transparent frames between forward it.
        void Arrive(Block? block, string? name, Pair? pair) {
            if (!apply) {
                var parent = frames.FirstOrDefault(frame => frame.Levels > 0);
                if (!HtmlContainers.ParentAccepts(parent?.Name, name)) RejectPair(parent!.Pair);
                if (!HtmlContainers.ChildAccepts(name, parent?.Name)) RejectPair(pair!);
                return;
            }
            Attach(block!);
        }

        void Attach(Block block) {
            if (frames.Count > 0) frames.Peek().Children.Add(block);
            else pending!.Add(block);
        }

        void Note(int levels) {
            if (frames.Count > 0) frames.Peek().Reach = Math.Max(frames.Peek().Reach, frames.Peek().Levels + levels);
            else reach = Math.Max(reach, levels);
        }

        void RejectPair(Pair pair) {
            if (pair.Rejected) return;
            pair.Rejected = true;
            changed = true;
        }

        void RejectItem(Item item) {
            if (item.Plan!.Rejected) return;
            item.Plan.Rejected = true;
            changed = true;
        }
    }

    static Block Build(HtmlBlockPart part) {
        switch (part.Kind) {
            case HtmlBlockPartKind.Pre:
                return new HtmlPreBlock(InlineBuilder.Build(part.Tokens, InlineBuildMode.Pre));
            case HtmlBlockPartKind.Heading:
                HtmlTags.IsHeading(part.Tag!.Name, out var level);
                return new HeadingBlock(null!) { Level = level, HeaderChar = '#', Inline = InlineBuilder.Build(part.Tokens, InlineBuildMode.Paragraph) };
            case HtmlBlockPartKind.Rule:
                return new ThematicBreakBlock(null!) { ThematicChar = '-', ThematicCharCount = 3 };
            default:
                return new ParagraphBlock { Inline = InlineBuilder.Build(part.Tokens, InlineBuildMode.Paragraph) };
        }
    }
}
