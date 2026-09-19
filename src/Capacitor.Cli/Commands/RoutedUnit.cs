namespace Capacitor.Cli.Commands;

/// <summary>One routed server session: a parent plus the correlated children its own call imports
/// inline. Children never become top-level sessions, so a unit counts as one everywhere.</summary>
internal sealed record RoutedUnit(
    ImportCommand.SessionClassification                Parent,
    IReadOnlyList<ImportCommand.SessionClassification> Children) {
    public bool Eligible => Parent.Status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial;

    public IEnumerable<ImportCommand.SessionClassification> Members => Children.Prepend(Parent);
}

internal static class RoutedUnits {
    public static List<RoutedUnit> Build(IReadOnlyList<ImportCommand.SessionClassification> routed) {
        var ids      = routed.Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);
        var children = routed
            .Where(c => ParentOf(c) is { } p && ids.Contains(p))
            .ToLookup(c => ParentOf(c)!, StringComparer.Ordinal);

        return routed
            .Where(c => ParentOf(c) is not { } p || !ids.Contains(p))
            .Select(parent => new RoutedUnit(parent, [.. children[parent.SessionId]]))
            .ToList();
    }

    public static bool IsCorrelatedChild(ImportCommand.SessionClassification c, ISet<string> planIds) =>
        ParentOf(c) is { } p && planIds.Contains(p);

    static string? ParentOf(ImportCommand.SessionClassification c) =>
        c.SourceMeta is { } meta
     && meta.TryGetValue("IsSubagentChild", out var isChild) && isChild is true
     && meta.TryGetValue("ParentSessionId", out var parent) ? parent as string : null;
}
