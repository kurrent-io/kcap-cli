using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

/// The block rule: one pass over a block's tokens that either describes everything the block
/// would become or rejects it whole. Nothing is built here, so a rejected block costs a scan.
static class HtmlBlockReader {
    public static HtmlBlockPlan Read(HtmlBlock block) {
        var plan = new HtmlBlockPlan();
        var result = HtmlTokenizer.Tokenize(Source(block));
        if (result.Malformed) plan.Rejected = true;
        new Reader(plan).Run(result.Tokens);
        return plan;
    }

    static string Source(HtmlBlock block) {
        var lines = new string[block.Lines.Count];
        for (var i = 0; i < lines.Length; i++) lines[i] = block.Lines.Lines[i].Slice.ToString();
        return string.Join('\n', lines);
    }

    sealed class Reader(HtmlBlockPlan plan) {
        readonly Stack<string> _open = new();
        List<HtmlToken> _content = [];
        HtmlToken? _leafTag;
        int _inlineLevels;
        int _deepest;
        bool _hasContent;
        bool _afterDetailsOpen;

        public void Run(IReadOnlyList<HtmlToken> tokens) {
            if (WrapsAcrossBlocks(tokens)) {
                foreach (var token in tokens) if (!token.IsWhitespace) Structural(token);
                return;
            }
            foreach (var token in tokens) {
                var isTag = token.Kind is HtmlTokenKind.OpenTag or HtmlTokenKind.CloseTag;
                if (isTag && HtmlTags.Classify(token.Name) == HtmlTagClass.Structural) Structural(token);
                else if (!plan.Rejected) Step(token);
            }
            if (plan.Rejected) return;
            if (_open.Count > 0) { plan.Rejected = true; return; }
            FlushParagraph();
        }

        /// A block of formatting tags alone, all open or all close — `<sub>` on a line of its own,
        /// `</sub>` on a later one — wraps the markdown between them, so its tags pair across
        /// blocks like structural ones. An anchor or code tag is not taken: the wrapper renders
        /// its content and nothing else, which would lose the link or the formatting.
        static bool WrapsAcrossBlocks(IReadOnlyList<HtmlToken> tokens) {
            HtmlTokenKind? kind = null;
            foreach (var token in tokens) {
                if (token.IsWhitespace) continue;
                if (token.Kind is not (HtmlTokenKind.OpenTag or HtmlTokenKind.CloseTag)) return false;
                if (!HtmlTags.IsTransparent(token.Name) && !HtmlTags.TryEmphasis(token.Name, out _, out _)) return false;
                if (kind is null) kind = token.Kind;
                else if (kind != token.Kind) return false;
            }
            return kind is not null;
        }

        /// A structural tag is never pushed or popped: it needs nothing open around it and is
        /// matched across blocks later.
        void Structural(HtmlToken token) {
            var opens = token.Kind == HtmlTokenKind.OpenTag;
            plan.StructuralTags.Add(token);
            if (plan.Rejected) return;
            if (_open.Count > 0) { plan.Rejected = true; return; }
            FlushParagraph();
            plan.Parts.Add(new(opens ? HtmlBlockPartKind.Open : HtmlBlockPartKind.Close, token, [], 0));
            _afterDetailsOpen = opens && token.Name == "details";
        }

        void Step(HtmlToken token) {
            switch (token.Kind) {
                case HtmlTokenKind.Comment:
                    return;
                case HtmlTokenKind.Text:
                    if (!token.IsWhitespace) {
                        _afterDetailsOpen = false;
                        _hasContent = true;
                        Leaf(1);
                    }
                    _content.Add(token);
                    return;
            }

            var opens = token.Kind == HtmlTokenKind.OpenTag;
            switch (HtmlTags.Classify(token.Name)) {
                case HtmlTagClass.Void when opens && token.Name == "hr":
                    Rule();
                    return;
                case HtmlTagClass.Void when opens && token.Name is "source" or "wbr":
                    return;
                // A line break is content only beside something else: alone it makes no paragraph.
                case HtmlTagClass.Void when opens:
                    _afterDetailsOpen = false;
                    _hasContent |= token.Name == "img";
                    Leaf(token.Name == "img" ? 2 : 1);
                    _content.Add(token);
                    return;
                case HtmlTagClass.Paired when token.Name == "summary":
                    Summary(opens);
                    return;
                case HtmlTagClass.Paired when token.Name == "pre":
                    LeafBlock(token, opens, HtmlBlockPartKind.Pre);
                    return;
                case HtmlTagClass.Paired when HtmlTags.IsHeading(token.Name, out _):
                    LeafBlock(token, opens, HtmlBlockPartKind.Heading);
                    return;
                case HtmlTagClass.Paired:
                    Inline(token, opens);
                    return;
                default:
                    plan.Rejected = true;
                    return;
            }
        }

        /// Accepted only as the first thing after its `details` open tag, in the same block, once.
        void Summary(bool opens) {
            if (opens) {
                if (!_afterDetailsOpen || _open.Count > 0) { plan.Rejected = true; return; }
                _afterDetailsOpen = false;
                _open.Push("summary");
                Begin();
                return;
            }
            if (_open.Count == 0 || _open.Peek() != "summary") { plan.Rejected = true; return; }
            _open.Pop();
            Emit(HtmlBlockPartKind.Summary, null, 1 + _deepest);
        }

        /// A `pre` or a heading: a leaf block of its own, accepted only where nothing else is open.
        void LeafBlock(HtmlToken token, bool opens, HtmlBlockPartKind kind) {
            if (opens) {
                if (_open.Count > 0) { plan.Rejected = true; return; }
                _afterDetailsOpen = false;
                FlushParagraph();
                _open.Push(token.Name);
                _leafTag = token;
                return;
            }
            if (_open.Count == 0 || _open.Peek() != token.Name) { plan.Rejected = true; return; }
            _open.Pop();
            Emit(kind, _leafTag, 2 + _deepest);
        }

        void Rule() {
            if (_open.Count > 0) { plan.Rejected = true; return; }
            _afterDetailsOpen = false;
            FlushParagraph();
            plan.Parts.Add(new(HtmlBlockPartKind.Rule, null, [], 1));
        }

        void Inline(HtmlToken token, bool opens) {
            if (opens) {
                _afterDetailsOpen = false;
                _open.Push(token.Name);
                // A block nested this far can never fit, and rejecting here keeps the builder shallow.
                if (++_inlineLevels > GitHubHtmlPass.MaxDepth) plan.Rejected = true;
            } else {
                if (_open.Count == 0 || _open.Peek() != token.Name) { plan.Rejected = true; return; }
                _open.Pop();
                _inlineLevels--;
            }
            _content.Add(token);
        }

        void Leaf(int levels) => _deepest = Math.Max(_deepest, _inlineLevels + levels);

        void FlushParagraph() {
            if (_hasContent) Emit(HtmlBlockPartKind.Paragraph, null, 2 + _deepest);
            else Begin();
        }

        void Emit(HtmlBlockPartKind kind, HtmlToken? tag, int reach) {
            plan.Parts.Add(new(kind, tag, _content, reach));
            Begin();
        }

        void Begin() {
            _content = [];
            _deepest = 0;
            _hasContent = false;
        }
    }
}
