namespace Capacitor.App.GitHubHtml;

/// One thing an HTML block would become. `Tag` is the structural tag of an open or close part
/// and the open tag of a heading; `Reach` counts levels: for a paragraph, heading or `pre`, its
/// block node included; for a summary, the levels below its details node.
sealed record HtmlBlockPart(HtmlBlockPartKind Kind, HtmlToken? Tag, IReadOnlyList<HtmlToken> Tokens, int Reach);
