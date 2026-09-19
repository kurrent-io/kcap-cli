namespace Capacitor.Cli.Commands;

/// <summary>Decided before any import work: whole chains newest first to the cap, then eligible
/// routed units; the boundary unit is taken whole.</summary>
internal static class ForegroundSelection {
    public static ForegroundPlan Select(
            List<List<ImportCommand.SessionClassification>> chains,
            List<ImportCommand.SessionClassification>       routed,
            IReadOnlyList<ImportCommand.SessionClassification> all,
            int                                              maxSessions) {
        var units   = RoutedUnits.Build(routed);
        var planIds = routed.Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);

        var selectedChains = new List<List<ImportCommand.SessionClassification>>();
        var count = 0;
        foreach (var chain in chains) {
            if (count >= maxSessions) break;
            selectedChains.Add(chain);
            count += chain.Count;
        }

        var selectedUnits = new List<RoutedUnit>();
        foreach (var unit in units.Where(u => u.Eligible)) {
            if (count >= maxSessions) break;
            selectedUnits.Add(unit);
            count++;
        }

        // The same session id can appear under several project/backup dirs (import tolerates it).
        // Publish each id once: duplicates would waste the handoff's 500-id cap and, because the
        // eval-watch cohort size is the list length while analytics returns one row per unique id,
        // keep the all-complete stop rule from ever firing.
        var selectedIds = selectedChains.SelectMany(c => c).Select(c => c.SessionId)
            .Concat(selectedUnits.Select(u => u.Parent.SessionId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var selectedSet = selectedIds.ToHashSet(StringComparer.Ordinal);

        var candidates = all
            .Where(c => c.Status is ImportCommand.ClassificationStatus.New
                                 or ImportCommand.ClassificationStatus.Partial
                                 or ImportCommand.ClassificationStatus.ProbeError)
            .Where(c => !RoutedUnits.IsCorrelatedChild(c, planIds))
            .OrderBy(c => c, ImportOrdering.Candidate)
            .Select(c => c.SessionId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var carried = selectedUnits.SelectMany(u => u.Children).Select(c => c.SessionId).ToHashSet(StringComparer.Ordinal);
        var remainder = all.Any(c =>
            (c.Status is ImportCommand.ClassificationStatus.New or ImportCommand.ClassificationStatus.Partial
                && !selectedSet.Contains(c.SessionId) && !carried.Contains(c.SessionId))
         || (c.Status is ImportCommand.ClassificationStatus.AlreadyLoaded && string.IsNullOrEmpty(c.FilePath)
                && !carried.Contains(c.SessionId))
         || c.Status is ImportCommand.ClassificationStatus.ProbeError);

        return new ForegroundPlan(
            selectedChains,
            [.. selectedUnits.SelectMany(u => u.Members)],
            new ImportRunSelection(candidates, selectedIds, remainder));
    }
}
