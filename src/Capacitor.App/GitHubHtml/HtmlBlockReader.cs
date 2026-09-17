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
        int _inlineLevels;
        int _deepest;
        bool _hasContent;
        bool _afterDetailsOpen;

        public void Run(IReadOnlyList<HtmlToken> tokens) {
            foreach (var token in tokens) {
                var isTag = token.Kind is HtmlTokenKind.OpenTag or HtmlTokenKind.CloseTag;
                if (isTag && HtmlTags.Classify(token.Name) == HtmlTagClass.Structural) Details(token);
                else if (!plan.Rejected) Step(token);
            }
            if (plan.Rejected) return;
            if (_open.Count > 0) { plan.Rejected = true; return; }
            FlushParagraph();
        }

        /// A `details` tag is never pushed or popped: it needs nothing open around it and is
        /// matched across blocks later.
        void Details(HtmlToken token) {
            var opens = token.Kind == HtmlTokenKind.OpenTag;
            plan.DetailsTags.Add(opens);
            if (plan.Rejected) return;
            if (_open.Count > 0) { plan.Rejected = true; return; }
            FlushParagraph();
            plan.Parts.Add(new(opens ? HtmlBlockPartKind.DetailsOpen : HtmlBlockPartKind.DetailsClose, [], opens && token.Attributes.ContainsKey("open"), 0));
            _afterDetailsOpen = opens;
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
                case HtmlTagClass.Void when opens:
                    _afterDetailsOpen = false;
                    _hasContent = true;
                    Leaf(token.Name == "img" ? 2 : 1);
                    _content.Add(token);
                    return;
                case HtmlTagClass.Paired when token.Name == "summary":
                    Summary(opens);
                    return;
                case HtmlTagClass.Paired when token.Name == "pre":
                    Pre(opens);
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
            Emit(HtmlBlockPartKind.Summary, 1 + _deepest);
        }

        void Pre(bool opens) {
            if (opens) {
                if (_open.Count > 0) { plan.Rejected = true; return; }
                _afterDetailsOpen = false;
                FlushParagraph();
                _open.Push("pre");
                return;
            }
            if (_open.Count == 0 || _open.Peek() != "pre") { plan.Rejected = true; return; }
            _open.Pop();
            Emit(HtmlBlockPartKind.Pre, 2 + _deepest);
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
            if (_hasContent) Emit(HtmlBlockPartKind.Paragraph, 2 + _deepest);
            else Begin();
        }

        void Emit(HtmlBlockPartKind kind, int reach) {
            plan.Parts.Add(new(kind, _content, false, reach));
            Begin();
        }

        void Begin() {
            _content = [];
            _deepest = 0;
            _hasContent = false;
        }
    }
}
