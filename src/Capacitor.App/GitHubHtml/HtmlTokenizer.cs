using System.Collections.Frozen;
using System.Net;

namespace Capacitor.App.GitHubHtml;

/// Splits an HTML fragment into tags, text and comments in one forward scan. It never throws:
/// whatever does not finish as a tag or a comment is text, and marks the result malformed, as
/// does a declaration, processing instruction or CDATA section.
public static class HtmlTokenizer {
    public static HtmlTokenization Tokenize(string html) => new Scanner(html).Run();

    sealed class Scanner(string html) {
        readonly List<HtmlToken> _tokens = [];
        readonly int _lastClose = html.LastIndexOf('>');
        bool _malformed;

        public HtmlTokenization Run() {
            var i = 0;
            var textStart = 0;
            while (i < html.Length) {
                if (html[i] != '<') { i++; continue; }

                if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0) {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    if (end < 0) { _malformed = true; break; }
                    Text(textStart, i);
                    _tokens.Add(HtmlToken.OfComment(html[i..(end + 3)]));
                    i = textStart = end + 3;
                    continue;
                }

                var next = i + 1 < html.Length ? html[i + 1] : '\0';
                // A declaration, processing instruction or CDATA section is text the reader never
                // converts around: marking it malformed keeps its whole block as source.
                if (next is '!' or '?') { _malformed = true; i++; continue; }
                if (!char.IsAsciiLetter(next) && next != '/') { i++; continue; }

                if (i < _lastClose && TryReadTag(i, out var tag, out var after)) {
                    Text(textStart, i);
                    _tokens.Add(tag);
                    i = textStart = after;
                    continue;
                }
                _malformed = true;
                i++;
            }
            Text(textStart, html.Length);
            return new(_tokens, _malformed);
        }

        void Text(int start, int end) {
            if (end > start) _tokens.Add(HtmlToken.OfText(html[start..end]));
        }

        bool TryReadTag(int start, out HtmlToken token, out int after) {
            token = null!;
            after = start;
            var i = start + 1;
            var close = html[i] == '/';
            if (close) i++;

            var nameStart = i;
            if (i >= html.Length || !char.IsAsciiLetter(html[i])) return false;
            while (i < html.Length && (char.IsAsciiLetterOrDigit(html[i]) || html[i] == '-')) i++;
            var name = html[nameStart..i].ToLowerInvariant();

            Dictionary<string, string>? attributes = null;
            while (true) {
                var gap = i;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (i >= html.Length) return false;
                if (html[i] == '>') { i++; break; }
                if (html[i] == '/') {
                    if (i + 1 < html.Length && html[i + 1] == '>') { i += 2; break; }
                    return false;
                }
                if (close || i == gap) return false;

                var attributeStart = i;
                while (i < html.Length && IsAttributeNameChar(html[i])) i++;
                if (i == attributeStart) return false;
                var attribute = html[attributeStart..i];

                var value = "";
                var afterName = i;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (i < html.Length && html[i] == '=') {
                    i++;
                    while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                    if (i >= html.Length) return false;
                    if (html[i] is '"' or '\'') {
                        var end = html.IndexOf(html[i], i + 1);
                        if (end < 0) return false;
                        value = html[(i + 1)..end];
                        i = end + 1;
                    } else {
                        var valueStart = i;
                        while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('"' or '\'' or '=' or '<' or '>' or '`')) i++;
                        if (i == valueStart) return false;
                        value = html[valueStart..i];
                    }
                } else {
                    i = afterName;
                }
                (attributes ??= new(StringComparer.OrdinalIgnoreCase)).TryAdd(attribute, WebUtility.HtmlDecode(value));
            }

            token = new(close ? HtmlTokenKind.CloseTag : HtmlTokenKind.OpenTag, name, html[start..i],
                attributes is null ? FrozenDictionary<string, string>.Empty : attributes);
            after = i;
            return true;
        }

        static bool IsAttributeNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or ':' or '.' or '-';
    }
}
