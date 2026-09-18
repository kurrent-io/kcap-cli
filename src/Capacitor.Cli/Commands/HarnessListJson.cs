using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

/// <summary>One harness's row in <c>kcap harness list --json</c>.</summary>
/// <param name="Vendor">The stable key, and the id <c>kcap harness dismiss</c> and <c>reset</c>
/// take.</param>
/// <param name="Label">Display text. May be reworded; do not key off it.</param>
/// <param name="BinaryOnPath">The vendor's CLI resolves on the search path.</param>
/// <param name="ConfigFound">The vendor's own user-level data exists here.</param>
/// <param name="Wired">kcap's hook or extension is registered with the vendor.</param>
/// <param name="Dismissed">Its setup nudge was turned down locally.</param>
/// <remarks>
/// The two detection signals stay apart, as they do in <see cref="Capacitor.Cli.Core.FirstRun.FirstRunHarnessReport"/>:
/// a caller offering the user a choice can say which signal it saw, and one that only needs "is it
/// here" ORs them itself.
/// </remarks>
public sealed record HarnessListEntryJson(
    string Vendor, string Label, bool BinaryOnPath, bool ConfigFound, bool Wired, bool Dismissed);

/// <summary>Machine-readable payload for <c>kcap harness list --json</c>.</summary>
/// <remarks>
/// Every harness this build knows is listed, present or not, in the registry's own display order —
/// so a caller can tell "we do not support it" from "it is not on this machine" without carrying its
/// own vendor list.
/// </remarks>
public sealed record HarnessListJson(IReadOnlyList<HarnessListEntryJson> Harnesses);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HarnessListJson))]
public partial class HarnessListJsonContext : JsonSerializerContext;

/// <summary>Pure renderer for the list payload — kept separate from I/O so it's directly testable.</summary>
internal static class HarnessListRender {
    public static string Render(HarnessRegistry harnesses, HarnessOfferLedger ledger) {
        var entries = new List<HarnessListEntryJson>();

        foreach (var harness in harnesses) {
            var agent = harnesses.Detect(harness.Id);
            entries.Add(new HarnessListEntryJson(
                harness.VendorId,
                harness.Label,
                agent.BinaryFound,
                agent.InstallSignalFound,
                harness.Signals.IsWired,
                ledger.Entry(harness.Id) is { Declined: true }));
        }

        return JsonSerializer.Serialize(new HarnessListJson(entries), HarnessListJsonContext.Default.HarnessListJson);
    }
}
