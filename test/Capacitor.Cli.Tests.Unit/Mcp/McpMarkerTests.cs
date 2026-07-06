using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Mcp;

public class McpMarkerTests {
    static (McpMarker marker, string cfg, string markerFile) NewMarker() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-").FullName;
        var cfg = Path.Combine(dir, "mcp.json");
        var markerFile = Path.Combine(dir, "marker.json");
        return (new McpMarker("test", _ => markerFile), cfg, markerFile);
    }

    [Test]
    public async Task Record_then_Owned_roundtrips_names() {
        var (marker, cfg, _) = NewMarker();
        marker.Record(cfg, ["kcap-review", "kcap-sessions"]);
        await Assert.That(marker.Owned(cfg)).Contains("kcap-review");
        await Assert.That(marker.Owned(cfg)).Contains("kcap-sessions");
    }

    [Test]
    public async Task Owns_true_for_recorded_kcap_command_entry() {
        var (marker, cfg, _) = NewMarker();
        marker.Record(cfg, ["kcap-review"]);
        var entry = new JsonObject { ["command"] = "kcap", ["args"] = new JsonArray { "mcp", "review" } };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsTrue();
    }

    [Test]
    public async Task Owns_false_for_unrecorded_lookalike() {
        var (marker, cfg, _) = NewMarker();
        // Never recorded → not owned, even though it's named kcap-review.
        var entry = new JsonObject { ["command"] = "mine" };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsFalse();
    }

    [Test]
    public async Task Owns_true_for_opencode_command_array() {
        var (marker, cfg, _) = NewMarker();
        marker.Record(cfg, ["kcap-review"]);
        var entry = new JsonObject { ["command"] = new JsonArray { "kcap", "mcp", "review" } };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsTrue();
    }

    [Test]
    public async Task Clear_removes_the_marker() {
        var (marker, cfg, markerFile) = NewMarker();
        marker.Record(cfg, ["kcap-review"]);
        marker.Clear(cfg);
        await Assert.That(File.Exists(markerFile)).IsFalse();
        await Assert.That(marker.Owned(cfg)).IsEmpty();
    }
}
