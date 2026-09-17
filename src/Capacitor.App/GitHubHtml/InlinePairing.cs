using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// The inline rule. Reach counts the levels a node occupies, its own included, so a wrapper
/// placed one level below its container fits when `containerDepth + reach` stays within the limit.
static class InlinePairing {
    sealed class OpenTag(string name, HtmlInline node, HtmlToken token, int index) {
        public string Name => name;
        public HtmlInline Node => node;
        public HtmlToken Token => token;
        public int Index => index;
        /// The greatest reach seen since this tag opened.
        public int Reach;
        /// False once a close tag matched past it: a spent open tag never pairs again.
        public bool Live = true;
    }

    /// Returns the reach of the container's children.
    public static int Process(ContainerInline container, int containerDepth) {
        var open = new List<OpenTag>();
        var byName = new Dictionary<string, Stack<OpenTag>>();
        var reach = 0;

        for (var child = container.FirstChild; child is not null;) {
            var next = child.NextSibling;
            if (child is HtmlInline html) Tag(html);
            else Note(child is ContainerInline nested ? 1 + Process(nested, containerDepth + 1) : 1);
            child = next;
        }
        foreach (var leftover in open) reach = Math.Max(reach, leftover.Reach);
        return reach;

        void Note(int levels) {
            if (open.Count > 0) open[^1].Reach = Math.Max(open[^1].Reach, levels);
            else reach = Math.Max(reach, levels);
        }

        void Tag(HtmlInline html) {
            var result = HtmlTokenizer.Tokenize(html.Tag);
            if (result.Malformed || result.Tokens.Count != 1) { Note(1); return; }
            var token = result.Tokens[0];
            if (token.Kind == HtmlTokenKind.Comment) { html.Remove(); return; }
            if (token.Kind == HtmlTokenKind.Text) { Note(1); return; }

            switch (HtmlTags.Classify(token.Name)) {
                case HtmlTagClass.Void when token.Kind == HtmlTokenKind.OpenTag:
                    Void(html, token);
                    break;
                case HtmlTagClass.Paired when HtmlTags.IsInline(token.Name) && token.Kind == HtmlTokenKind.OpenTag:
                    // Counted as source until a close tag proves otherwise.
                    Note(1);
                    var entry = new OpenTag(token.Name, html, token, open.Count);
                    open.Add(entry);
                    if (!byName.TryGetValue(token.Name, out var named)) byName[token.Name] = named = new();
                    named.Push(entry);
                    break;
                case HtmlTagClass.Paired when HtmlTags.IsInline(token.Name):
                    Close(html, token.Name);
                    break;
                default:
                    Note(1);
                    break;
            }
        }

        void Void(HtmlInline html, HtmlToken token) {
            if (token.Name == "br") {
                html.ReplaceBy(new LineBreakInline { IsHard = true }, copyChildren: false);
                Note(1);
                return;
            }
            if (containerDepth + 2 > GitHubHtmlPass.MaxDepth) { Note(1); return; }
            var image = new LinkInline(token.Attribute("src")?.Trim() ?? "", "") { IsImage = true };
            image.AppendChild(new LiteralInline(ImageLabel.For(token.Attribute("alt"), image.Url)));
            html.ReplaceBy(image, copyChildren: false);
            Note(2);
        }

        OpenTag? Find(string name) {
            if (!byName.TryGetValue(name, out var named)) return null;
            while (named.Count > 0 && !named.Peek().Live) named.Pop();
            return named.Count > 0 ? named.Peek() : null;
        }

        void Close(HtmlInline close, string name) {
            if (Find(name) is not { } match) { Note(1); return; }

            var inner = match.Reach;
            for (var i = open.Count - 1; i > match.Index; i--) {
                inner = Math.Max(inner, open[i].Reach);
                open[i].Live = false;
            }
            match.Live = false;
            open.RemoveRange(match.Index, open.Count - match.Index);

            var first = match.Node.NextSibling!;
            var empty = ReferenceEquals(first, close);

            if (HtmlTags.IsCode(name)) {
                var text = empty ? "" : InlineText.Collapse(InlineText.Plain(first, close));
                for (var inline = first; !ReferenceEquals(inline, close);) {
                    var following = inline.NextSibling!;
                    inline.Remove();
                    inline = following;
                }
                close.Remove();
                if (text.Length == 0) { match.Node.Remove(); return; }
                match.Node.ReplaceBy(new CodeInline(text), copyChildren: false);
                Note(1);
                return;
            }

            var target = name == "a" ? match.Token.Attribute("href")?.Trim() : null;
            if (name == "a" && string.IsNullOrEmpty(target)) {
                match.Node.Remove();
                close.Remove();
                Note(inner);
                return;
            }

            var wrapperReach = empty ? 1 : 1 + inner;
            if (containerDepth + wrapperReach > GitHubHtmlPass.MaxDepth) { Note(Math.Max(1, inner)); return; }

            ContainerInline wrapper = name == "a" ? new LinkInline(target!, "") : Emphasis(name);
            for (var inline = first; !ReferenceEquals(inline, close);) {
                var following = inline.NextSibling!;
                inline.Remove();
                wrapper.AppendChild(inline);
                inline = following;
            }
            match.Node.ReplaceBy(wrapper, copyChildren: false);
            close.Remove();
            Note(wrapperReach);
        }
    }

    static EmphasisInline Emphasis(string name) {
        HtmlTags.TryEmphasis(name, out var delimiter, out var count);
        return new EmphasisInline { DelimiterChar = delimiter, DelimiterCount = count };
    }
}
