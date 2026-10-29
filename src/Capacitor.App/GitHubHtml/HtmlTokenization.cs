namespace Capacitor.App.GitHubHtml;

/// `Malformed` means the input started a tag or a comment it never finished.
public sealed record HtmlTokenization(IReadOnlyList<HtmlToken> Tokens, bool Malformed);
