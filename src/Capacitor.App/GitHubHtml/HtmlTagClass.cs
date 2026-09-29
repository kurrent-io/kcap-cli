namespace Capacitor.App.GitHubHtml;

/// Void tags convert on their own; structural tags pair across blocks; paired tags must balance
/// where they stand.
public enum HtmlTagClass { Unknown, Void, Structural, Paired }
