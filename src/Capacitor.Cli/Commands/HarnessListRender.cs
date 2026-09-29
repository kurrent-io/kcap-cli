using System.Text.Json;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Commands;

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
