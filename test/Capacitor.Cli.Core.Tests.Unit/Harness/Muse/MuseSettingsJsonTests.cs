using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Muse;

public class MuseSettingsJsonTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Register_writes_stdio_optional_entries_keyed_by_name_and_keeps_schema_version() {
        var home = new UserHome(Tmp.Path);
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"model":"muse-spark-1.3"}""");

        await Assert.That(MuseSettingsJson.RegisterKcapMcpServers(path, home, () => "/opt/kcap")).IsEqualTo(JsonMcpConfigWriter.Change.Updated);

        var root  = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        var entry = (JsonObject)root["mcp_servers"]!["kcap-sessions"]!;
        await Assert.That(root["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(root["model"]!.GetValue<string>()).IsEqualTo("muse-spark-1.3");
        await Assert.That(entry["transport"]!.GetValue<string>()).IsEqualTo("stdio");
        await Assert.That(entry["mode"]!.GetValue<string>()).IsEqualTo("optional");
        await Assert.That(entry["command"]!.GetValue<string>()).IsEqualTo("/opt/kcap");
        await Assert.That(entry["args"]!.AsArray().Select(a => a!.GetValue<string>())).IsEquivalentTo(new[] { "mcp", "sessions" });
        await Assert.That(entry["cwd"]).IsNull();
        await Assert.That(root["mcp_servers"]!["kcap-flows"]!["args"]!.AsArray().Select(a => a!.GetValue<string>())).Contains("muse");
    }

    [Test]
    public async Task Register_adds_schema_version_to_an_existing_file_without_one_and_keeps_other_members() {
        var path = Tmp.CreateFile("settings.json", """{"model":"muse-spark-1.3","theme":"dark"}""");

        await Assert.That(MuseSettingsJson.RegisterKcapMcpServers(path, new UserHome(Tmp.Path))).IsEqualTo(JsonMcpConfigWriter.Change.Updated);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["schema_version"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(root["model"]!.GetValue<string>()).IsEqualTo("muse-spark-1.3");
        await Assert.That(root["theme"]!.GetValue<string>()).IsEqualTo("dark");
        await Assert.That(root["mcp_servers"]!["kcap-sessions"]).IsNotNull();
    }

    [Test]
    public async Task Register_on_a_missing_file_creates_schema_version() {
        var path = Tmp.PathTo("settings.json");
        await Assert.That(MuseSettingsJson.RegisterKcapMcpServers(path, new UserHome(Tmp.Path))).IsEqualTo(JsonMcpConfigWriter.Change.Updated);
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["schema_version"]!.GetValue<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Unregister_preserves_foreign_entries() {
        var home = new UserHome(Tmp.Path);
        var path = Tmp.CreateFile("settings.json", """{"schema_version":1,"mcp_servers":{"mine":{"transport":"stdio","command":"x"}}}""");
        MuseSettingsJson.RegisterKcapMcpServers(path, home);

        await Assert.That(MuseSettingsJson.UnregisterKcapMcpServers(path, home)).IsEqualTo(JsonMcpConfigWriter.Change.Updated);

        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        await Assert.That(root["mcp_servers"]!["mine"]).IsNotNull();
        await Assert.That(root["mcp_servers"]!["kcap-sessions"]).IsNull();
    }

    [Test]
    public async Task Register_refuses_a_malformed_file() {
        var path = Tmp.CreateFile("settings.json", "{not json");
        await Assert.That(MuseSettingsJson.RegisterKcapMcpServers(path, new UserHome(Tmp.Path))).IsEqualTo(JsonMcpConfigWriter.Change.Failed);
        await Assert.That(File.ReadAllText(path)).IsEqualTo("{not json");
    }
}
