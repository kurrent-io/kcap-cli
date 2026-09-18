using System.Net;
using System.Text;
using Markdig.Helpers;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.GitHubHtml;

/// Turns the tokens of one accepted part into inlines. The reader has already checked that the
/// tags nest, so a close tag always has its open tag on the stack.
static class InlineBuilder {
    public static ContainerInline Build(IReadOnlyList<HtmlToken> tokens, InlineBuildMode mode) {
        var root = new ContainerInline();
        new Builder(root, mode).Run(tokens);
        if (mode != InlineBuildMode.Pre) {
            TrimStart(root);
            TrimEnd(root);
        }
        // A summary is a button's label: it has no links to hoist.
        if (mode != InlineBuildMode.Summary) LinkHoister.Hoist(root);
        return root;
    }

    sealed class Builder(ContainerInline root, InlineBuildMode mode) {
        /// Null for a tag that adds no node: an anchor without a target, any anchor in a summary.
        readonly Stack<ContainerInline?> _frames = new();
        ContainerInline _target = root;
        StringBuilder? _code;
        int _codeDepth;

        public void Run(IReadOnlyList<HtmlToken> tokens) {
            for (var i = 0; i < tokens.Count; i++) {
                var token = tokens[i];
                switch (token.Kind) {
                    case HtmlTokenKind.Text:
                        Text(token.Text, first: i == 0, last: i == tokens.Count - 1);
                        break;
                    case HtmlTokenKind.OpenTag when HtmlTags.Classify(token.Name) == HtmlTagClass.Void:
                        Void(token);
                        break;
                    case HtmlTokenKind.OpenTag:
                        Open(token);
                        break;
                    case HtmlTokenKind.CloseTag:
                        Close();
                        break;
                }
            }
        }

        void Text(string raw, bool first, bool last) {
            var text = WebUtility.HtmlDecode(raw);
            if (mode != InlineBuildMode.Pre) { Append(InlineText.Collapse(text)); return; }

            text = text.ReplaceLineEndings("\n");
            // Browsers show neither the line end after `<pre>` nor the one before `</pre>`.
            if (first && text.StartsWith('\n')) text = text[1..];
            if (last && text.EndsWith('\n')) text = text[..^1];
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                if (i > 0) Break();
                Append(lines[i]);
            }
        }

        void Append(string text) {
            if (_code is not null) _code.Append(text);
            else if (text.Length > 0) _target.AppendChild(new LiteralInline(text));
        }

        void Break() {
            FlushCode();
            _target.AppendChild(new LineBreakInline { IsHard = true });
        }

        void Void(HtmlToken token) {
            if (token.Name == "br") {
                if (_code is not null && mode != InlineBuildMode.Pre) _code.Append(' ');
                else Break();
                return;
            }
            var source = token.Attribute("src")?.Trim() ?? "";
            var label = ImageLabel.For(token.Attribute("alt"), source);
            if (_code is not null || mode == InlineBuildMode.Summary) { Append(label); return; }
            var image = new LinkInline(source, "") { IsImage = true };
            image.AppendChild(new LiteralInline(label));
            _target.AppendChild(image);
        }

        void Open(HtmlToken token) {
            // Inside a code-like tag everything flattens to text; nested tags only need counting.
            if (_code is not null) { _codeDepth++; return; }
            if (HtmlTags.IsCode(token.Name)) { _code = new(); _codeDepth = 1; return; }

            if (token.Name == "a") {
                var target = token.Attribute("href")?.Trim();
                Push(mode == InlineBuildMode.Summary || string.IsNullOrEmpty(target) ? null : new LinkInline(target, ""));
                return;
            }
            HtmlTags.TryEmphasis(token.Name, out var delimiter, out var count);
            Push(new EmphasisInline { DelimiterChar = delimiter, DelimiterCount = count });
        }

        void Push(ContainerInline? node) {
            _frames.Push(node);
            if (node is null) return;
            _target.AppendChild(node);
            _target = node;
        }

        void Close() {
            if (_code is not null) {
                if (--_codeDepth > 0) return;
                FlushCode();
                _code = null;
                return;
            }
            if (_frames.Pop() is { } node) _target = node.Parent!;
        }

        void FlushCode() {
            if (_code is null || _code.Length == 0) return;
            var text = mode == InlineBuildMode.Pre ? _code.ToString() : InlineText.Collapse(_code.ToString());
            _code.Clear();
            if (text.Length > 0) _target.AppendChild(new CodeInline(text));
        }
    }

    static void TrimStart(ContainerInline container) {
        while (container.FirstChild is { } first) {
            if (first is LiteralInline literal) {
                var trimmed = literal.Content.ToString().TrimStart();
                if (trimmed.Length == 0) { literal.Remove(); continue; }
                literal.Content = new StringSlice(trimmed);
                return;
            }
            if (first is ContainerInline nested and not LinkInline { IsImage: true }) TrimStart(nested);
            return;
        }
    }

    static void TrimEnd(ContainerInline container) {
        while (container.LastChild is { } last) {
            if (last is LiteralInline literal) {
                var trimmed = literal.Content.ToString().TrimEnd();
                if (trimmed.Length == 0) { literal.Remove(); continue; }
                literal.Content = new StringSlice(trimmed);
                return;
            }
            if (last is ContainerInline nested and not LinkInline { IsImage: true }) TrimEnd(nested);
            return;
        }
    }
}
