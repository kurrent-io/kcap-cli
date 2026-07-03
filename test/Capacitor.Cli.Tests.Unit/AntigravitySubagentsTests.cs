using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Antigravity;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AntigravitySubagents"/> (AI-1160): the child→parent map built
/// from the parent brain dir's <c>messages/*.json</c> linkage used to nest subagents at
/// import time.
/// </summary>
public class AntigravitySubagentsTests {
    static string NewHome() {
        var home = Path.Combine(Path.GetTempPath(), "kcap-agsub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(home);
        return home;
    }

    // Writes brain/<owner>/.system_generated/messages/<name>.json with sender→recipient.
    static void WriteMessage(string home, string owner, string name, string sender, string recipient) {
        var dir = Path.Combine(home, ".gemini", "antigravity", "brain", owner, ".system_generated", "messages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"),
            new JsonObject { ["sender"] = sender, ["recipient"] = recipient }.ToJsonString());
    }

    static void MakeBrainDir(string home, string convId) =>
        Directory.CreateDirectory(Path.Combine(home, ".gemini", "antigravity", "brain", convId, ".system_generated", "logs"));

    [Test]
    public async Task BuildParentMap_links_children_to_the_parent_that_owns_the_message() {
        var home = NewHome();
        try {
            // Parent P owns messages from two children C1, C2 (child = sender, parent = recipient).
            MakeBrainDir(home, "P"); MakeBrainDir(home, "C1"); MakeBrainDir(home, "C2");
            WriteMessage(home, owner: "P", name: "m1", sender: "C1", recipient: "P");
            WriteMessage(home, owner: "P", name: "m2", sender: "C2", recipient: "P");

            var map = AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "");

            await Assert.That(map.Count).IsEqualTo(2);
            await Assert.That(map["C1"]).IsEqualTo("P");
            await Assert.That(map["C2"]).IsEqualTo("P");
            // The root is never a sender, so it isn't a key.
            await Assert.That(map.ContainsKey("P")).IsFalse();

            await Assert.That(AntigravitySubagents.ResolveParent("C1", home: home, geminiCliHome: "")).IsEqualTo("P");
            await Assert.That(AntigravitySubagents.ResolveParent("P", home: home, geminiCliHome: "")).IsNull();
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_ignores_self_messages_and_malformed_files() {
        var home = NewHome();
        try {
            MakeBrainDir(home, "P");
            WriteMessage(home, owner: "P", name: "self", sender: "P", recipient: "P"); // self → ignored
            // malformed file
            var dir = Path.Combine(home, ".gemini", "antigravity", "brain", "P", ".system_generated", "messages");
            File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");

            var map = AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "");
            await Assert.That(map.Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }

    [Test]
    public async Task BuildParentMap_is_empty_when_no_antigravity_data() {
        var home = NewHome();
        try {
            await Assert.That(AntigravitySubagents.BuildParentMap(home: home, geminiCliHome: "").Count).IsEqualTo(0);
        } finally { Directory.Delete(home, recursive: true); }
    }
}
