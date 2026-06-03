using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Import;

/// <summary>
/// Unit tests for <see cref="ImportCommand.ComputePerSourceFinalCounts"/>,
/// the per-source attribution helper that feeds the Done sub-grid.
///
/// The regression this pins: routed-phase Skipped outcomes (e.g. Cursor's
/// "already current" watermark) must roll up into Excluded, not Errored.
/// </summary>
public class ImportDoneBreakdownTests {
    static ImportCommand.SessionClassification Cls(
            string                              sessionId,
            string                              vendor,
            ImportCommand.ClassificationStatus  status
        ) =>
        new() {
            SessionId  = sessionId,
            FilePath   = $"/tmp/{sessionId}.jsonl",
            EncodedCwd = "-tmp",
            Meta       = new SessionMetadata(),
            Status     = status,
            Vendor     = vendor,
        };

    // --- Routed-phase vendor (Cursor) ---

    [Test]
    public async Task routed_vendor_folds_skipped_into_excluded_not_errored() {
        // 3 sessions classified as New for Cursor; server returned:
        //   Loaded=1, Skipped=1, Failed=1
        // (Skipped = "already current" — must NOT be counted as Errored.)
        var classifications = new[] {
            Cls("c1", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c2", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c3", "cursor", ImportCommand.ClassificationStatus.New),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         1,
            routedOutcomes:   (Loaded: 1, Skipped: 1, Failed: 1));

        await Assert.That(counts.Loaded).IsEqualTo(1);
        await Assert.That(counts.Excluded).IsEqualTo(1);    // <-- routed Skipped lands here
        await Assert.That(counts.Errored).IsEqualTo(1);     // <-- ONLY routed Failed
    }

    [Test]
    public async Task routed_vendor_includes_classify_time_excluded_plus_routed_skipped() {
        // 2 classify-time Excluded + 1 routed Skipped → total Excluded == 3.
        var classifications = new[] {
            Cls("c1", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c2", "cursor", ImportCommand.ClassificationStatus.Excluded),
            Cls("c3", "cursor", ImportCommand.ClassificationStatus.Excluded),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         0,
            routedOutcomes:   (Loaded: 0, Skipped: 1, Failed: 0));

        await Assert.That(counts.Excluded).IsEqualTo(3);
        await Assert.That(counts.Errored).IsEqualTo(0);
    }

    [Test]
    public async Task routed_vendor_with_all_skipped_has_zero_errored() {
        // 5 sessions, all of which the server marked as "already current".
        // Pre-fix bug: sub-grid would show Errored=5. Post-fix: Errored=0.
        var classifications = Enumerable.Range(0, 5)
            .Select(i => Cls($"c{i}", "cursor", ImportCommand.ClassificationStatus.New))
            .ToArray();

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         0,
            routedOutcomes:   (Loaded: 0, Skipped: 5, Failed: 0));

        await Assert.That(counts.Errored).IsEqualTo(0);
        await Assert.That(counts.Excluded).IsEqualTo(5);
        await Assert.That(counts.Loaded).IsEqualTo(0);
    }

    // --- Chain-phase vendor (Claude / Codex) ---

    [Test]
    public async Task chain_vendor_uses_imported_count_for_loaded() {
        // No routed outcomes available — falls back to the chain approximation:
        // Loaded = imported; Errored = (New+Partial) - imported.
        var classifications = new[] {
            Cls("s1", "claude", ImportCommand.ClassificationStatus.New),
            Cls("s2", "claude", ImportCommand.ClassificationStatus.New),
            Cls("s3", "claude", ImportCommand.ClassificationStatus.Partial),
            Cls("s4", "claude", ImportCommand.ClassificationStatus.AlreadyLoaded),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         2,
            routedOutcomes:   null);

        await Assert.That(counts.Loaded).IsEqualTo(2);
        await Assert.That(counts.AlreadyLoaded).IsEqualTo(1);
        // (New=2 + Partial=1) - imported=2 = 1
        await Assert.That(counts.Errored).IsEqualTo(1);
        await Assert.That(counts.Excluded).IsEqualTo(0);
    }

    [Test]
    public async Task chain_vendor_with_all_imported_has_zero_errored() {
        var classifications = new[] {
            Cls("s1", "claude", ImportCommand.ClassificationStatus.New),
            Cls("s2", "claude", ImportCommand.ClassificationStatus.New),
        };

        var counts = ImportCommand.ComputePerSourceFinalCounts(
            classifications:  classifications,
            imported:         2,
            routedOutcomes:   null);

        await Assert.That(counts.Loaded).IsEqualTo(2);
        await Assert.That(counts.Errored).IsEqualTo(0);
    }

    // --- Multi-source invariant: sub-grid sum == totals ---

    [Test]
    public async Task multi_source_subgrid_sum_matches_aggregate_totals() {
        // Mixed run: Claude chain phase + Cursor routed phase.
        // Claude: 3 New, 2 imported, 1 errored.
        // Cursor: 4 New, 1 Loaded, 2 Skipped, 1 Failed.
        // Expected aggregate Excluded = 0 (Claude) + 2 (Cursor Skipped) = 2.
        // Expected aggregate Errored  = 1 (Claude) + 1 (Cursor Failed)  = 2.

        var claude = new[] {
            Cls("k1", "claude", ImportCommand.ClassificationStatus.New),
            Cls("k2", "claude", ImportCommand.ClassificationStatus.New),
            Cls("k3", "claude", ImportCommand.ClassificationStatus.New),
        };
        var cursor = new[] {
            Cls("c1", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c2", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c3", "cursor", ImportCommand.ClassificationStatus.New),
            Cls("c4", "cursor", ImportCommand.ClassificationStatus.New),
        };

        var claudeRow = ImportCommand.ComputePerSourceFinalCounts(claude, imported: 2, routedOutcomes: null);
        var cursorRow = ImportCommand.ComputePerSourceFinalCounts(cursor, imported: 1, routedOutcomes: (Loaded: 1, Skipped: 2, Failed: 1));

        await Assert.That(claudeRow.Loaded + cursorRow.Loaded).IsEqualTo(3);   // 2 + 1
        await Assert.That(claudeRow.Excluded + cursorRow.Excluded).IsEqualTo(2); // 0 + 2 (Skipped)
        await Assert.That(claudeRow.Errored + cursorRow.Errored).IsEqualTo(2);   // 1 + 1
    }
}
