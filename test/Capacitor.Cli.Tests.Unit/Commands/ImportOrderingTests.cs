using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportOrderingTests {
    static ImportCommand.SessionClassification Routed(string id, string? ts, ImportCommand.ClassificationStatus status = ImportCommand.ClassificationStatus.New) => new() {
        SessionId  = id,
        FilePath   = "",
        EncodedCwd = "",
        Meta       = new SessionMetadata { FirstTimestamp = ts is null ? null : DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture) },
        Status     = status,
    };

    [Test]
    public async Task Candidate_order_is_newest_first_with_unresolvable_timestamps_last() {
        var items = new List<ImportCommand.SessionClassification> {
            Routed("mid",   "2026-02-01T00:00:00Z"),
            Routed("none",  null),                    // no timestamp, no file → MinValue
            Routed("new",   "2026-03-01T00:00:00Z"),
            Routed("probe", "2026-02-15T00:00:00Z", ImportCommand.ClassificationStatus.ProbeError),
        };

        items.Sort(ImportOrdering.Candidate);

        await Assert.That(items.Select(c => c.SessionId).ToList()).IsEquivalentTo(["new", "probe", "mid", "none"]);
        await Assert.That(items[0].SessionId).IsEqualTo("new");
        await Assert.That(items[3].SessionId).IsEqualTo("none");
    }

    [Test]
    public async Task Candidate_order_breaks_equal_timestamps_by_session_id_descending() {
        var items = new List<ImportCommand.SessionClassification> {
            Routed("a", "2026-03-01T00:00:00Z"),
            Routed("b", "2026-03-01T00:00:00Z"),
        };

        items.Sort(ImportOrdering.Candidate);

        await Assert.That(items[0].SessionId).IsEqualTo("b");
    }

    [Test]
    public async Task Routed_dispatch_uses_the_same_rule_as_candidates() {
        var a = Routed("a", "2026-03-01T00:00:00Z");
        var b = Routed("b", "2026-01-01T00:00:00Z");

        await Assert.That(ImportOrdering.RoutedDispatch.Compare(a, b)).IsLessThan(0);
        await Assert.That(ImportOrdering.Candidate.Compare(a, b)).IsLessThan(0);
    }
}
