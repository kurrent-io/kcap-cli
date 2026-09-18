using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RoutedUnitTests {
    internal static ImportCommand.SessionClassification Row(
            string id, ImportCommand.ClassificationStatus status, string? parent = null, string? ts = null) => new() {
        SessionId  = id,
        FilePath   = "",
        EncodedCwd = "",
        Meta       = new SessionMetadata { FirstTimestamp = ts is null ? null : DateTimeOffset.Parse(ts, System.Globalization.CultureInfo.InvariantCulture) },
        Status     = status,
        SourceMeta = parent is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?> { ["IsSubagentChild"] = true, ["ParentSessionId"] = parent },
    };

    [Test]
    public async Task A_child_whose_parent_is_routed_joins_the_parents_unit() {
        var units = RoutedUnits.Build([
            Row("p", ImportCommand.ClassificationStatus.New),
            Row("c", ImportCommand.ClassificationStatus.New, parent: "p"),
            Row("solo", ImportCommand.ClassificationStatus.New),
        ]);

        await Assert.That(units.Count).IsEqualTo(2);
        var p = units.Single(u => u.Parent.SessionId == "p");
        await Assert.That(p.Children.Select(c => c.SessionId).ToList()).IsEquivalentTo(["c"]);
        await Assert.That(p.Eligible).IsTrue();
    }

    [Test]
    public async Task An_orphan_is_its_own_unit() {
        var units = RoutedUnits.Build([Row("c", ImportCommand.ClassificationStatus.New, parent: "missing")]);

        await Assert.That(units.Count).IsEqualTo(1);
        await Assert.That(units[0].Parent.SessionId).IsEqualTo("c");
    }

    [Test]
    public async Task A_unit_with_an_already_loaded_parent_is_not_eligible_whatever_its_children() {
        var units = RoutedUnits.Build([
            Row("p", ImportCommand.ClassificationStatus.AlreadyLoaded),
            Row("c", ImportCommand.ClassificationStatus.New, parent: "p"),
        ]);

        await Assert.That(units.Single().Eligible).IsFalse();
    }
}
