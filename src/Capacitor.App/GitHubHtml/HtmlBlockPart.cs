namespace Capacitor.App.GitHubHtml;

/// One thing an HTML block would become. `Reach` counts levels: for a paragraph or a `pre`, its
/// block node included; for a summary, the levels below its details node.
sealed record HtmlBlockPart(HtmlBlockPartKind Kind, IReadOnlyList<HtmlToken> Tokens, bool IsOpen, int Reach);
