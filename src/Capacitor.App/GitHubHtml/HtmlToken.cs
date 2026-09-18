using System.Collections.Frozen;

namespace Capacitor.App.GitHubHtml;

/// One piece of an HTML fragment. `Name` is lower-case, attribute values are entity-decoded, and
/// `Text` is the raw source of the piece.
public sealed record HtmlToken(HtmlTokenKind Kind, string Name, string Text, IReadOnlyDictionary<string, string> Attributes) {
    public static HtmlToken OfText(string text) => new(HtmlTokenKind.Text, "", text, FrozenDictionary<string, string>.Empty);

    public static HtmlToken OfComment(string text) => new(HtmlTokenKind.Comment, "", text, FrozenDictionary<string, string>.Empty);

    public string? Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : null;

    public bool IsWhitespace => Kind == HtmlTokenKind.Text && string.IsNullOrWhiteSpace(Text);
}
