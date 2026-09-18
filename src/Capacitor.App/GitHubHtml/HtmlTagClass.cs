namespace Capacitor.App.GitHubHtml;

/// Void tags convert on their own; the structural tag pairs across blocks; paired tags must
/// balance where they stand.
public enum HtmlTagClass { Unknown, Void, Structural, Paired }
