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
    public async Task Owns_false_for_malformed_nonstring_command_array() {
        var (marker, cfg, _) = NewMarker();
        marker.Record(cfg, ["kcap-review"]);
        var entry = new JsonObject { ["command"] = new JsonArray { 123, "mcp" } };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsFalse(); // must not throw
    }

    [Test]
    public async Task Clear_removes_the_marker() {
        var (marker, cfg, markerFile) = NewMarker();
        marker.Record(cfg, ["kcap-review"]);
        marker.Clear(cfg);
        await Assert.That(File.Exists(markerFile)).IsFalse();
        await Assert.That(marker.Owned(cfg)).IsEmpty();
    }

    [Test]
    public async Task Owns_false_and_no_throw_for_nonobject_entry() {
        var (marker, cfg, _) = NewMarker();
        marker.Record(cfg, ["kcap-review"]);
        JsonNode arrEntry = new JsonArray { "x" };
        await Assert.That(marker.Owns(cfg, "kcap-review", arrEntry)).IsFalse();            // must not throw
        JsonNode valEntry = JsonValue.Create("disabled")!;
        await Assert.That(marker.Owns(cfg, "kcap-review", valEntry)).IsFalse();
    }

    [Test]
    public async Task Owned_ignores_marker_recorded_for_a_different_config() {
        var dir = Directory.CreateTempSubdirectory("kcap-marker-x-").FullName;
        var shared = Path.Combine(dir, ".kcap-mcp-version");
        var m = new McpMarker("test", _ => shared); // both configs resolve to the SAME sidecar (simulates per-dir collision)
        var cfgA = Path.Combine(dir, "a.json");
        var cfgB = Path.Combine(dir, "b.json");
        m.Record(cfgA, ["kcap-review"]);
        await Assert.That(m.Owned(cfgA)).Contains("kcap-review");   // A owns it
        await Assert.That(m.Owned(cfgB)).IsEmpty();                 // B must NOT inherit A's marker
        await Assert.That(m.Owns(cfgB, "kcap-review", new JsonObject { ["command"] = "kcap" })).IsFalse(); // → preserved
    }
}
