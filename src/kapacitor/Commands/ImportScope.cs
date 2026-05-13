namespace kapacitor.Commands;

/// <summary>
/// Selected history import scope, resolved from CLI flags or the interactive picker.
/// </summary>
public abstract record ImportScope {
    public sealed record All  : ImportScope;
    public sealed record Org  (string OrgLogin) : ImportScope;
    public sealed record Repo (string Owner, string Name) : ImportScope;

    private ImportScope() { }
}
