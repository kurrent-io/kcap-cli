using System.Text.Json;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

public class SequencedSettlementWireTests {
    static readonly JsonSerializerOptions Opts = new() {
        TypeInfoResolver = CapacitorJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Test]
    public async Task StartupDiscovery_pins_exact_wire_tokens_and_defaults_to_pending() {
        var json = JsonSerializer.Serialize(new StartupDiscovery(MarkerScanState.Complete), Opts);
        await Assert.That(json).Contains("\"marker_scan_state\":\"complete\"");

        // The zero value (missing field) is the conservative default.
        await Assert.That(default(MarkerScanState)).IsEqualTo(MarkerScanState.Pending);
    }

    [Test]
    public async Task Resolved_and_unresolved_candidates_round_trip() {
        var resolved = new ResolvedStartupCandidate(7, "a1", "old-epoch", "flow-1", "reviewer");
        var rt = JsonSerializer.Deserialize<ResolvedStartupCandidate>(JsonSerializer.Serialize(resolved, Opts), Opts);
        await Assert.That(rt).IsEqualTo(resolved);

        var blockedJson = JsonSerializer.Serialize(
            new UnresolvedStartupCandidate("a2", StartupCandidateUnresolvedReason.PendingMarker), Opts);
        await Assert.That(blockedJson).Contains("\"reason\":\"pending_marker\"");
    }

    [Test]
    public async Task DaemonStatusReport_new_fields_round_trip_and_default_null() {
        // Old-shape construction still compiles (all new args optional).
        var old = new DaemonStatusReport(2, [], []);
        await Assert.That(old.Epoch).IsNull();
        await Assert.That(old.StartupDiscovery).IsNull();

        var full = new DaemonStatusReport(
            2, [], [], Epoch: "e1", LastProcessedSeq: 5, HighestAcceptedSeq: 6,
            StartupReapComplete: true, ResolvedStartupCandidates: [],
            UnresolvedStartupCandidates: [], StartupDiscovery: new StartupDiscovery(MarkerScanState.Complete));
        var rt = JsonSerializer.Deserialize<DaemonStatusReport>(JsonSerializer.Serialize(full, Opts), Opts);
        await Assert.That(rt.Epoch).IsEqualTo("e1");
        await Assert.That(rt.StartupReapComplete).IsTrue();
        await Assert.That(rt.StartupDiscovery!.Value.MarkerScanState).IsEqualTo(MarkerScanState.Complete);
    }
}
