namespace Capacitor.App.ViewModels;

public sealed record PastedLinkMatch(
    PastedLinkKind Kind,
    string         Url,
    string?        Owner    = null,
    string?        Repo     = null,
    string?        RepoHash = null,
    int?           Number   = null,
    long?          RunId    = null,
    string?        IssueKey = null,
    string?        Workspace = null,
    string?        Slug     = null);
