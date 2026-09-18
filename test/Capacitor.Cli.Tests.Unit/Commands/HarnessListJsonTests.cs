using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HarnessListJsonTests {
    static JsonElement Render(HarnessRegistry harnesses, HarnessOfferLedger? ledger = null) =>
        JsonDocument.Parse(HarnessListRender.Render(harnesses, ledger ?? new HarnessOfferLedger())).RootElement;

    static JsonElement Row(JsonElement root, string vendor) =>
        root.GetProperty("harnesses").EnumerateArray().Single(e => e.GetProperty("vendor").GetString() == vendor);

    [Test]
    public async Task Lists_every_known_harness_in_registry_order() {
        var root = Render(TestHarnesses.All());
        var rows = root.GetProperty("harnesses").EnumerateArray().ToList();

        await Assert.That(rows.Count).IsEqualTo(HarnessRegistry.Identities.Count);
        await Assert.That(rows.Select(r => r.GetProperty("vendor").GetString()!))
            .IsEquivalentTo(HarnessRegistry.Identities.Select(i => i.Id.VendorId), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Renders_snake_case_row() {
        var root = Render(TestHarnesses.All(detected: [HarnessId.Claude], wired: [HarnessId.Claude]));
        var row  = Row(root, HarnessId.Claude.VendorId);

        await Assert.That(row.GetProperty("label").GetString()).IsEqualTo(HarnessRegistry.LabelOf(HarnessId.Claude));
        await Assert.That(row.GetProperty("config_found").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("wired").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("dismissed").GetBoolean()).IsFalse();
    }

    // The signal a caller offering the user a choice needs to name, and the reason this payload does
    // not fold the two the way the nudge inventory does.
    [Test]
    public async Task Keeps_the_two_detection_signals_apart() {
        using var bin = new TempDir();
        var root = Render(HarnessRegistry.Over(
            TestBinaries.Searching(bin, "claude"),
            TestHarnesses.Probing(HarnessId.Claude, "claude"),
            TestHarnesses.Of(HarnessId.Cursor, detected: true)));

        await Assert.That(Row(root, "claude").GetProperty("binary_on_path").GetBoolean()).IsTrue();
        await Assert.That(Row(root, "claude").GetProperty("config_found").GetBoolean()).IsFalse();
        await Assert.That(Row(root, "cursor").GetProperty("binary_on_path").GetBoolean()).IsFalse();
        await Assert.That(Row(root, "cursor").GetProperty("config_found").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task An_absent_harness_is_listed_with_both_signals_false() {
        var row = Row(Render(TestHarnesses.All(detected: [HarnessId.Claude])), HarnessId.Codex.VendorId);

        await Assert.That(row.GetProperty("binary_on_path").GetBoolean()).IsFalse();
        await Assert.That(row.GetProperty("config_found").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Reports_a_dismissal_from_the_ledger() {
        var ledger = new HarnessOfferLedger().WithDismissed([HarnessId.Cursor], DateTimeOffset.UtcNow);
        var root   = Render(TestHarnesses.All(detected: [HarnessId.Cursor]), ledger);

        await Assert.That(Row(root, HarnessId.Cursor.VendorId).GetProperty("dismissed").GetBoolean()).IsTrue();
        await Assert.That(Row(root, HarnessId.Claude.VendorId).GetProperty("dismissed").GetBoolean()).IsFalse();
    }
}
